# X-Haptics Guard — diagrams

## 1. Components

```mermaid
flowchart LR
    subgraph PC["Windows PC"]
        APPS["Games / foobar2000 / other apps"] -->|audio| WIN["Windows audio mixer<br/>(default playback device)"]
        WIN -->|"WASAPI loopback<br/>system = Mix"| ENG
        APPS -.->|"process-loopback<br/>(one game/app)"| ENG
        ENG["xHaptics Engine.exe<br/>HTTP :18757 / WS :18758"]
        WEB["Space Station<br/>(desktop app or web page)"] <-->|"API + status WebSocket<br/>holds the controller while open"| ENG
        GUARD["HapticsGuard.exe<br/>(tray, optional start with Windows)"] -->|"enable / disable profiles<br/>(PUT global config, PATCH game profiles)"| ENG
        GUARD -->|"poll /api/xhaptics/status"| ENG
        GUARD -->|"default device, peak meter,<br/>per-app audio sessions"| WIN
        ENG -->|writes| GAMES[("engine games file<br/>xhaptics_game_status.json")]
        GAMES -->|"registered games/apps"| GUARD
        CFG[("config.json<br/>hotkeys + tuning")] --> GUARD
        FILES[("data\<br/>auto-mode.txt / sources.txt / guard.log")] <--> GUARD
    end
    ENG -->|"HID motor frames<br/>(grip + trigger)"| PAD["Flydigi APEX 6 Pro"]
    PAD -->|"M2 → Ctrl+Alt+R<br/>M4 → Ctrl+Alt+P"| GUARD
    PAD -.->|"button input (XInput)<br/>wakes haptics"| ENG
```

## 1b. Mapping a back button to a hotkey (Space Station)

```mermaid
flowchart LR
    A["Open Space Station"] --> B["Advanced"] --> C["profile"] --> D["Button Mapping"]
    D --> E["back button M2 / M4"] --> F["Add Trigger"] --> G["Direct"]
    G --> H["Keyboard tab"] --> I["Ctrl + Alt + R / P"] --> J["close the page"]
```

Screenshots: see README › *Mapping the APEX back buttons*.

## 2. Master switch — toggle hotkey (default Ctrl+Alt+R, M2)

```mermaid
stateDiagram-v2
    [*] --> ON: engine global profile enabled
    [*] --> OFF: engine global profile disabled
    note left of ON
        At start and every 3 s the switch follows
        the engine (Space Station can change it).
        auto-mode.txt only while the engine is unreachable.
    end note

    ON --> OFF: toggle (engine is on)
    note right of OFF
        Disable running scope + global
        Remember them in auto-mode.txt
        No automatic re-arm
    end note

    OFF --> ON: toggle (engine is off)
    note right of ON
        Re-enable remembered scopes
        (or re-arm if already enabled)
        Watchdog active
    end note
```

## 3. Watchdog (only while ON, every pollMs)

Every rule is checked on every poll; the first one that applies re-arms. Automatic re-arms are spaced by the
back-off, so a re-arm that comes due earlier waits for it.

```mermaid
flowchart TD
    T([poll]) --> S{engine reachable?}
    S -- no --> W([next poll])
    S -- yes --> N["note events:<br/>controller connected → re-arm due in 4 s<br/>default output device changed → re-arm due in 2 s"]
    N --> EX{"engine stopped and the output device<br/>held by an exclusive / ASIO player?"}
    EX -- yes --> HOLD["wait, probe every 2 s;<br/>re-arm once when it is released"] --> W
    EX -- no --> D{scheduled re-arm due?}
    D -- yes --> SK{"controller off, Space Station holds it,<br/>engine recovered by itself (reconnect),<br/>or engine hears the new device (device change)?"}
    SK -- yes --> SKIP["skip, log why"] --> W
    SK -- no --> KICK
    D -- no --> R1{"capture/output stopped or error for 4 s,<br/>not held by Space Station or bouncing back to Idle,<br/>no re-arm in the last 10 s?"}
    R1 -- yes --> KICK
    R1 -- no --> R2{"controller awake, Mix capture, audio playing:<br/>capture frozen or engine hears only silence?"}
    R2 -- yes --> KICK
    R2 -- no --> R3{"per-app capture still bound to<br/>an exited process for 4 s?"}
    R3 -- yes --> KICK
    R3 -- no --> W
    KICK["re-arm: disable → enable the profile<br/>back-off 5/10/20/30/60 s"] --> W
```

## 4. Audio source — switchSource hotkey (default Ctrl+Alt+P, M4)

Candidates = games/apps registered in X-Haptics (built-in or custom) whose process is running and that are
checked in tray › *Audio sources* (not in `sources.exclude`), sorted by name.

```mermaid
stateDiagram-v2
    direction LR
    Mix: Mix (all apps, system loopback)
    A: App/game 1 (process-loopback)
    B: App/game 2 (process-loopback)
    Mix --> A: switchSource
    A --> B: switchSource
    B --> Mix: switchSource
```

```mermaid
sequenceDiagram
    participant G as HapticsGuard
    participant E as xHaptics Engine
    G->>E: disable every running game scope
    Note over E: falls back to Mix
    alt target is a game/app
        G->>G: wait 1.2 s
        G->>E: enable target scope
        Note over E: process-loopback on target
    end
    G->>G: save original flags → sources.txt
    loop every 5 s
        G->>G: none of those processes running?
        G->>E: restore original enabled flags
    end
```
