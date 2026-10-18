# Testing X-Haptics Guard

Manual test cases and what was observed while building the guard. They need real hardware; the logic
that does not (status parsing, config, hotkeys, process tree, balloon texts, watchdog rules, state files, log rotation)
is covered by the unit tests: run `test.cmd` (see [DEVELOPMENT.md](DEVELOPMENT.md#unit-tests)). Every
manual case can be checked from the tray menu status lines and `data\guard.log` (tray › *View log*).

**Test setup:** Flydigi APEX 6 Pro *Phantom Blade Zero* edition with its charging dock, xHaptics Engine
5.0.2.4 and 5.0.2.6, Windows 11. Tip: set *Real-time audio auto sleep* to 20 s in Space Station to test the sleep
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
| 2.1 | Change the Windows default output device | `default output changed`, then about 2 s later `RE-ARMED … default output device changed`, or, with engine 5.0.2.4 while audio plays, `skip re-arm (default output device changed): the engine already follows the new device` |
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
- `batteryLevel` is the controller's raw battery byte, passed on undecoded. Space Station splits it: the low
  4 bits are the level 0–10 (shown as `level/10`), the high 4 bits the charge state (1 charging, 2 full).
  Examples: `6` = 60 %, `0x15` (21) = 50 % and charging. The engine's own `chargingStatus` field stays 0.
- The level matches the controller screen and is refreshed slowly (6 → 5 → 4 over ~2 h of play, no
  reconnect needed). The charging flag only shows up for a few minutes after charging starts (`0x14`,
  `0x13`, `0x15`, each followed by a plain value a few minutes later while still on the dock), so "not
  charging" in the tray does not mean the controller is off the charger.

### Engine capture behaviour

Measured with the guard closed:

- It follows a change of the Windows default playback device by itself (no gap in capture).
- It does **not** recover after an exclusive-mode player held the device (`0x88890004`), not even after the
  device is released or changed; it stays Idle until something re-arms it.
- It does not hear a game's child process either (BH3 → ZFGameBrowser web events: engine 0.00 while the
  child peaked 0.07–0.24).
- It keeps the bound game when another window is focused, returns to Mix ~3 s after the game exits, and
  binds a catalog game with an enabled profile by itself (Zenless Zone Zero).
- The engine process now has its own audio session that mirrors what it captures.
- `runState` / `foreground` in its games file are not live (BH3 stayed `running` after exiting).

PID reuse: Windows keeps a process's parent PID after the parent exits and later gives that PID to a new
process. Chrome, started at sign-in by a launcher that had exited, showed up as a "child" of BH3 and the
guard followed it. The guard now ignores a parent that started after its child.

Found on earlier engines; the guard is still built around them:

- The engine binds a per-app profile only when it is **enabled while the engine sits on Mix**, so a source
  switch disables the running profiles, waits 1.2 s and enables the target.
- `GET /api/v1/xhaptics/games` makes the engine rescan and drop to Mix for ~2 s, so the guard reads the
  engine's games file instead of polling it.
- After any re-arm the grip "activity gate" is closed until the next controller input.
- Some games mute themselves when unfocused, so their capture is silent in the background.

### Antivirus scans

| Build | Windows Defender | VirusTotal |
|---|---|---|
| v1.0 (no version resource / manifest) | clean | 12/75, heuristic and ML verdicts incl. Kaspersky `VHO:Backdoor.MSIL.Convagent.gen`, McAfee, CrowdStrike |
| v1.0.1 (version resource + `asInvoker` manifest) | clean | [4/71](https://www.virustotal.com/gui/file/1f938d7d069fa5260da5018c7fa56245361fc50408141c5764ed333d68b7a042): Bkav, Elastic, SecureAge APEX, Sophos (generic ML/PUA) |
| 1.0.2 test build | clean | 5/70: the same four plus Kingsoft |
| v1.0.2 (modular sources) | clean | [5/69](https://www.virustotal.com/gui/file/23563875530c5f8794c28058c487a36a349a22ec7aab574d766ec6477e70b289): Bkav, Elastic, SecureAge APEX, Sophos, Trapmine (generic ML/PUA); Microsoft, Kaspersky, ESET, BitDefender, Avast, Malwarebytes, Symantec, McAfee, CrowdStrike clean |
| v1.0.3 | clean | [4/67](https://www.virustotal.com/gui/file/b49af9453331bfcc9469d32d8d78d635f858b9c6e7d6fb1ee01c7501e5daa37c): Bkav, Elastic, SecureAge APEX, Trapmine (generic ML); major engines clean |
| v1.0.4 (refactor, unit tests) | clean | [5/71](https://www.virustotal.com/gui/file/80d9dc7d13efe2704b58f42c2b351c1f121bb7048a9df91c94b010a66903edc5): APEX, Bkav, Elastic, MaxSecure, Trapmine (generic ML/heuristic); Microsoft, Kaspersky, ESET, BitDefender, Avast, Malwarebytes, Symantec, CrowdStrike, Sophos clean |
| v1.0.5 (battery level fix) | clean | [5/71](https://www.virustotal.com/gui/file/f4006921fcd4de0043bcfdbf78b9828d23a5c93af7e27e61c62e131a1479a6a1): the same five as v1.0.4 (APEX, Bkav, Elastic, MaxSecure, Trapmine); major engines clean |
| v1.0.6 (engine 5.0.2.4 support) | clean | [5/68](https://www.virustotal.com/gui/file/691f900613e25ce12ea5777c1663f4c31b1b7202264b0dc0c855f4f5096ec1b9): the same five as v1.0.4 (APEX, Bkav, Elastic, MaxSecure, Trapmine); major engines clean |
| v1.0.7 (battery byte decoding) | clean | [4/70](https://www.virustotal.com/gui/file/b26317c1e044782f28e684cd83bdf22e44df55932c1f2338fa26cc605f685f05): APEX, Bkav, Elastic, Trapmine (generic ML); major engines clean |

No engine names a malware family; the hits are generic ML/heuristic verdicts typical for small unsigned
tools that register global hotkeys and enumerate processes.
