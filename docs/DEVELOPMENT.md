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

The exe needs **.NET Framework 4.5 or later** (CLR 4; every 4.x version replaces the previous one in place,
so it runs on 4.5 – 4.8.1). The 4.5 floor comes from `EventWaitHandle.TryOpenExisting` and the code the
compiler emits for `yield` iterators. The Windows APIs it uses exist since Windows 7, so the real limit is
the xHaptics Engine: 64-bit Windows (tested on Windows 11). Windows 10/11 ship .NET 4.8.

Run the exe from Explorer when testing. Processes started from inside some sandboxed/packaged hosts get a
virtualized `HKCU`/`%LOCALAPPDATA%`, so *Start with Windows* and the LocalAppData fallback would not
reach the real locations.

## Unit tests

```bat
test.cmd             :: builds tests\*.cs + src\**\*.cs into tests\bin\HapticsGuard.Tests.exe and runs it
test.cmd Settings    :: only tests whose Class.Method name contains "Settings"
```

The exit code is the number of failed tests. The tests use a small runner in `tests/TestFramework.cs`
(mark a `static` method with `[Test]`, use `Assert.*`), so they need no packages and stay C# 5.

They cover the logic that needs no hardware: engine status parsing (`XHapticsStatus`), JSON helpers,
hotkey parsing, the process tree, controller balloon texts, the watchdog re-arm rules (with made-up engine states), `config.json` loading/clamping/saving,
`Settings.DefaultConfig` vs `config/config.default.json`, the state files and log rotation. The test exe
runs from `tests\bin`, so the `config.json` and `data\` it creates stay there and are deleted afterwards;
it refuses to run if that folder is not writable. Everything that needs the controller, the engine or
real audio is in the manual cases of [TESTING.md](TESTING.md).

## Source layout

| Folder | Contents |
|---|---|
| `src/Program.cs` | entry point, command-line switches (`--toggle`, `--cycle`, `--kick`, `--reload`), single instance |
| `src/Properties/` | assembly version information |
| `src/UI/` | `TrayApp` (tray icon, menu, balloons), `HotkeyWindow` (global hotkeys) |
| `src/Core/` | `Guard`, the worker thread, split into partial files: `Guard.cs` (loop, shared helpers), `Guard.MasterSwitch.cs`, `Guard.Watchdog.cs` (poll loop: reads the engine once per poll, runs the rules, re-arms), `Guard.WatchdogRules.cs` (one method per re-arm rule), `Guard.Sources.cs` (switch source, Audio sources menu, restoring profile flags), `Guard.FollowChild.cs`, `Guard.Status.cs` (Source/Controller lines, controller balloons), `Guard.Helpers.cs` |
| `src/Engine/` | `EngineClient` (local HTTP API, authentication, JSON helpers), `EngineApi` (endpoints, headers, `global` scope), and typed views of its JSON: `XHapticsStatus`, `XHapticsConfigs` (profiles + enabled flags), `XHapticsGame` (game list) |
| `src/Platform/` | Windows specifics: `Audio/` (Core Audio ids/enums in `CoreAudioConstants`, COM declarations, `AudioSessions` per-app peaks, `SystemAudio` default device + exclusive-mode probe), `ProcessTree` (parents, foreground window, exe paths), `UsbDevices`, `AutoStart` (Run key) |
| `src/Config/` | `AppPaths` (exe folder or LocalAppData fallback), `Settings` (config.json load/save, defaults) |
| `src/State/` | `AutoFlag` (master switch state), `SourceOverrides` (original profile flags) |
| `src/Common/` | `Log` |
| `tests/` | unit tests and their runner (`test.cmd`) |

The defaults in `Settings.DefaultConfig` and `config/config.default.json` must stay identical.

## Files

| Path | Tracked in git | Purpose |
|---|---|---|
| `src/**/*.cs` | yes | source |
| `tests/*.cs`, `test.cmd` | yes | unit tests |
| `build.cmd`, `HapticsGuard.csproj` | yes | build script / IDE project |
| `app.manifest` | yes | manifest embedded in the exe (`asInvoker`: runs as the current user, never asks for admin) |
| `config/config.default.json` | yes | default settings |
| `README.md`, `DIAGRAM.md`, `docs/` | yes | documentation and screenshots |
| `HapticsGuard.exe`, `obj/`, `bin/`, `tests/bin/` | no | build output |
| `config.json` | no | user settings |
| `data\auto-mode.txt` | no | master switch state + profiles it disabled |
| `data\sources.txt` | no | original profile flags changed by source switching |
| `data\guard.log`, `data\guard.log.old` | no | log, rotated at `log.maxFileKB` (5 MB, so ~10 MB for both) |

## Release checklist

1. Bump the version in `src/Properties/AssemblyInfo.cs` and `app.manifest`.
2. Run `test.cmd`, build from a clean tree, run through [TESTING.md](TESTING.md).
3. Scan the exe with Windows Defender and VirusTotal.
4. Zip `HapticsGuard.exe`, `config/config.default.json`, `README.md`, `LICENSE` (forward-slash paths).
5. Publish the release with the SHA-256 of both assets.
