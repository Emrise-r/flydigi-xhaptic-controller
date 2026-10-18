# X-Haptics Guard

A small Windows tray tool for the **Flydigi APEX 6 Pro** (and other controllers driven by the Flydigi
*xHaptics Engine*). It fixes the "X-Haptics stopped vibrating, go to the web page and toggle it" problem
and lets you control X-Haptics from the controller's back buttons:

| Default hotkey | Suggested button | What it does |
|---|---|---|
| `Ctrl+Alt+R` | M2 | **Master switch.** OFF = X-Haptics disabled and nothing turns it back on. ON = X-Haptics enabled and the watchdog keeps it alive. |
| `Ctrl+Alt+P` | M4 | **Switch audio source.** Mix (all apps) → app/game 1 → app/game 2 → … → Mix. |
| *(none)* | – | Re-arm X-Haptics once (disable → enable) without changing the master switch. |

![Tray menu with the Audio sources submenu](docs/images/tray-menu.png)

**Download:** [latest release](https://github.com/Emrise-r/flydigi-xhaptic-controller/releases/latest)
([v1.0.8](https://github.com/Emrise-r/flydigi-xhaptic-controller/releases/tag/v1.0.8)). Get
`HapticsGuard-v1.0.8.zip`, extract it anywhere and run `HapticsGuard.exe`.

> **Requires the Flydigi xHaptics Engine.** This tool only drives the engine's local API; it does not
> make the controller vibrate by itself. Install the engine with Flydigi Space Station first.
>
> **Tested on:** Flydigi APEX 6 Pro *Phantom Blade Zero* edition, xHaptics Engine 5.0.2.4 and 5.0.2.6, Windows 11.
> Other Flydigi controllers that use the same engine may work but are untested.
>
> **Engine version:** v1.0.6 and later are made for xHaptics Engine **5.0.2.4 or newer** (the engine changed
> how X-Haptics is switched on and off). With an older engine use
> [v1.0.5](https://github.com/Emrise-r/flydigi-xhaptic-controller/releases/tag/v1.0.5). Engine 5.0.2.4 also
> forgot the apps added as audio sources: tick them again in *Audio sources*.

### Documentation

| | |
|---|---|
| [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) | engine API, watchdog rules and delays, source switching, follow child audio, controller states |
| [docs/CONFIGURATION.md](docs/CONFIGURATION.md) | every `config.json` setting |
| [docs/TESTING.md](docs/TESTING.md) | manual test cases and what was measured (controller/dock, Chrome, BH3, antivirus scans) |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | build, source layout, files, release checklist |
| [DIAGRAM.md](DIAGRAM.md) | diagrams |

---

## Requirements

- **Flydigi xHaptics Engine**, installed and running (its installer starts it with Windows). If it is
  missing or stopped, the guard says so when you press a hotkey, and the tray menu offers
  *Start xHaptics Engine*.
- 64-bit Windows, because the xHaptics Engine is x64 only. Tested on Windows 11.
- .NET Framework 4.5 or later. Windows 10 and 11 already include it (4.8), so there is nothing to install.

## Install

Only `HapticsGuard.exe` is needed. On first start it creates `config.json` and `data\` (log and state)
next to the exe, or in `%LOCALAPPDATA%\FlydigiHapticsGuard\` when that folder is not writable.

| Put it in | |
|---|---|
| `%LOCALAPPDATA%\Programs\X-Haptics Guard\` | recommended per-user location, no admin needed |
| any folder you created, Desktop, Downloads | fine |
| `C:\Program Files\…` | works; settings and log go to `%LOCALAPPDATA%\FlydigiHapticsGuard\`. Do not run it as administrator |

The guard does **not** start with Windows by itself: turn **Start with Windows** on in the tray menu.

To build it yourself: run `build.cmd` (uses the C# compiler that ships with Windows), see
[DEVELOPMENT.md](docs/DEVELOPMENT.md).

**Antivirus:** the exe is small, unsigned, registers global hotkeys and lists processes, so a few engines
flag it with generic machine-learning verdicts (v1.0.8: Defender clean,
[VirusTotal](https://www.virustotal.com/gui/file/e60e931fdcad12901b2e623c1bdaad3bf47b25b62702bf14e5a38c122bff64a4)
4/68, major engines clean). Build it from source if you prefer.

---

## Mapping the APEX back buttons (M1–M4) to the hotkeys

Keyboard output is only available in **Advanced** mapping mode.

| Back button | Hotkey (default) | Action |
|---|---|---|
| M2 | `Ctrl+Alt+R` | master switch (X-Haptics on/off + watchdog) |
| M4 | `Ctrl+Alt+P` | switch audio source |
| *(free)* | set `rearmOnce` in `config.json` | re-arm once |

```mermaid
flowchart LR
    A["Open Space Station<br/>(tray › Open X-Haptics settings)"] --> B["① Advanced"]
    B --> C["② pick your profile"]
    C --> D["③ Button Mapping"]
    D --> E["④ click a back button<br/>(M2 / M4)"]
    E --> F["⑤ Add Trigger<br/>(trigger list opens)"]
    F --> G["⑥ Direct"]
    G --> H["⑦ Keyboard tab"]
    H --> I["⑧ Ctrl + Alt"]
    I --> J["⑨ key: R (M2) / P (M4)"]
    J --> K["repeat for each profile<br/>you use"]
    K --> L["close the Space Station page"]
    L --> M["press the button<br/>tray › View log"]
```

### Step 1: pick the button and the trigger type (①–⑥)

![Step 1: Advanced, profile, Button Mapping, back button, Add Trigger, Direct](docs/images/step-1-add-trigger-direct.png)

1. **Advanced**: switch the top-left toggle from *Basic* to *Advanced*.
2. **Profile**: select the profile you use. Mappings are per profile.
3. **Button Mapping** tab.
4. **Back button**: click it under *Rear Buttons*. The screenshots show **M2** (`Ctrl+Alt+R`); do the
   same for **M4** with `Ctrl+Alt+P`.
5. **Add Trigger**: the list of trigger types opens first.
6. **Direct**. **Long Press** also works if you prefer holding the button. Avoid **Press Pulse** /
   **Release Pulse**: the pulse can be too short for Windows to register the combination.

### Step 2: choose the key combination (⑦–⑨)

![Step 2: Keyboard tab, Ctrl + Alt, key R; result Direct → Ctrl+Alt+R](docs/images/step-2-keyboard-ctrl-alt-key.png)

7. **Keyboard** tab.
8. **Combo Modifiers**: turn on **Ctrl** and **Alt**.
9. **Key**: **R** for M2, **P** for M4. The trigger row reads `Direct → Ctrl+Alt+R`.

If a *keyboard/mouse compatibility* notice appears, choose **Continue mapping**. Repeat for every
profile you use, then **close the Space Station page**: while it is open it holds the controller and
X-Haptics stays paused. Press the button and check tray › **View log** for `switch ON/OFF` or
`audio source -> …`.

The mapped button no longer acts as a controller button in games.

---

## Tray menu

- **Tray icon**: coloured while X-Haptics is on and watched, grey while the master switch is OFF.
- **Source / Controller**: what is being captured (an app, `Mix (all apps)`, `X-Haptics off`, …) and
  the controller state (`connected`, battery, `haptics asleep`, `not connected`).
- **X-Haptics on + auto re-arm**: the master switch.
- **Switch audio source** / **Audio sources**: cycle the source, choose which apps are in the cycle.
  Apps that run inside a game are listed under it, e.g. `↳ ZFGameBrowser (in BH3)`.
- **Re-arm X-Haptics once**, **Open X-Haptics settings**
- **Edit config**, **Reload config**, **Reset config to defaults...**, **View log**
- **Start with Windows** (off by default), **Start xHaptics Engine** (only when the engine is not running), **Exit**

Command line: `HapticsGuard.exe --toggle | --cycle | --kick | --reload`.

## Good to know

- **Re-arming interrupts haptics for 1–2 s** and Flydigi shows a "configuration applied" popup.
- **Haptics fall asleep** after a while without controller input (Space Station's *Real-time audio auto
  sleep*) and after every re-arm. Press any controller button; the back-button hotkeys do not wake them.
- **The Space Station page pauses X-Haptics** while it is open. Close it when you are done.
- **Switched on or off in Space Station?** The guard follows the engine within a few seconds (one balloon)
  and its master switch always does the opposite of what the engine currently does.
- **Browsers** (Chrome, Edge…) only work as a per-app source while their window is focused; use Mix.
- **Games with embedded browsers** (BH3 web events): the guard follows the child process automatically.
- **Exclusive / bit-perfect players**: see the warning below.
- **Controller off / dock / cable**: the guard shows one balloon per state change, e.g.
  *Controller disconnected… Press Home to reconnect*.

Details: [HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md).

> [!WARNING]
> **Exclusive mode, ASIO and bit-perfect output to a DAC/amp give no haptics.**
>
> X-Haptics hears audio by capturing what Windows mixes (WASAPI loopback). Players that send audio
> straight to the device bypass that mixer, so the engine hears **silence**:
>
> - **TIDAL**: *Settings › Streaming › Exclusive Mode* (and *Force Volume*).
> - **foobar2000**: output devices named **WASAPI (exclusive)**, **ASIO** (e.g. your DAC's or XMOS
>   ASIO driver), or kernel streaming.
> - Other players with WASAPI exclusive / ASIO output (Qobuz, Roon, JRiver, MusicBee…).
>
> While such a player holds the device:
>
> - there is no vibration from that music, nor from anything else playing on the same device;
> - other apps (games) may not be able to play on that DAC at all;
> - the engine's capture breaks and does not recover by itself. With the watchdog ON the guard notices
>   the device is held (one balloon, `Source: audio device in exclusive use…`), does **not** re-arm in
>   vain, and re-arms once about 2 s after the player releases the device.
>
> **Fix:** to feel music, use shared output: TIDAL with *Exclusive Mode* **off**, foobar2000 with
> **WASAPI (shared)** or **Default : Primary Sound Driver**. Keep the DAC as the Windows default playback
> device so Mix captures it. If you want bit-perfect listening, accept that haptics stop while it plays.

## Troubleshooting

| Log line / symptom | Meaning / fix |
|---|---|
| `config.json error: …` | Invalid JSON. Fix it and *Reload config*, or *Reset config to defaults...*. |
| `engine idle while Space Station page is attached` | The Space Station page holds the controller. Close it. |
| `engine unreachable` | The engine is not running or still starting; the guard waits for it. A hotkey press tells you whether it is not installed, not running or not responding. |
| `hotkey warning: … already used by another app` | Pick another combination in `config.json` and remap the button. |
| No vibration but the source looks right | Haptics asleep: press any controller button. Check the battery. |
| No vibration while TIDAL / foobar2000 plays to a DAC | The player uses exclusive mode or ASIO; switch it to shared output (see the warning above). |
| A game's profile stays off | Source switching disabled it while the game runs. Cycle back, or exit the game; the original flags are restored automatically. |
| `re-arm failed` / `switch failed` with an HTTP error | A Flydigi update may have changed the engine API. |

## License

[MIT](LICENSE) © 2026 Emrise. Not affiliated with Flydigi; "Flydigi", "APEX" and "X-Haptics" belong to
their owners.
