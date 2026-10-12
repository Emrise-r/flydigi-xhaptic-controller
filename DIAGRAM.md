# X-Haptics Guard — diagrams

## 1. Components

```mermaid
flowchart LR
    subgraph PC["Windows PC"]
        APPS["Games / foobar2000 / other apps"] -->|audio| WIN["Windows audio mixer<br/>(default playback device)"]
        WIN -->|"WASAPI loopback<br/>system = Mix"| ENG
        APPS -.->|"process-loopback<br/>(one game/app)"| ENG
        ENG["xHaptics Engine.exe<br/>HTTP :18757 / WS :18758"]
        WEB["Space Station page<br/>space_station.flydigi.com"] <-->|"API + status WebSocket<br/>holds controller while configuring"| ENG
        GUARD["HapticsGuard.exe<br/>(tray, optional start with Windows)"] -->|"PATCH configurations<br/>enable / disable scopes"| ENG
        GUARD -->|"poll /api/xhaptics/status"| ENG
        GUARD -->|"default device + peak meter"| WIN
        CFG[("config.json<br/>hotkeys + tuning")] --> GUARD
        FILES[("data\<br/>auto-mode.txt / sources.txt / guard.log")] <--> GUARD
    end
    ENG -->|"HID motor frames<br/>(grip + trigger)"| PAD["Flydigi APEX 6 Pro"]
    PAD -->|"M2 → Ctrl+Alt+R<br/>M4 → Ctrl+Alt+P"| GUARD
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
    [*] --> ON: auto-mode.txt missing or "on"
    [*] --> OFF: auto-mode.txt = "off"

    ON --> OFF: toggle
    note right of OFF
        Disable running scope + global
        Remember them in auto-mode.txt
        No automatic re-arm
    end note

    OFF --> ON: toggle
    note right of ON
        Re-enable remembered scopes
        (or re-arm if already enabled)
        Watchdog active
    end note
```

## 3. Watchdog (only while ON, every pollMs)

```mermaid
flowchart TD
    T([tick]) --> S{engine reachable?}
    S -- no --> W([wait])
    S -- yes --> R{"re-armed < 10 s ago?"}
    R -- yes --> W
    R -- no --> C1{controller just connected?}
    C1 -- yes --> K4["re-arm in 4 s"]
    C1 -- no --> C2{default output device changed?}
    C2 -- yes --> K2["re-arm in 2 s"]
    C2 -- no --> C3{"capture/output stopped<br/>or error for 4 s?"}
    C3 -- yes --> WEB{"Space Station page attached<br/>or re-arm keeps bouncing?"}
    WEB -- yes --> HOLD["leave it alone<br/>(page holds the controller)"]
    WEB -- no --> KICK
    C3 -- no --> C4{"Mix capture:<br/>audio playing but capture frozen / silent?"}
    C4 -- yes --> KICK
    C4 -- no --> C5{"process capture:<br/>game process exited?"}
    C5 -- yes --> KICK
    C5 -- no --> W
    K4 --> KICK
    K2 --> KICK
    KICK["re-arm: disable → enable scope<br/>backoff 5/10/20/30/60 s"] --> W
```

## 4. Audio source — switchSource hotkey (default Ctrl+Alt+P, M4)

Candidates = games/apps registered in X-Haptics (built-in or custom) whose process is running, sorted by name.

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
