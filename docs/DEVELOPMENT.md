# Development

## Build

```bat
build.cmd    :: compiles src\**\*.cs into HapticsGuard.exe with the csc.exe that ships with Windows
```

`HapticsGuard.csproj` builds the same sources with Visual Studio, Rider or the MSBuild that ships with
Windows:

```bat
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe HapticsGuard.csproj
```

No SDK or targeting pack is needed. The compiler bundled with Windows only understands **C# 5**, so keep
to that syntax: no `$"..."`, `?.`, `nameof`, expression-bodied members, `out var`, …

Run the exe from Explorer when testing. Processes started from inside some sandboxed/packaged hosts get a
virtualized `HKCU`/`%LOCALAPPDATA%`, so *Start with Windows* and the LocalAppData fallback would not
reach the real locations.

## Source layout

| Folder | Contents |
|---|---|
| `src/Program.cs` | entry point, command-line switches (`--toggle`, `--cycle`, `--kick`, `--reload`), single instance |
| `src/Properties/` | assembly version information |
| `src/UI/` | `TrayApp` (tray icon, menu, balloons), `HotkeyWindow` (global hotkeys) |
| `src/Core/` | `Guard`, the worker thread, split into partial files: `Guard.cs` (loop, shared helpers), `Guard.MasterSwitch.cs`, `Guard.Watchdog.cs` (re-arm rules), `Guard.Sources.cs` (switch source, Audio sources menu, restoring profile flags), `Guard.FollowChild.cs`, `Guard.Status.cs` (Source/Controller lines, controller balloons), `Guard.Helpers.cs` |
| `src/Engine/` | `EngineClient`: local HTTP API, authentication, JSON helpers |
| `src/Platform/` | Windows specifics: `CoreAudio` (default device, peak meters, audio sessions), `ProcessTree` (parents, foreground window, exe paths), `UsbDevices`, `AutoStart` (Run key) |
| `src/Config/` | `AppPaths` (exe folder or LocalAppData fallback), `Settings` (config.json load/save, defaults) |
| `src/State/` | `AutoFlag` (master switch state), `SourceOverrides` (original profile flags) |
| `src/Common/` | `Log` |

The defaults in `Settings.DefaultConfig` and `config/config.default.json` must stay identical.

## Files

| Path | Tracked in git | Purpose |
|---|---|---|
| `src/**/*.cs` | yes | source |
| `build.cmd`, `HapticsGuard.csproj` | yes | build script / IDE project |
| `app.manifest` | yes | manifest embedded in the exe (`asInvoker`: runs as the current user, never asks for admin) |
| `config/config.default.json` | yes | default settings |
| `README.md`, `DIAGRAM.md`, `docs/` | yes | documentation and screenshots |
| `HapticsGuard.exe`, `obj/`, `bin/` | no | build output |
| `config.json` | no | user settings |
| `data\auto-mode.txt` | no | master switch state + profiles it disabled |
| `data\sources.txt` | no | original profile flags changed by source switching |
| `data\guard.log` | no | log (rotated at 1 MB) |

## Release checklist

1. Bump the version in `src/Properties/AssemblyInfo.cs` and `app.manifest`.
2. Build from a clean tree, run through [TESTING.md](TESTING.md).
3. Scan the exe with Windows Defender and VirusTotal.
4. Zip `HapticsGuard.exe`, `config/config.default.json`, `README.md`, `LICENSE` (forward-slash paths).
5. Publish the release with the SHA-256 of both assets.
