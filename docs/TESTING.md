# Testing X-Haptics Guard

Manual test cases and what was observed while building the guard. They need real hardware; the logic
that does not (status parsing, config, hotkeys, process tree, balloon texts, watchdog rules, state files, log rotation)
is covered by the unit tests: run `test.cmd` (see [DEVELOPMENT.md](DEVELOPMENT.md#unit-tests)). Every
manual case can be checked from the tray menu status lines and `data\guard.log` (tray › *View log*).

**Test setup:** Flydigi APEX 6 Pro *Phantom Blade Zero* edition with its charging dock, xHaptics Engine
5.0.1.6, Windows 11. Tip: set *Real-time audio auto sleep* to 20 s in Space Station to test the sleep
cases quickly (the allowed range is 5–1800 s, default 900), and put it back afterwards.

Start the guard **from Explorer**, and close the Space Station page before each test: while that page
is open it holds the controller and X-Haptics is paused.

---

## 1. Master switch

| # | Steps | Expected |
|---|---|---|
| 1.1 | Press the toggle hotkey (M2) while ON | Balloon *X-Haptics OFF…*; log `switch OFF`, `X-Haptics turned OFF (scopes: …)`; no automatic re-arm afterwards |
| 1.2 | Press it again | Balloon *X-Haptics ON…*; log `switch ON`, `X-Haptics turned ON (scopes: …)`, `monitoring: …` |
| 1.3 | Restart the guard while OFF | Stays OFF (`guard … started, switch OFF`) |
| 1.4 | Press M2 while the engine is stopped | Balloon says the engine is not running; the switch keeps its previous state (`switch failed`) |
| 1.5 | Turn X-Haptics off in Space Station while ON | One balloon *X-Haptics was turned off in Space Station: press … twice*; `Source:` line says so; the guard does not turn it back on |

## 2. Watchdog

| # | Steps | Expected |
|---|---|---|
| 2.1 | Change the Windows default output device | `default output changed`, then `RE-ARMED … default output device changed` about 2 s later |
| 2.2 | Open the Space Station mapping page | `engine idle while Space Station page is attached -> not interfering`; no re-arm loop |
| 2.3 | Re-arm once (tray) with the page open | Balloon *The Space Station page is holding the controller…* |
| 2.4 | Select a game as the source, then close the game | `game exited but the engine is still bound to its process`, re-arm, `Source:` back to Mix |
| 2.5 | Play in TIDAL with *Exclusive Mode* on (or foobar2000 *WASAPI (exclusive)* / *ASIO*) to the default DAC | One balloon *Another app is using the audio device in exclusive mode…*; log `output device is in exclusive use by another app (…)`; `Source: audio device in exclusive use…`; no re-arm attempts |
| 2.6 | Stop / pause it, or turn *Exclusive Mode* off | ~2 s later `output device released by the exclusive-mode app -> re-arm`, `RE-ARMED …`; haptics back after a controller button press |

## 3. Audio sources

| # | Steps | Expected |
|---|---|---|
| 3.1 | Press the switch-source hotkey (M4) several times | `audio source -> …` cycling Mix → each checked running app/game → Mix |
| 3.2 | Tray › *Audio sources*, check an app marked *not registered* | `registered … as custom game … (disabled)`, `audio source … included` |
| 3.3 | Uncheck an app | `audio source … excluded`; `sources.exclude` updated in `config.json` |
| 3.4 | Child process listed under its parent | e.g. `↳ ZFGameBrowser [ZFGameBrowser.exe] (in BH3)` |
| 3.5 | Select a browser (Chrome) as the source | Silent unless the browser window is focused; log `… plays no audio (multi-process app; …)` |
| 3.6 | Exit every app whose profile was changed | `audio source overrides restored (…)` |

## 4. Follow child audio (BH3 → ZFGameBrowser)

| # | Steps | Expected |
|---|---|---|
| 4.1 | Source = BH3, open a web event page with music | `child audio: ZFGameBrowser (PID …) under BH3 plays while the engine hears nothing -> following it`, then usually `engine bound PID … of ZFGameBrowser, which plays nothing -> Mix` |
| 4.2 | Close the event page | ~15 s later `stop following (ZFGameBrowser silent for 15s) -> BH3` |
| 4.3 | Quit BH3 while following | `stop following (BH3 closed) -> Mix (all apps)` |
| 4.4 | Press M4 while following | `manual source switch: stop following …` |

## 5. Controller, cable and dock

Wait about 10 s per step (state must last `controllerNotifyDelaySec`, plus the engine's own reaction).

| # | Steps | Expected balloon (exactly one) | Log |
|---|---|---|---|
| 5.1 | Start the guard with the controller on | none (battery-low balloon if ≤ 10 %) | `controller state: start -> connected` |
| 5.2 | Turn the controller off | *Controller disconnected (… taken off the cable/dock). Press Home to reconnect.* | `connected -> disconnected` |
| 5.3 | Put it on the dock / cable while off | may come up by itself after ~4 s → *Controller connected…* | `disconnected -> asleep` or `-> connected` |
| 5.4 | Take it off the dock while on | *Controller disconnected…* (the engine needs ~9 s to notice) | `-> disconnected` |
| 5.5 | Press Home | *Controller connected. Press any controller button to wake haptics.* | `disconnected -> asleep`, `skip re-arm (controller connected): the engine already recovered on its own` |
| 5.6 | Press a button | none | `asleep -> connected` |
| 5.7 | Leave it untouched past the auto-sleep time | *Haptics asleep…* | `connected -> asleep` |
| 5.8 | Start the PC/guard with the controller off | *Controller not connected. Turn it on (Home)…* | `start -> disconnected` |

## 6. Configuration and files

| # | Steps | Expected |
|---|---|---|
| 6.1 | Break `config.json` (invalid JSON), *Reload config* | One-line `config.json error`, previous settings kept |
| 6.2 | *Reset config to defaults…* | Old file saved as `data\config.backup.json`, defaults loaded |
| 6.3 | Unknown key name in a hotkey (`Ctrl+Alt+Banana`) | `hotkey warning: cannot parse hotkey …`, other hotkeys still work |
| 6.4 | Run only `HapticsGuard.exe` in an empty folder | Creates `config.json` (identical to `config/config.default.json`) and `data\guard.log` |
| 6.5 | Run it from `C:\Program Files\…` (not as admin) | `exe folder … is not writable; config and data are in %LOCALAPPDATA%\FlydigiHapticsGuard`; nothing written next to the exe |
| 6.6 | *Start with Windows* on/off | `HKCU\…\Run\X-Haptics Guard` added/removed (check in Task Manager › Startup apps) |

---

## Observations

### Controller power, cable and dock

| What you do | What the engine reports |
|---|---|
| Turn the controller off | 3 HID write errors (`WriteFile HID failed … error=433`), then `connected=false`, session Idle |
| Put it on the cable/dock while off | Comes up and reconnects within ~4 s (not every time on the dock) |
| Take it off the dock/cable while on | Still `connected` for ~5 s, `opened=false`, then `connected=false` after ~9 s; does not switch to wireless by itself |
| Unplug the cable after it is on | No change |

- The dock carries USB data like the cable.
- Windows cannot tell cable/dock from wireless: the controller's USB device `VID_37D7&PID_2502\FLYDIGI_APEX_6`
  is also presented by the wireless receiver (`VID_37D7&PID_6001`) while the controller is linked.
- `chargingStatus` stayed 0 while charging. `connectionMode` and `batteryLevel` differ between links
  (`mode=0`, 22 % over USB vs `mode=1`, 8 % wireless at the same moment).

### Engine capture behaviour

- The engine binds a per-app profile only when it is **enabled while the engine sits on Mix**. Enabling a
  second profile while one is active does nothing; disabling the active one drops to Mix.
- Per-app capture includes the captured process's children, and the engine prefers the process that owns
  the **foreground window**. Chrome: main browser process (focused) → audio heard; a renderer
  (unfocused) → silence. Re-arming does not help.
- BH3's embedded browser `ZFGameBrowser.exe` (BH3 → ZFGameBrowser → ZFGameBrowser) is **not** heard by the
  BH3 capture (engine 0.000 while ZFGameBrowser peaked 0.09–0.23); Mix hears it.
- Some games mute themselves when unfocused, so their capture is silent in the background.
- `GET /api/v1/xhaptics/games` makes the engine rescan and drop to Mix for ~2 s. Polling it every 5 s
  made the engine flap between the game and Mix (session revision ~500); the guard reads the engine's
  games file instead.
- After any re-arm the grip "activity gate" is closed until the next controller input.
- The status reported a queue delay of about 6–32 ms.

### Antivirus scans

| Build | Windows Defender | VirusTotal |
|---|---|---|
| v1.0 (no version resource / manifest) | clean | 12/75, heuristic and ML verdicts incl. Kaspersky `VHO:Backdoor.MSIL.Convagent.gen`, McAfee, CrowdStrike |
| v1.0.1 (version resource + `asInvoker` manifest) | clean | [4/71](https://www.virustotal.com/gui/file/1f938d7d069fa5260da5018c7fa56245361fc50408141c5764ed333d68b7a042): Bkav, Elastic, SecureAge APEX, Sophos (generic ML/PUA) |
| 1.0.2 test build | clean | 5/70: the same four plus Kingsoft |
| v1.0.3 | clean | [4/67](https://www.virustotal.com/gui/file/b49af9453331bfcc9469d32d8d78d635f858b9c6e7d6fb1ee01c7501e5daa37c): Bkav, Elastic, SecureAge APEX, Trapmine (generic ML); major engines clean |
| v1.0.2 (modular sources) | clean | [5/69](https://www.virustotal.com/gui/file/23563875530c5f8794c28058c487a36a349a22ec7aab574d766ec6477e70b289): Bkav, Elastic, SecureAge APEX, Sophos, Trapmine (generic ML/PUA); Microsoft, Kaspersky, ESET, BitDefender, Avast, Malwarebytes, Symantec, McAfee, CrowdStrike clean |

No engine names a malware family; the hits are generic ML/heuristic verdicts typical for small unsigned
tools that register global hotkeys and enumerate processes.
