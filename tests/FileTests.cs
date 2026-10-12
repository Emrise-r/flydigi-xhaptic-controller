using System.Collections.Generic;
using System.IO;

namespace FlydigiHapticsGuard.Tests
{
    static class SettingsTests
    {
        static string Normalize(string text) { return text.TrimStart('﻿').Replace("\r\n", "\n"); }

        [Test]
        static void Built_in_defaults_match_the_shipped_default_file()
        {
            Sandbox.DefaultSettings();   // no config\config.default.json in tests\bin: uses the copy built into the exe
            string shipped = File.ReadAllText(Path.Combine(Sandbox.RepoDir, "config", "config.default.json"));
            Assert.True(Normalize(shipped) == Normalize(File.ReadAllText(AppPaths.ConfigPath)),
                        "Settings.DefaultConfig and config/config.default.json differ");
            Assert.Equal("Ctrl+Alt+R", Settings.HotkeyToggle);
            Assert.Equal("Ctrl+Alt+P", Settings.HotkeySwitchSource);
        }

        [Test]
        static void Loads_values_and_clamps_unsafe_ones()
        {
            Sandbox.DefaultSettings();
            Sandbox.WriteConfig(@"{
              'hotkeys': { 'toggle': 'Ctrl+Shift+F9' },
              'engine': { 'apiUrl': 'http://127.0.0.1:1/' },
              'watchdog': { 'pollMs': 50, 'followDetectPolls': 0, 'followReturnSilenceSec': 1,
                            'controllerNotifyDelaySec': -5, 'lowBatteryPercent': -1, 'backoffSec': [1, 2], 'followChildAudio': false },
              'log': { 'maxFileKB': 1 },
              'sources': { 'exclude': ['Chrome.exe', ' ', 'BH3'] }
            }");
            Assert.Null(Settings.Load(), "load error");
            Assert.Equal("Ctrl+Shift+F9", Settings.HotkeyToggle);
            Assert.Equal("Ctrl+Alt+P", Settings.HotkeySwitchSource, "missing key keeps its value");
            Assert.Equal("http://127.0.0.1:1", Settings.EngineBase, "trailing slash");
            Assert.Equal(200, Settings.PollMs);
            Assert.Equal(1, Settings.FollowDetectPolls);
            Assert.Equal(3, Settings.FollowReturnSilenceSec);
            Assert.Equal(0, Settings.ControllerNotifyDelaySec);
            Assert.Equal(0, Settings.LowBatteryPercent);
            Assert.Equal(64, Settings.LogMaxFileKB);
            Assert.False(Settings.FollowChildAudio, "followChildAudio");
            Assert.SequenceEqual(new[] { 1, 2 }, Settings.BackoffSec);
            Assert.SequenceEqual(new[] { "Chrome", "BH3" }, Settings.SourceExclude);
            Sandbox.DefaultSettings();
        }

        [Test]
        static void Invalid_json_keeps_the_previous_settings()
        {
            Sandbox.DefaultSettings();
            Sandbox.WriteConfig("{'hotkeys':{'toggle':'Ctrl+Alt+F1'}}");
            Settings.Load();
            Sandbox.WriteConfig("{'hotkeys':{'toggle':'Ctrl+Alt+F2',}");
            string error = Settings.Load();
            Assert.NotNull(error, "error message");
            Assert.False(error.Contains("\n") || error.Contains("\r"), "one-line error");
            Assert.Equal("Ctrl+Alt+F1", Settings.HotkeyToggle);

            Sandbox.WriteConfig("[1, 2]");
            Assert.Equal("config.json is not a JSON object", Settings.Load());
            Sandbox.DefaultSettings();
        }

        [Test]
        static void Excluded_names_ignore_case_and_exe()
        {
            Sandbox.DefaultSettings();
            Settings.SourceExclude = new List<string> { "Chrome" };
            Assert.True(Settings.IsExcluded("chrome.EXE"), "chrome.EXE");
            Assert.True(Settings.IsExcluded(" Chrome "), "spaces");
            Assert.False(Settings.IsExcluded("BH3"), "BH3");
            Assert.Equal("ZFGameBrowser", Settings.NormalizeProcessName("ZFGameBrowser.exe"));
            Assert.Equal("", Settings.NormalizeProcessName(null));
            Sandbox.DefaultSettings();
        }

        [Test]
        static void Saving_the_exclude_list_keeps_other_settings()
        {
            Sandbox.DefaultSettings();
            Sandbox.WriteConfig("{'hotkeys':{'toggle':'Ctrl+Alt+F3'},'watchdog':{'pollMs':1500}}");
            Settings.Load();
            Settings.SaveSourceExclude(new List<string> { "Chrome", "BH3" });

            string text = File.ReadAllText(AppPaths.ConfigPath);
            Assert.Contains("\"Chrome.exe\"", text);
            Assert.Null(Settings.Load(), "reload");
            Assert.SequenceEqual(new[] { "Chrome", "BH3" }, Settings.SourceExclude);
            Assert.Equal("Ctrl+Alt+F3", Settings.HotkeyToggle);
            Assert.Equal(1500, Settings.PollMs);
            Sandbox.DefaultSettings();
        }

        [Test]
        static void Reset_keeps_a_backup()
        {
            Sandbox.DefaultSettings();
            string custom = Sandbox.Json("{'hotkeys':{'toggle':'Ctrl+Alt+F4'}}");
            File.WriteAllText(AppPaths.ConfigPath, custom);
            string backup = Settings.ResetToDefaults();
            Assert.Equal(Path.Combine(AppPaths.DataDir, "config.backup.json"), backup);
            Assert.Equal(custom, File.ReadAllText(backup));
            Assert.Null(Settings.Load(), "reload");
            Assert.Equal("Ctrl+Alt+R", Settings.HotkeyToggle);
        }
    }

    static class StateFileTests
    {
        [Test]
        static void Switch_state_defaults_to_on_and_round_trips()
        {
            Sandbox.Clean();
            Assert.True(AutoFlag.Load(), "no file = ON");
            Assert.SequenceEqual(new string[0], AutoFlag.LoadScopes());

            AutoFlag.Save(false, new[] { "global", "bh3" });
            Assert.False(AutoFlag.Load(), "saved OFF");
            Assert.SequenceEqual(new[] { "global", "bh3" }, AutoFlag.LoadScopes());

            AutoFlag.Save(true, null);
            Assert.True(AutoFlag.Load(), "saved ON");
            Assert.SequenceEqual(new string[0], AutoFlag.LoadScopes());
        }

        [Test]
        static void Source_overrides_round_trip_and_skip_bad_lines()
        {
            Sandbox.Clean();
            Assert.Equal(0, SourceOverrides.Load().Count, "no file");

            SourceOverrides.Save(new Dictionary<string, bool> { { "bh3", true }, { "chrome", false } });
            var loaded = SourceOverrides.Load();
            Assert.Equal(2, loaded.Count);
            Assert.True(loaded["bh3"], "bh3");
            Assert.False(loaded["chrome"], "chrome");

            File.WriteAllLines(Path.Combine(AppPaths.DataDir, "sources.txt"), new[] { "junk", "=true", " game = true " });
            loaded = SourceOverrides.Load();
            Assert.Equal(1, loaded.Count, "only the valid line");
            Assert.True(loaded["game"], "trimmed key");
        }

        [Test]
        static void Log_rotates_at_the_size_limit()
        {
            Sandbox.Clean();
            int saved = Settings.LogMaxFileKB;
            try
            {
                Settings.LogMaxFileKB = 64;
                string line = new string('x', 1000);
                for (int i = 0; i < 150; i++) Log.Write(line);

                var current = new FileInfo(Log.FilePath);
                var old = new FileInfo(Log.FilePath + ".old");
                Assert.True(old.Exists, "guard.log.old created");
                Assert.True(current.Length <= 66 * 1024, "guard.log stays under the limit (" + current.Length + " bytes)");
                Assert.True(old.Length <= 66 * 1024, "guard.log.old stays under the limit (" + old.Length + " bytes)");
            }
            finally { Settings.LogMaxFileKB = saved; }
        }
    }
}
