# How X-Haptics Guard works

See also [DIAGRAM.md](../DIAGRAM.md) for diagrams and [TESTING.md](TESTING.md) for what was measured.

## The engine

The xHaptics Engine (`C:\Program Files\Flydigi\Engine\xHaptics Engine.exe`) runs a local HTTP API on
`127.0.0.1:18757` and a status WebSocket on `:18758`. The Space Station web page
(`space_station.flydigi.com`) talks to that API. The engine:

1. captures audio with WASAPI loopback: the whole system mix by default, or a single process
   (process-loopback) while an enabled game/app profile is running,
2. turns it into grip/trigger motor frames,
3. sends them to the controller over HID.

The "toggle X-Haptics" switch is `enabled: false` then `true` on a profile, which rebuilds the capture
pipeline ("re-arm"). The guard authenticates the way the Space Station page does (an `Origin` header to
`GET /api/engine/version` returns an access token and the engine's capabilities) and then:

| Profile | How the guard switches it |
|---|---|
| `global` | `GET` then `PUT /api/v1/xhaptics/realtime-audio/configurations/global` with `{expectedRevision, config}`: the complete config, only `enabled` changed, so the Grip/Trigger choices made in Space Station stay as they are |
| a game/app | `PATCH /api/v1/xhaptics/configurations/{gameId}` with `{expectedRevision, enabled, selectedBackend, ui}` |

The enabled flags of all profiles are read from `GET /api/v1/xhaptics/configurations`. The log line
`monitoring: engine=…` shows which engine version the guard is talking to.

The API is undocumented and changes with Flydigi updates. This version needs engine 5.0.2.4 or newer;
use v1.0.5 with older engines.

## Master switch

- **OFF**: disables the running profile and `global`, remembers which ones in `data\auto-mode.txt`, and
  stops the watchdog.
- **ON**: re-enables exactly those profiles (or re-arms if they are already on) and starts the watchdog.
- The real state is the engine's `global` profile, which Space Station or an engine update can change too:
  - at start the guard takes the engine's state (`data\auto-mode.txt` only while the engine cannot be
    reached), logged as `switch ON: X-Haptics is on in the engine (at start)`;
  - a press switches to the opposite of the engine's current state, so one press is always enough;
  - every 3 s it compares again and follows a change made elsewhere, with one balloon. It ignores readings
    taken during its own re-arms and source switches, and a change must be seen twice in a row.

## Watchdog (while ON)

X-Haptics is re-armed when:

- the controller (re)connects, unless the engine already recovered by itself;
- the Windows default playback device changes, unless the engine already follows the new device (it
  usually does: while audio plays the guard sees the engine hear it and skips the re-arm);
- the engine reports that capture or output stopped, or an error (for example after a player released
  WASAPI exclusive mode);
- audio is playing but the engine's system capture is frozen or only hears silence;
- a per-app capture is still bound to a process that has exited.

It never fights the Space Station page: while that page is open and holds the controller, the engine
pauses X-Haptics on purpose (session Idle, status WebSocket connected) and the guard leaves it alone.
Re-arms that the engine undoes straight away are retried at most `maxIdleBounces` times.

Audio-level checks and child-process scanning are skipped while the controller is not connected or its
haptics are asleep.

### Exclusive mode and ASIO

The engine captures with WASAPI loopback, i.e. the Windows mixer output of the default device (Mix) or
of one process. WASAPI-exclusive, ASIO and kernel-streaming output (TIDAL *Exclusive Mode*, foobar2000
*WASAPI (exclusive)* / *ASIO* devices…) bypass the mixer, so the capture is silent and the device can
be locked for other apps. Nothing in the guard can change that.

The engine's capture then fails (`GetNextPacketSize failed: HRESULT 0x88890004`
AUDCLNT_E_DEVICE_INVALIDATED, later `IAudioClient::Initialize(loopback) failed: HRESULT 0x8889000a`
AUDCLNT_E_DEVICE_IN_USE) and stays Idle. When the "stopped / error" rule fires, the guard first probes the
default playback device by opening a shared stream (never started) and closing it:

- `AUDCLNT_E_DEVICE_IN_USE` → the device is held: no re-arm, `Source: audio device in exclusive use…`,
  one balloon, probe again every 2 s;
- the probe succeeds again → re-arm once (back-off reset), so haptics come back ~2 s after release.

### Delays

| Event | Waits before re-arming |
|---|---|
| Controller connected | 4 s (`reconnectDelaySec`) |
| Default output device changed | 2 s (`deviceChangeDelaySec`) |
| Engine reports stopped / error | 4 s (`brokenPersistSec`) |
| Audio playing but capture frozen | 5 s (`stallSec`) |
| Any re-arm or source switch | nothing is judged for 10 s (`rearmGraceSec`) |
| Repeated automatic re-arms | back-off 5 → 10 → 20 → 30 → 60 s (`backoffSec`) |

A re-arm is *disable → 0.6 s → enable* and the engine then rebuilds its capture: about 1–2 s without
vibration and a Flydigi "configuration applied" popup. Switching the source to an app adds 1.2 s.

Values that are too short (for example `rearmGraceSec` < 5, `brokenPersistSec` < 2, `pollMs` < 500) make
the guard re-arm an engine that is still starting, which leads to restart loops.

### Sleep state

The engine's *Real-time audio auto sleep* stops vibration after a period without controller input
(`gripActivityGateOpen = false`, no motor frames). It wakes on the next controller input. After any
re-arm it starts asleep, and the back-button hotkeys (keyboard input) do not wake it. The guard never
treats sleep as a fault.

## Audio source switching

The engine has no "capture this process" call. It captures one process while an **enabled** profile is
running and the whole mix when none is. The switch-source hotkey therefore:

1. disables every running profile (the engine drops to Mix),
2. waits 1.2 s and enables the target, because the engine only binds a profile that is enabled while it
   sits on Mix.

Targets are registered, running, non-excluded apps/games. The original `enabled` flags are saved in
`data\sources.txt` and restored once those processes have all exited. An excluded profile is never a
target but is still switched off.

The *Audio sources* menu lists processes with an audio session on any playback device (what the Windows
volume mixer shows) plus running registered games, nests children under their parent app, registers new
apps as disabled custom games (`POST /api/v1/xhaptics/games/custom`) and edits `sources.exclude`.

The guard reads the engine's games file (`engine.gameListPath`) instead of `GET /api/v1/xhaptics/games`,
because that call makes the engine rescan and drop to Mix for about 2 s.

### Multi-process apps

The engine captures the process tree of the process that owns the app's foreground window, otherwise a
process it matched by name (for Chrome usually a renderer that plays nothing). After a switch, if the
captured tree plays nothing while another process of the same app does, the guard shows a balloon.
Browsers work as a per-app source only while their window is focused; use Mix for background audio.

### Follow child audio

While a game is the source (`followChildAudio`, default on):

1. **Detect**: the engine hears silence while a descendant process of the game plays audio, for
   `followDetectPolls` polls in a row.
2. **Follow**: register the child if needed, switch the game's profile off and the child's on. If 4 s
   later the engine is bound to a process of the child that plays nothing, switch to Mix instead.
3. **Return**: child closed or silent for `followReturnSilenceSec` → back to the game; game closed → Mix.

The switch-source hotkey and the master switch cancel following.

## Controller state

The tray menu shows `connected` / `not connected` / `haptics asleep`, and one balloon is shown per state
change once the state has lasted `controllerNotifyDelaySec`:

| Change | Balloon |
|---|---|
| start, not connected | Controller not connected. Turn it on (Home) or put it on the cable/dock. |
| → disconnected | Controller disconnected (turned off, out of range, or taken off the cable/dock). Press Home to reconnect. |
| disconnected → connected | Controller connected. (+ "press any controller button to wake haptics" when it comes back asleep) |
| connected → asleep | Haptics asleep… (not right after a re-arm) |
| asleep → awake, or already connected at start | none |
| battery ≤ `lowBatteryPercent` | Controller battery low (once, until it goes back above +5 %) |

## Files and locations

`config.json` and `data\` (log, `auto-mode.txt`, `sources.txt`) live next to the exe when that folder is
writable, otherwise in `%LOCALAPPDATA%\FlydigiHapticsGuard\`. The log rotates to `guard.log.old` at
`log.maxFileKB` (5 MB), so both files stay under about 10 MB. The shipped defaults are read from
`config\config.default.json` next to the exe, or from the copy built into the exe.

*Start with Windows* writes `X-Haptics Guard` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
