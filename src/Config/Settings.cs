using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace FlydigiHapticsGuard
{
    // Values come from config.json (see README.md); anything missing there keeps the default below.
    static class Settings
    {
        // hotkeys ("Ctrl+Alt+R" style, empty = disabled)
        public static string HotkeyToggle = "Ctrl+Alt+R";
        public static string HotkeySwitchSource = "Ctrl+Alt+P";
        public static string HotkeyRearmOnce = "";
        // engine / UI
        public static string EngineBase = "http://127.0.0.1:18757";
        public static string WebOrigin = "https://space_station.flydigi.com";
        public static int WebUiPort = 18758;               // Space Station page keeps a status WebSocket here
        public static string EngineExePath = @"C:\Program Files\Flydigi\Engine\xHaptics Engine.exe";
        public static string GameListPath = Environment.ExpandEnvironmentVariables(@"%APPDATA%\Flydigi\xHaptics Engine\config\xhaptics_game_status.json");
        public static string UiUrl = "https://space_station.flydigi.com/#/controller/x-haptics";
        public static string Browser = "";                 // empty = Chrome app window if installed, else default browser
        // watchdog
        public static int PollMs = 1000;
        public static int ReconnectKickDelaySec = 4;       // after controller connects
        public static int DeviceChangeKickDelaySec = 2;    // after default output changes
        public static int BrokenPersistSec = 4;            // engine reports stopped/error this long
        public static int RearmGraceSec = 10;              // ignore "stopped" this long after any re-arm
        public static int StallSec = 5;                    // capture counter frozen this long...
        public static int StallAudioSec = 3;               // ...while Windows is playing audio this long
        public static float AudioPeakThreshold = 0.02f;    // default endpoint meter = "audio is playing"
        public static float LoudPeakThreshold = 0.05f;
        public static float EngineSilentPeak = 0.0005f;
        public static int SilentMismatchKick = 6;          // loud system audio but engine hears silence
        public static int HealthyResetSec = 30;            // reset backoff after this long healthy
        public static int MaxIdleBounces = 2;              // re-arms undone by someone else before backing off
        public static int WebCheckDelaySec = 5;            // after a manual re-arm, check whether the web page paused it
        public static bool FollowChildAudio = true;        // per-app capture silent but a child process plays -> follow it
        public static int FollowDetectPolls = 3;           // child must play this many polls in a row before following
        public static int FollowReturnSilenceSec = 15;     // child silent this long -> back to the parent
        public static int ControllerNotifyDelaySec = 5;    // a controller state must last this long before a balloon (USB <-> wireless hand-overs)
        public static int LowBatteryPercent = 10;          // one balloon when the reported battery drops to this (0 = off)
        // log
        public static int LogMaxFileKB = 5120;             // guard.log -> guard.log.old above this; both files ~2x
        public static int[] BackoffSec = { 5, 10, 20, 30, 60 };
        // audio sources: process names (without .exe) never picked by the switch-source cycle
        public static List<string> SourceExclude = new List<string>();

        const string DefaultConfig =
@"{
  ""hotkeys"": {
    ""toggle"": ""Ctrl+Alt+R"",
    ""switchSource"": ""Ctrl+Alt+P"",
    ""rearmOnce"": """"
  },
  ""engine"": {
    ""apiUrl"": ""http://127.0.0.1:18757"",
    ""webOrigin"": ""https://space_station.flydigi.com"",
    ""statusSocketPort"": 18758,
    ""exePath"": ""C:\\Program Files\\Flydigi\\Engine\\xHaptics Engine.exe"",
    ""gameListPath"": ""%APPDATA%\\Flydigi\\xHaptics Engine\\config\\xhaptics_game_status.json""
  },
  ""ui"": {
    ""url"": ""https://space_station.flydigi.com/#/controller/x-haptics"",
    ""browser"": """"
  },
  ""watchdog"": {
    ""pollMs"": 1000,
    ""reconnectDelaySec"": 4,
    ""deviceChangeDelaySec"": 2,
    ""brokenPersistSec"": 4,
    ""rearmGraceSec"": 10,
    ""stallSec"": 5,
    ""stallAudioSec"": 3,
    ""audioPeakThreshold"": 0.02,
    ""loudPeakThreshold"": 0.05,
    ""engineSilentPeak"": 0.0005,
    ""silentMismatchKick"": 6,
    ""healthyResetSec"": 30,
    ""maxIdleBounces"": 2,
    ""webCheckDelaySec"": 5,
    ""followChildAudio"": true,
    ""followDetectPolls"": 3,
    ""followReturnSilenceSec"": 15,
    ""controllerNotifyDelaySec"": 5,
    ""lowBatteryPercent"": 10,
    ""backoffSec"": [5, 10, 20, 30, 60]
  },
  ""log"": {
    ""maxFileKB"": 5120
  },
  ""sources"": {
    ""exclude"": []
  }
}
";

        // config.json starts as a copy of config\config.default.json (built-in defaults if that file is missing too).
        static void WriteDefaults()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigPath));
            if (File.Exists(AppPaths.DefaultConfigPath)) File.Copy(AppPaths.DefaultConfigPath, AppPaths.ConfigPath, true);
            else File.WriteAllText(AppPaths.ConfigPath, DefaultConfig, new UTF8Encoding(false));
        }
        // Overwrites config.json with the defaults; the previous file is kept as data\config.backup.json.
        // Returns the backup path (null when there was nothing to back up).
        public static string ResetToDefaults()
        {
            string backup = null;
            if (File.Exists(AppPaths.ConfigPath))
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                backup = Path.Combine(AppPaths.DataDir, "config.backup.json");
                File.Copy(AppPaths.ConfigPath, backup, true);
            }
            WriteDefaults();
            return backup;
        }

        // Returns an error message, or null when config.json was read (or created) fine.
        public static string Load()
        {
            try
            {
                if (!File.Exists(AppPaths.ConfigPath)) WriteDefaults();
                var root = EngineClient.Obj(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(AppPaths.ConfigPath)));
                if (root == null) return "config.json is not a JSON object";

                HotkeyToggle = S(root, "hotkeys.toggle", HotkeyToggle);
                HotkeySwitchSource = S(root, "hotkeys.switchSource", HotkeySwitchSource);
                HotkeyRearmOnce = S(root, "hotkeys.rearmOnce", HotkeyRearmOnce);

                EngineBase = S(root, "engine.apiUrl", EngineBase).TrimEnd('/');
                WebOrigin = S(root, "engine.webOrigin", WebOrigin);
                WebUiPort = I(root, "engine.statusSocketPort", WebUiPort);
                EngineExePath = Environment.ExpandEnvironmentVariables(S(root, "engine.exePath", EngineExePath));
                GameListPath = Environment.ExpandEnvironmentVariables(S(root, "engine.gameListPath", GameListPath));
                UiUrl = S(root, "ui.url", UiUrl);
                Browser = Environment.ExpandEnvironmentVariables(S(root, "ui.browser", Browser));

                PollMs = I(root, "watchdog.pollMs", PollMs);
                ReconnectKickDelaySec = I(root, "watchdog.reconnectDelaySec", ReconnectKickDelaySec);
                DeviceChangeKickDelaySec = I(root, "watchdog.deviceChangeDelaySec", DeviceChangeKickDelaySec);
                BrokenPersistSec = I(root, "watchdog.brokenPersistSec", BrokenPersistSec);
                RearmGraceSec = I(root, "watchdog.rearmGraceSec", RearmGraceSec);
                StallSec = I(root, "watchdog.stallSec", StallSec);
                StallAudioSec = I(root, "watchdog.stallAudioSec", StallAudioSec);
                AudioPeakThreshold = F(root, "watchdog.audioPeakThreshold", AudioPeakThreshold);
                LoudPeakThreshold = F(root, "watchdog.loudPeakThreshold", LoudPeakThreshold);
                EngineSilentPeak = F(root, "watchdog.engineSilentPeak", EngineSilentPeak);
                SilentMismatchKick = I(root, "watchdog.silentMismatchKick", SilentMismatchKick);
                HealthyResetSec = I(root, "watchdog.healthyResetSec", HealthyResetSec);
                MaxIdleBounces = I(root, "watchdog.maxIdleBounces", MaxIdleBounces);
                WebCheckDelaySec = I(root, "watchdog.webCheckDelaySec", WebCheckDelaySec);
                var follow = EngineClient.At(root, "watchdog.followChildAudio");
                if (follow is bool) FollowChildAudio = (bool)follow;
                FollowDetectPolls = Math.Max(1, I(root, "watchdog.followDetectPolls", FollowDetectPolls));
                FollowReturnSilenceSec = Math.Max(3, I(root, "watchdog.followReturnSilenceSec", FollowReturnSilenceSec));
                ControllerNotifyDelaySec = Math.Max(0, I(root, "watchdog.controllerNotifyDelaySec", ControllerNotifyDelaySec));
                LowBatteryPercent = Math.Max(0, I(root, "watchdog.lowBatteryPercent", LowBatteryPercent));
                LogMaxFileKB = Math.Max(64, I(root, "log.maxFileKB", LogMaxFileKB));
                var backoff = EngineClient.At(root, "watchdog.backoffSec") as object[];
                if (backoff != null && backoff.Length > 0) BackoffSec = backoff.Select(b => Convert.ToInt32(b)).ToArray();
                var exclude = EngineClient.At(root, "sources.exclude") as object[] ?? new object[0];
                SourceExclude = exclude.Select(e => NormalizeProcessName(Convert.ToString(e))).Where(n => n.Length > 0).ToList();
                if (PollMs < 200) PollMs = 200;
                return null;
            }
            catch (Exception ex)
            {
                // the JSON parser appends the whole file to its message; keep the first line
                string msg = ex.Message.Split('\r', '\n')[0];
                return msg.Length > 150 ? msg.Substring(0, 150) : msg;
            }
        }

        public static string NormalizeProcessName(string name)
        {
            name = (name ?? "").Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return name;
        }

        public static bool IsExcluded(string processName)
        {
            return SourceExclude.Contains(NormalizeProcessName(processName), StringComparer.OrdinalIgnoreCase);
        }

        // Rewrites only "sources.exclude" in config.json, keeping every other value.
        public static void SaveSourceExclude(List<string> exclude)
        {
            var root = EngineClient.Obj(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(AppPaths.ConfigPath)));
            if (root == null) throw new InvalidDataException("config.json is not a JSON object");
            var sources = EngineClient.Obj(EngineClient.At(root, "sources"));
            if (sources == null) { sources = new Dictionary<string, object>(); root["sources"] = sources; }
            sources["exclude"] = exclude.Select(e => (object)(e + ".exe")).ToArray();
            var sb = new StringBuilder();
            WriteJson(sb, root, "");
            sb.AppendLine();
            File.WriteAllText(AppPaths.ConfigPath, sb.ToString(), new UTF8Encoding(false));
            SourceExclude = new List<string>(exclude);
        }

        // Pretty printer in the same style as the shipped config.json (scalar arrays stay on one line).
        static void WriteJson(StringBuilder sb, object v, string indent)
        {
            var js = new JavaScriptSerializer();
            var d = v as Dictionary<string, object>;
            var a = v as object[];
            if (d != null)
            {
                if (d.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (var kv in d)
                {
                    sb.Append(indent + "  " + js.Serialize(kv.Key) + ": ");
                    WriteJson(sb, kv.Value, indent + "  ");
                    sb.Append(++i < d.Count ? ",\n" : "\n");
                }
                sb.Append(indent + "}");
            }
            else if (a != null)
            {
                bool scalars = !a.Any(x => x is Dictionary<string, object> || x is object[]);
                if (scalars)
                {
                    sb.Append("[" + string.Join(", ", a.Select(x => js.Serialize(x))) + "]");
                    return;
                }
                sb.Append("[\n");
                for (int i = 0; i < a.Length; i++)
                {
                    sb.Append(indent + "  ");
                    WriteJson(sb, a[i], indent + "  ");
                    sb.Append(i + 1 < a.Length ? ",\n" : "\n");
                }
                sb.Append(indent + "]");
            }
            else sb.Append(js.Serialize(v));
        }

        static string S(Dictionary<string, object> o, string path, string def) { var v = EngineClient.At(o, path); return v is string ? (string)v : def; }
        static int I(Dictionary<string, object> o, string path, int def) { var v = EngineClient.At(o, path); try { return v == null ? def : Convert.ToInt32(v); } catch { return def; } }
        static float F(Dictionary<string, object> o, string path, float def) { var v = EngineClient.At(o, path); try { return v == null ? def : Convert.ToSingle(v); } catch { return def; } }
    }
}
