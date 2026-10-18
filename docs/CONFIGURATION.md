# Configuration (`config.json`)

`config.json` is created on first start from `config/config.default.json`. Edit it from the tray menu
(*Edit config*), then *Reload config* (or `HapticsGuard.exe --reload`). *Reset config to defaults…*
restores it and keeps the old file as `data\config.backup.json`.

```json
{
  "hotkeys": {
    "toggle": "Ctrl+Alt+R",
    "switchSource": "Ctrl+Alt+P",
    "rearmOnce": ""
  },
  "engine": {
    "apiUrl": "http://127.0.0.1:18757",
    "webOrigin": "https://space_station.flydigi.com",
    "statusSocketPort": 18758,
    "exePath": "C:\\Program Files\\Flydigi\\Engine\\xHaptics Engine.exe",
    "gameListPath": "%APPDATA%\\Flydigi\\xHaptics Engine\\config\\xhaptics_game_status.json"
  },
  "ui": {
    "url": "https://space_station.flydigi.com/#/controller/x-haptics",
    "browser": ""
  },
  "watchdog": { "...": "see below" },
  "log": {
    "maxFileKB": 5120
  },
  "sources": {
    "exclude": []
  }
}
```

## hotkeys

- Defaults `Ctrl+Alt+R` (master switch) and `Ctrl+Alt+P` (switch source), picked because few apps use
  them (`Ctrl+Alt+S`, for example, is a window zoom shortcut in some tools).
- Format: modifiers `Ctrl`, `Alt`, `Shift`, `Win` plus one key name from .NET `Keys`: `A`–`Z`, `0`–`9`,
  `F1`–`F24`, `Home`, `PageUp`, `OemPeriod` for `.`/`>`, `Oemcomma` for `,`/`<`, …
- An empty string disables the hotkey. If another app already owns a combination, a balloon and the log
  say so.

## engine

- **apiUrl**, **webOrigin**, **statusSocketPort**: how the guard reaches the engine.
- **exePath**: used for the tray icon, *Start xHaptics Engine*, and the "engine not installed" check.
- **gameListPath**: the engine's own list of games and process names. Read instead of
  `GET /api/v1/xhaptics/games`, which makes the engine rescan and drop to Mix for ~2 s.

## ui

- **url**: the page *Open X-Haptics settings* opens.
- **browser**: empty = Chrome app window if Chrome is installed, otherwise the default browser; or a full
  path to a browser exe.

## log

- **maxFileKB** (default 5120 = 5 MB, minimum 64): when `data\guard.log` grows past this, it becomes
  `guard.log.old` (replacing the previous one) and a new `guard.log` starts, so the two files together
  stay under about twice this value (10 MB by default). Normal use writes roughly 0.1–0.5 MB a month.

## sources

- **exclude**: process names never picked by the switch-source cycle. The *Audio sources* menu keeps
  this list up to date (it rewrites only this key, keeping the rest of the file).

## watchdog

| Key | Default | Meaning |
|---|---|---|
| `pollMs` | 1000 | status poll interval |
| `reconnectDelaySec` | 4 | re-arm this long after the controller connects (skipped if the engine already recovered) |
| `deviceChangeDelaySec` | 2 | re-arm this long after the default output device changes |
| `brokenPersistSec` | 4 | how long "stopped/error" must last before a re-arm |
| `rearmGraceSec` | 10 | ignore "stopped" this long after any re-arm or source switch |
| `stallSec`, `stallAudioSec` | 5, 3 | capture frozen this long while audio plays this long |
| `audioPeakThreshold` | 0.02 | system meter level that counts as "audio is playing" |
| `loudPeakThreshold`, `engineSilentPeak`, `silentMismatchKick` | 0.05, 0.0005, 6 | "loud system audio but the engine hears silence" detector |
| `healthyResetSec` | 30 | reset the back-off after this long healthy |
| `maxIdleBounces` | 2 | re-arms undone by someone else before the guard stops retrying |
| `webCheckDelaySec` | 5 | after a manual re-arm, check whether the Space Station page paused it |
| `backoffSec` | [5,10,20,30,60] | wait between automatic re-arms |
| `followChildAudio` | true | follow audio played by a child process of the captured game |
| `followDetectPolls` | 3 | the child must play this many polls in a row before following |
| `followReturnSilenceSec` | 15 | child silent this long → back to the game |
| `controllerNotifyDelaySec` | 5 | a controller state must last this long before its balloon |
| `lowBatteryPercent` | 10 | one balloon when the battery drops to this; 0 = off. The engine reports the battery in 10 % steps (0–10); no balloon while charging |

> ⚠️ Lower watchdog values react faster but can make the guard re-arm an engine that is still starting,
> which leads to restart loops (haptics flicker and the Flydigi popup keeps appearing). See
> [HOW-IT-WORKS.md › Delays](HOW-IT-WORKS.md#delays).
