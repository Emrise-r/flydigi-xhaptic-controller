// Flydigi X-Haptics Guard
// Tray helper for the local xHaptics Engine (127.0.0.1:18757). "Re-arm" = disable -> enable X-Haptics,
// the same thing the Space Station web UI toggle does.
//
// Hotkeys and tuning live in config.json next to the exe; state and log go to .\data\. See README.md.
//
// Toggle hotkey (default Ctrl+Alt+R, e.g. mapped to a back button) or "--toggle" is a master switch:
//   OFF = X-Haptics disabled (running scope + global) and nothing re-enables it;
//   ON  = those scopes enabled again and the watchdog below is active.
// The state is saved to data\auto-mode.txt and restored on start (default on).
// Switch-source hotkey (default Ctrl+Alt+P) or "--cycle" cycles the capture: Mix -> running apps/games -> Mix.
// While ON, X-Haptics is re-armed when:
//   - the controller (re)connects
//   - the Windows default playback device changes
//   - the engine capture/output stops or reports an error (e.g. after a WASAPI-exclusive player)
//   - audio is playing but the engine's system loopback capture is frozen or silent
//   - a per-game capture is still bound to a game process that has exited
// It never fights the Space Station page while that page holds the controller.
// "HapticsGuard.exe --kick" (or the tray menu) re-arms once without touching AUTO mode.
// Build with build.cmd (.NET Framework csc, C# 5).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Reflection;

// Version resource shown in Explorer > Properties > Details (also helps antivirus reputation).
[assembly: AssemblyTitle("X-Haptics Guard")]
[assembly: AssemblyDescription("Tray helper for the Flydigi xHaptics Engine: X-Haptics on/off, watchdog and audio source switching")]
[assembly: AssemblyCompany("Emrise")]
[assembly: AssemblyProduct("X-Haptics Guard")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Emrise. MIT License.")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyInformationalVersion("1.0.1")]

namespace FlydigiHapticsGuard
{
    // Everything lives next to the exe: config.json (tracked) and data\ (state + log, not tracked).
    static class AppPaths
    {
        public static readonly string BaseDir = Path.GetDirectoryName(Application.ExecutablePath);
        public static readonly string ConfigPath = Path.Combine(BaseDir, "config.json");                  // personal, not tracked
        public static readonly string DefaultConfigPath = Path.Combine(BaseDir, "config", "config.default.json");   // shipped defaults
        public static readonly string DataDir = Path.Combine(BaseDir, "data");
    }

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
    ""backoffSec"": [5, 10, 20, 30, 60]
  },
  ""sources"": {
    ""exclude"": []
  }
}
";

        // config.json starts as a copy of config\config.default.json (built-in defaults if that file is missing too).
        static void WriteDefaults()
        {
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
                var backoff = EngineClient.At(root, "watchdog.backoffSec") as object[];
                if (backoff != null && backoff.Length > 0)
                {
                    var list = new List<int>();
                    foreach (var b in backoff) list.Add(Convert.ToInt32(b));
                    BackoffSec = list.ToArray();
                }
                var exclude = new List<string>();
                var excludeArr = EngineClient.At(root, "sources.exclude") as object[];
                if (excludeArr != null) foreach (var e in excludeArr) { string n = NormalizeProcessName(Convert.ToString(e)); if (n.Length > 0) exclude.Add(n); }
                SourceExclude = exclude;
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
            string n = NormalizeProcessName(processName);
            foreach (var e in SourceExclude) if (string.Equals(e, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Rewrites only "sources.exclude" in config.json, keeping every other value.
        public static void SaveSourceExclude(List<string> exclude)
        {
            var root = EngineClient.Obj(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(AppPaths.ConfigPath)));
            if (root == null) throw new InvalidDataException("config.json is not a JSON object");
            var sources = EngineClient.Obj(EngineClient.At(root, "sources"));
            if (sources == null) { sources = new Dictionary<string, object>(); root["sources"] = sources; }
            var list = new List<object>();
            foreach (var e in exclude) list.Add(e + ".exe");
            sources["exclude"] = list.ToArray();
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
                bool scalars = true;
                foreach (var x in a) if (x is Dictionary<string, object> || x is object[]) scalars = false;
                if (scalars)
                {
                    var parts = new List<string>();
                    foreach (var x in a) parts.Add(js.Serialize(x));
                    sb.Append("[" + string.Join(", ", parts) + "]");
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

    // ---------------------------------------------------------------- Core Audio (default endpoint id + peak meter)
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    // Processes that own an audio session on any active playback device (what the Volume mixer shows).
    static class AudioSessions
    {
        const int eRender = 0, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23, AudioSessionStateExpired = 2;

        public static HashSet<int> ProcessIds()
        {
            var pids = new HashSet<int>();
            IMMDeviceEnumerator en = null; IMMDeviceCollection devices = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out devices) != 0 || devices == null) return pids;
                int count; devices.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IMMDevice dev = null; object mgrObj = null; IAudioSessionEnumerator sessions = null;
                    try
                    {
                        if (devices.Item(i, out dev) != 0 || dev == null) continue;
                        Guid iid = typeof(IAudioSessionManager2).GUID;
                        if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out mgrObj) != 0 || mgrObj == null) continue;
                        if (((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions) != 0 || sessions == null) continue;
                        int n; sessions.GetCount(out n);
                        for (int s = 0; s < n; s++)
                        {
                            object ctlObj = null;
                            try
                            {
                                if (sessions.GetSession(s, out ctlObj) != 0 || ctlObj == null) continue;
                                var ctl = (IAudioSessionControl2)ctlObj;
                                int state; uint pid;
                                if (ctl.GetState(out state) == 0 && state != AudioSessionStateExpired && ctl.GetProcessId(out pid) == 0 && pid != 0)
                                    pids.Add((int)pid);
                            }
                            catch { }
                            finally { if (ctlObj != null) Marshal.ReleaseComObject(ctlObj); }
                        }
                    }
                    catch { }
                    finally
                    {
                        if (sessions != null) Marshal.ReleaseComObject(sessions);
                        if (mgrObj != null) Marshal.ReleaseComObject(mgrObj);
                        if (dev != null) Marshal.ReleaseComObject(dev);
                    }
                }
            }
            catch { }
            finally
            {
                if (devices != null) Marshal.ReleaseComObject(devices);
                if (en != null) Marshal.ReleaseComObject(en);
            }
            return pids;
        }

        // ---- process tree (the engine's process-loopback includes child processes)

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);

        public static Dictionary<int, int> ParentMap()
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (Process32FirstW(snap, ref e))
                    do map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; while (Process32NextW(snap, ref e));
            }
            finally { CloseHandle(snap); }
            return map;
        }

        // True when pid itself, or one of its descendants, owns an audio session.
        public static bool TreeHasSession(int pid, HashSet<int> sessionPids, Dictionary<int, int> parents)
        {
            foreach (int s in sessionPids)
            {
                int cur = s;
                for (int depth = 0; depth < 32 && cur > 0; depth++)
                {
                    if (cur == pid) return true;
                    int parent;
                    if (!parents.TryGetValue(cur, out parent) || parent == cur) break;
                    cur = parent;
                }
            }
            return false;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        public static int ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return (int)pid; }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // Works for most processes, including ones Process.MainModule refuses (other bitness, elevated).
        public static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    static class SystemAudio
    {
        const int eRender = 0, eConsole = 0, CLSCTX_ALL = 23;

        // Returns false when there is no default playback device.
        public static bool Read(out string deviceId, out float peak)
        {
            deviceId = null; peak = 0;
            IMMDeviceEnumerator en = null; IMMDevice dev = null; object meterObj = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.GetDefaultAudioEndpoint(eRender, eConsole, out dev) != 0 || dev == null) return false;
                dev.GetId(out deviceId);
                Guid iid = typeof(IAudioMeterInformation).GUID;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out meterObj) == 0 && meterObj != null)
                    ((IAudioMeterInformation)meterObj).GetPeakValue(out peak);
                return true;
            }
            catch { return deviceId != null; }
            finally
            {
                if (meterObj != null) Marshal.ReleaseComObject(meterObj);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }
    }

    // ---------------------------------------------------------------- Engine HTTP client
    class EngineException : Exception
    {
        public int Status;
        public EngineException(int status, string msg) : base(msg) { Status = status; }
    }

    class EngineClient
    {
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        string token;

        string Send(string method, string path, string body, bool auth)
        {
            var req = (HttpWebRequest)WebRequest.Create(Settings.EngineBase + path);
            req.Method = method;
            req.Proxy = null;
            req.Timeout = 4000;
            req.ReadWriteTimeout = 4000;
            req.Headers["Origin"] = Settings.WebOrigin;
            req.Headers["X-Client-Name"] = "space-station-web";
            req.Headers["X-Protocol-Version"] = "1";
            if (auth) req.Headers["Authorization"] = "Bearer " + token;
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json";
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new EngineException(0, ex.Message);
                string text;
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd();
                throw new EngineException((int)resp.StatusCode, text);
            }
        }

        void Bootstrap()
        {
            var v = Obj(json.DeserializeObject(Send("GET", "/api/engine/version", null, false)));
            token = Str(v, "accessToken");
            if (string.IsNullOrEmpty(token)) throw new EngineException(0, "bootstrap returned no access token");
        }

        string Call(string method, string path, string body)
        {
            if (token == null) Bootstrap();
            try { return Send(method, path, body, true); }
            catch (EngineException ex)
            {
                if (ex.Status != 401) throw;
                Bootstrap();
                return Send(method, path, body, true);
            }
        }

        public Dictionary<string, object> Get(string path)
        {
            return Obj(json.DeserializeObject(Call("GET", path, null)));
        }

        public Dictionary<string, object> Patch(string path, object body)
        {
            return Obj(json.DeserializeObject(Call("PATCH", path, json.Serialize(body))));
        }

        public Dictionary<string, object> Post(string path, object body)
        {
            return Obj(json.DeserializeObject(Call("POST", path, json.Serialize(body))));
        }

        // ---- tiny JSON helpers
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        public static object At(Dictionary<string, object> o, string path)
        {
            object cur = o;
            foreach (var key in path.Split('.'))
            {
                var d = cur as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(key, out cur)) return null;
            }
            return cur;
        }
        public static string Str(Dictionary<string, object> o, string path) { var v = At(o, path); return v == null ? null : Convert.ToString(v); }
        public static bool Bool(Dictionary<string, object> o, string path) { var v = At(o, path); return v is bool && (bool)v; }
        public static long Long(Dictionary<string, object> o, string path) { var v = At(o, path); try { return v == null ? -1 : Convert.ToInt64(v); } catch { return -1; } }
        public static double Num(Dictionary<string, object> o, string path) { var v = At(o, path); try { return v == null ? 0 : Convert.ToDouble(v); } catch { return 0; } }
    }

    // ---------------------------------------------------------------- Persistent master switch
    // Line 1: "on"/"off". Line 2: the X-Haptics scopes the guard disabled, so "on" restores exactly those.
    static class AutoFlag
    {
        static readonly string FilePath = Path.Combine(Log.Dir, "auto-mode.txt");
        static string[] Lines()
        {
            try { return File.Exists(FilePath) ? File.ReadAllLines(FilePath) : new string[0]; }
            catch { return new string[0]; }
        }
        public static bool Load()
        {
            // on unless the user switched it off
            var l = Lines();
            return l.Length == 0 || l[0].Trim() != "off";
        }
        public static List<string> LoadScopes()
        {
            var l = Lines();
            var scopes = new List<string>();
            if (l.Length > 1)
                foreach (var s in l[1].Split(','))
                    if (s.Trim().Length > 0) scopes.Add(s.Trim());
            return scopes;
        }
        public static void Save(bool on, IEnumerable<string> disabledScopes)
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllLines(FilePath, new[] { on ? "on" : "off", disabledScopes == null ? "" : string.Join(",", disabledScopes) });
            }
            catch (Exception ex) { Log.Write("cannot save switch state: " + ex.Message); }
        }
    }

    // ---------------------------------------------------------------- Start with Windows (tray toggle, off unless the user turns it on)
    // Uses HKCU\...\Run. Older versions used a Startup-folder shortcut; it counts as "on" and is removed on the next toggle.
    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "X-Haptics Guard";
        static string Command { get { return "\"" + Application.ExecutablePath + "\""; } }
        static string LegacyShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "X-Haptics Guard.lnk"); } }

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = key == null ? null : key.GetValue(ValueName) as string;
                    if (v != null && string.Equals(v.Trim(), Command, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return File.Exists(LegacyShortcut);
        }

        public static void Set(bool on)
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(ValueName, Command);
                else key.DeleteValue(ValueName, false);
            }
            if (File.Exists(LegacyShortcut)) File.Delete(LegacyShortcut);
        }
    }

    // ---------------------------------------------------------------- Audio source overrides (scope -> original enabled flag)
    static class SourceOverrides
    {
        static readonly string FilePath = Path.Combine(Log.Dir, "sources.txt");
        public static Dictionary<string, bool> Load()
        {
            var d = new Dictionary<string, bool>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim() == "true";
                    }
            }
            catch { }
            return d;
        }
        public static void Save(Dictionary<string, bool> d)
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                var lines = new List<string>();
                foreach (var kv in d) lines.Add(kv.Key + "=" + (kv.Value ? "true" : "false"));
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception ex) { Log.Write("cannot save sources.txt: " + ex.Message); }
        }
    }

    // ---------------------------------------------------------------- Watchdog
    // Everything engine-related runs on this one worker thread; the UI only sets flags.
    class Guard
    {
        readonly EngineClient engine = new EngineClient();
        readonly Action<string> notify;
        volatile bool autoEnabled, manualKick, stopping;

        // monitor state (reset whenever AUTO is switched on)
        bool engineReachable = true, hidConnected, initialised;
        string defaultDeviceId, activeScope = "global";
        DateTime? pendingKickAt; string pendingReason;
        DateTime brokenSince, healthySince, nextKickAllowed, seqChangedAt, deadPidSince, lastKickAt, lastBalloon;
        DateTime? webCheckAt;
        long lastSeq; int audioSecondsWhileStalled, silentMismatch, backoffIndex, idleBounces;
        bool lastKickWasIdle, loggedIdleHold;

        public string LastAction = "Ready";
        public bool AutoEnabled { get { return autoEnabled; } }

        public Guard(Action<string> notify, bool auto)
        {
            this.notify = notify;
            autoEnabled = auto;
            ResetMonitor();
        }

        // Master switch: off = X-Haptics disabled and no auto re-arm; on = X-Haptics enabled and watched.
        public bool ToggleAuto()
        {
            autoEnabled = !autoEnabled;
            pendingSwitch = autoEnabled ? 1 : 2;
            Log.Write("switch " + (autoEnabled ? "ON" : "OFF"));
            LastAction = autoEnabled ? "on" : "off";
            return autoEnabled;
        }

        volatile int pendingSwitch;   // 1 = turn X-Haptics on, 2 = turn it off (done on the worker thread)

        void ApplySwitch(bool on)
        {
            var configs = EngineClient.Obj(EngineClient.At(engine.Get("/api/v1/xhaptics/configurations"), "configurations"));
            if (configs == null) throw new EngineException(0, "no configurations");
            if (!on)
            {
                // Disable the running scope and global, otherwise the engine just falls back to global.
                var st = engine.Get("/api/xhaptics/status");
                string scope = EngineClient.Str(st, "session.scope");
                var targets = new List<string>();
                if (!string.IsNullOrEmpty(scope)) targets.Add(scope);
                if (!targets.Contains("global")) targets.Add("global");
                var disabled = new List<string>();
                foreach (var s in targets)
                {
                    var c = configs.ContainsKey(s) ? EngineClient.Obj(configs[s]) : null;
                    if (c != null && EngineClient.Bool(c, "enabled")) { Toggle(s, c, false); disabled.Add(s); }
                }
                AutoFlag.Save(false, disabled);
                Log.Write("X-Haptics turned OFF (scopes: " + (disabled.Count > 0 ? string.Join(",", disabled) : "already off") + ")");
                LastAction = "X-Haptics off";
                Notify("X-Haptics OFF. It stays off until you press the button again.", true);
            }
            else
            {
                var scopes = AutoFlag.LoadScopes();
                if (scopes.Count == 0) scopes.Add("global");
                var enabled = new List<string>();
                foreach (var s in scopes)
                {
                    var c = configs.ContainsKey(s) ? EngineClient.Obj(configs[s]) : null;
                    if (c != null && !EngineClient.Bool(c, "enabled")) { Toggle(s, c, true); enabled.Add(s); }
                }
                AutoFlag.Save(true, null);
                // enabling starts a fresh pipeline; if it was already on, re-arm it instead
                if (enabled.Count == 0) Kick("switch on", true, false);
                else { lastKickAt = DateTime.Now; Log.Write("X-Haptics turned ON (scopes: " + string.Join(",", enabled) + ")"); }
                LastAction = "X-Haptics on, auto re-arm active";
                Notify("X-Haptics ON. Auto re-arm is active. Press any controller button to wake it.", true);
            }
        }

        public void RequestKick() { manualKick = true; }
        public void RequestCycle() { cycleRequested = true; }
        volatile bool cycleRequested;
        DateTime? sourceCheckAt; string sourceCheckLabel;

        // Multi-process apps (e.g. Chrome plays audio from a separate audio-service process): the engine captures
        // the process tree of the app's foreground-window process while that window is focused, and otherwise a
        // process it picked by name (often one that plays nothing). The guard cannot change that; it only says so.
        void CheckSourceProcess()
        {
            var st = engine.Get("/api/xhaptics/status");
            if (EngineClient.Str(st, "realtimeAudio.wasapi.source") != "process") return;
            long pid = EngineClient.Long(st, "realtimeAudio.wasapi.processId");
            if (pid <= 0) return;
            var sessions = AudioSessions.ProcessIds();
            var parents = AudioSessions.ParentMap();
            if (AudioSessions.TreeHasSession((int)pid, sessions, parents)) return;
            // only complain when another process of the same app is the one playing
            string name = ProcessName((int)pid);
            bool appPlays = false;
            foreach (int s in sessions) if (string.Equals(ProcessName(s), name, StringComparison.OrdinalIgnoreCase)) appPlays = true;
            if (!appPlays) return;
            Log.Write("audio source " + sourceCheckLabel + ": engine captures PID " + pid + ", which plays no audio (multi-process app; only captured while its window is focused)");
            Notify(sourceCheckLabel + " plays audio from another process. The engine only captures it while the " + sourceCheckLabel +
                   " window is focused. Use Mix to feel it in the background.", true);
        }

        static string ProcessName(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch { return null; }
        }
        DateTime nextRestoreCheck = DateTime.MinValue;

        // ---- audio source cycling
        // The engine captures one process (process-loopback) while an enabled game/app scope is running,
        // and the whole system mix when none is. Cycling flips the enabled flags of the running scopes:
        //   Mix -> app/game 1 -> app/game 2 -> ... -> Mix
        // The original flags are kept in sources.txt and put back once those processes have exited.

        class GameInfo { public string Id, Name, Process; }

        object[] cachedGames; DateTime cachedGamesStamp;
        readonly object gamesGate = new object();

        // GET /api/v1/xhaptics/games makes the engine rescan games and drop to Mix for ~2 s, so the list is
        // read from the engine's own games file instead; the API is only a fallback for user-triggered calls.
        object[] LoadGames(bool allowApi)
        {
            lock (gamesGate) return LoadGamesLocked(allowApi);
        }

        object[] LoadGamesLocked(bool allowApi)
        {
            try
            {
                var fi = new FileInfo(Settings.GameListPath);
                if (fi.Exists)
                {
                    if (cachedGames == null || fi.LastWriteTimeUtc != cachedGamesStamp)
                    {
                        string text;
                        using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var r = new StreamReader(fs, Encoding.UTF8)) text = r.ReadToEnd();
                        var root = EngineClient.Obj(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text));
                        cachedGames = EngineClient.At(root, "games") as object[];
                        cachedGamesStamp = fi.LastWriteTimeUtc;
                    }
                    if (cachedGames != null) return cachedGames;
                }
            }
            catch (Exception ex) { Log.Write("cannot read games file: " + Trim(ex.Message)); }
            return allowApi ? EngineClient.At(engine.Get("/api/v1/xhaptics/games"), "games") as object[] : null;
        }

        // Registered games/apps whose process is running. applyExclude drops the ones listed in
        // config.json "sources.exclude" (they are never picked as a target, but are still switched off).
        List<GameInfo> RunningGames(bool allowApi, bool applyExclude)
        {
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses()) { try { running.Add(p.ProcessName); } catch { } finally { p.Dispose(); } }
            return MatchGames(LoadGames(allowApi), running, applyExclude);
        }

        static List<GameInfo> MatchGames(object[] games, HashSet<string> processNames, bool applyExclude)
        {
            var result = new List<GameInfo>();
            if (games == null) return result;
            foreach (var o in games)
            {
                var g = EngineClient.Obj(o);
                if (g == null) continue;
                var names = EngineClient.At(g, "processNames") as object[];
                if (names == null) continue;
                foreach (var n in names)
                {
                    string name = Settings.NormalizeProcessName(Convert.ToString(n));
                    if (name.Length > 0 && processNames.Contains(name))
                    {
                        if (!(applyExclude && Settings.IsExcluded(name)))
                            result.Add(new GameInfo { Id = EngineClient.Str(g, "gameId"), Name = EngineClient.Str(g, "displayName") ?? name, Process = name });
                        break;
                    }
                }
            }
            result.Sort(delegate(GameInfo a, GameInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return result;
        }

        // ---- "Audio sources" tray menu

        public class SourceEntry
        {
            public string Process, Path, Label, GameId;   // GameId == null: not registered in X-Haptics yet
            public bool Included;                         // registered and not excluded = part of the cycle
        }

        // Called on the UI thread when the menu opens: processes with an audio session plus running
        // registered games. Reads only the engine's games file (never the games API).
        public List<SourceEntry> SourceEntries()
        {
            var byName = new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            foreach (int pid in AudioSessions.ProcessIds())
            {
                if (pid == self) continue;
                string path = AudioSessions.ImagePath(pid);
                if (string.IsNullOrEmpty(path)) continue;
                if (string.Equals(path, Settings.EngineExePath, StringComparison.OrdinalIgnoreCase)) continue;   // the engine itself
                string name = Settings.NormalizeProcessName(System.IO.Path.GetFileName(path));
                if (byName.ContainsKey(name)) continue;
                string label = name;
                try { var d = FileVersionInfo.GetVersionInfo(path).FileDescription; if (!string.IsNullOrEmpty(d) && d.Trim().Length > 0) label = d.Trim(); } catch { }
                byName[name] = new SourceEntry { Process = name, Path = path, Label = label };
            }

            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses()) { try { running.Add(p.ProcessName); } catch { } finally { p.Dispose(); } }
            foreach (var g in MatchGames(LoadGames(false), running, false))
            {
                SourceEntry e;
                if (!byName.TryGetValue(g.Process, out e)) byName[g.Process] = e = new SourceEntry { Process = g.Process };
                e.GameId = g.Id; e.Label = g.Name;
            }

            var list = new List<SourceEntry>(byName.Values);
            foreach (var e in list) e.Included = e.GameId != null && !Settings.IsExcluded(e.Process);
            list.Sort(delegate(SourceEntry a, SourceEntry b) { return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        readonly Queue<Action> work = new Queue<Action>();
        void Enqueue(Action a) { lock (work) work.Enqueue(a); }

        // Include: register the process as a disabled custom game if needed and drop it from the exclude list.
        // Exclude: add it to the exclude list (nothing is removed from Flydigi's game list).
        public void RequestSourceInclude(SourceEntry e, bool include)
        {
            Enqueue(delegate
            {
                try
                {
                    var exclude = new List<string>(Settings.SourceExclude);
                    exclude.RemoveAll(delegate(string x) { return string.Equals(x, e.Process, StringComparison.OrdinalIgnoreCase); });
                    if (include)
                    {
                        if (e.GameId == null)
                        {
                            if (string.IsNullOrEmpty(e.Path)) throw new InvalidOperationException("unknown path for " + e.Process);
                            var body = new Dictionary<string, object>();
                            body["displayName"] = e.Label;
                            body["processPath"] = e.Path;
                            var r = engine.Post("/api/v1/xhaptics/games/custom", body);
                            string id = EngineClient.Str(r, "game.gameId");
                            // new custom games start disabled; make sure, so the engine keeps capturing Mix
                            var configs = EngineClient.Obj(EngineClient.At(engine.Get("/api/v1/xhaptics/configurations"), "configurations"));
                            var c = id != null && configs != null && configs.ContainsKey(id) ? EngineClient.Obj(configs[id]) : null;
                            if (c != null && EngineClient.Bool(c, "enabled")) Toggle(id, c, false);
                            lastKickAt = DateTime.Now;
                            Log.Write("registered " + e.Path + " as custom game " + id + " (disabled)");
                        }
                    }
                    else exclude.Add(e.Process);
                    Settings.SaveSourceExclude(exclude);
                    Log.Write("audio source " + e.Process + (include ? " included" : " excluded"));
                    Notify(e.Label + (include ? " added to" : " removed from") + " the audio source cycle.", true);
                }
                catch (Exception ex)
                {
                    Log.Write("audio source " + (include ? "include" : "exclude") + " failed (" + e.Process + "): " + Trim(ex.Message));
                    Notify("Could not update audio sources. " + Explain(ex), true);
                }
            });
        }

        void Cycle()
        {
            if (!autoEnabled)
            {
                Log.Write("audio source switch ignored: switch is OFF");
                Notify("X-Haptics is off. Turn it on first" + (Settings.HotkeyToggle.Length > 0 ? " (" + Settings.HotkeyToggle + ")." : "."), true);
                return;
            }

            var games = RunningGames(true, false);   // all running registered scopes (all get switched off)
            if (games.Count == 0)
            {
                Notify("Audio source: Mix (all apps). No registered game or app is running.", true);
                return;
            }
            var targets = games.FindAll(delegate(GameInfo g) { return !Settings.IsExcluded(g.Process); });
            var configs = EngineClient.Obj(EngineClient.At(engine.Get("/api/v1/xhaptics/configurations"), "configurations"));

            // Current source = the enabled running scope (the engine's own status is unreliable while it is
            // switching or paused by the Space Station page). If several are enabled, ask the engine.
            var enabledIds = new List<string>();
            foreach (var g in games)
                if (configs != null && configs.ContainsKey(g.Id) && EngineClient.Bool(EngineClient.Obj(configs[g.Id]), "enabled")) enabledIds.Add(g.Id);
            string current = "mix";
            if (enabledIds.Count == 1) current = enabledIds[0];
            else if (enabledIds.Count > 1)
            {
                string scope = EngineClient.Str(engine.Get("/api/xhaptics/status"), "session.scope");
                current = enabledIds.Contains(scope) ? scope : enabledIds[0];
            }

            // an excluded scope that is currently selected (idx -1) moves on to Mix
            var order = new List<string> { "mix" };
            foreach (var g in targets) order.Add(g.Id);
            int idx = order.IndexOf(current);
            string next = order[(idx + 1) % order.Count];
            var originals = SourceOverrides.Load();
            // The engine only picks up a game scope when it is enabled while the engine sits on global,
            // so: disable every running scope (target included) -> engine falls back to Mix -> enable target.
            foreach (var g in games)
            {
                var c = configs != null && configs.ContainsKey(g.Id) ? EngineClient.Obj(configs[g.Id]) : null;
                if (c == null) continue;
                bool have = EngineClient.Bool(c, "enabled");
                if (!originals.ContainsKey(g.Id)) originals[g.Id] = have;
                if (have) Toggle(g.Id, c, false);
            }
            SourceOverrides.Save(originals);
            if (next != "mix" && configs != null && configs.ContainsKey(next))
            {
                Thread.Sleep(1200);
                Toggle(next, EngineClient.Obj(configs[next]), true);
            }
            lastKickAt = DateTime.Now; brokenSince = DateTime.MaxValue; deadPidSince = DateTime.MaxValue; silentMismatch = 0;

            string label = next == "mix" ? "Mix (all apps)" : games.Find(delegate(GameInfo g) { return g.Id == next; }).Name;
            Log.Write("audio source -> " + label + " (" + next + ")");
            LastAction = "Source: " + label;
            Notify("Audio source: " + label, true);
            if (next != "mix") { sourceCheckAt = DateTime.Now.AddSeconds(4); sourceCheckLabel = label; }
        }

        // Put back the enabled flags changed by Cycle() once none of those processes is running.
        void RestoreSourcesIfDone()
        {
            if (!autoEnabled || DateTime.Now < nextRestoreCheck) return;
            nextRestoreCheck = DateTime.Now.AddSeconds(5);
            var originals = SourceOverrides.Load();
            if (originals.Count == 0) return;
            var running = new HashSet<string>();
            var games = RunningGames(false, false);
            if (games.Count == 0 && cachedGames == null) return;   // games file unreadable: don't guess
            foreach (var g in games) running.Add(g.Id);
            foreach (var id in originals.Keys) if (running.Contains(id)) return;

            var configs = EngineClient.Obj(EngineClient.At(engine.Get("/api/v1/xhaptics/configurations"), "configurations"));
            foreach (var kv in originals)
            {
                var c = configs != null && configs.ContainsKey(kv.Key) ? EngineClient.Obj(configs[kv.Key]) : null;
                if (c != null && EngineClient.Bool(c, "enabled") != kv.Value) Toggle(kv.Key, c, kv.Value);
            }
            SourceOverrides.Save(new Dictionary<string, bool>());
            Log.Write("audio source overrides restored (" + string.Join(",", originals.Keys) + ")");
        }
        public void Stop() { stopping = true; }

        void ResetMonitor()
        {
            DateTime now = DateTime.Now;
            initialised = false; pendingKickAt = null;
            brokenSince = DateTime.MaxValue; deadPidSince = DateTime.MaxValue;
            healthySince = now; seqChangedAt = now; nextKickAllowed = DateTime.MinValue; lastKickAt = DateTime.MinValue;
            lastSeq = -1; audioSecondsWhileStalled = 0; silentMismatch = 0; backoffIndex = 0; idleBounces = 0;
            lastKickWasIdle = false; loggedIdleHold = false;
        }

        public void Run()
        {
            Log.Write("guard " + Application.ProductVersion + " started, switch " + (autoEnabled ? "ON" : "OFF"));
            while (!stopping)
            {
                try
                {
                    int sw = pendingSwitch;
                    if (sw != 0)
                    {
                        pendingSwitch = 0;
                        ResetMonitor();
                        try { ApplySwitch(sw == 1); }
                        catch (Exception ex)
                        {
                            // nothing was switched: keep the previous state (menu + auto-mode.txt stay consistent)
                            autoEnabled = sw != 1;
                            LastAction = EngineDown(ex) ? EngineProblemShort() : "Switch failed";
                            Log.Write("switch failed: " + Trim(ex.Message));
                            Notify("X-Haptics was not switched " + (sw == 1 ? "on" : "off") + ". " + Explain(ex), true);
                        }
                    }
                    if (manualKick)
                    {
                        manualKick = false;
                        Kick("manual", true);
                        webCheckAt = DateTime.Now.AddSeconds(Settings.WebCheckDelaySec);
                    }
                    if (webCheckAt.HasValue && DateTime.Now >= webCheckAt.Value) { webCheckAt = null; CheckWebHold(); }
                    if (cycleRequested)
                    {
                        cycleRequested = false;
                        try { Cycle(); }
                        catch (Exception ex) { Log.Write("source switch failed: " + Trim(ex.Message)); Notify("Audio source was not switched. " + Explain(ex), true); }
                    }
                    if (sourceCheckAt.HasValue && DateTime.Now >= sourceCheckAt.Value) { sourceCheckAt = null; CheckSourceProcess(); }
                    while (true)
                    {
                        Action job = null;
                        lock (work) if (work.Count > 0) job = work.Dequeue();
                        if (job == null) break;
                        job();
                    }
                    RestoreSourcesIfDone();
                    if (autoEnabled) Tick();
                }
                catch (Exception ex) { Log.Write("error: " + Trim(ex.Message)); }
                Thread.Sleep(Settings.PollMs);
            }
        }

        void CheckWebHold()
        {
            var st = engine.Get("/api/xhaptics/status");
            if (EngineClient.Str(st, "session.state") == "Idle" && WebUiAttached())
            {
                Log.Write("engine went idle again: Space Station page is holding the controller");
                Notify("The Space Station page is holding the controller, so X-Haptics is paused. Close that page to resume.", true);
                LastAction = "Paused by Space Station page";
            }
        }

        void Tick()
        {
            DateTime now = DateTime.Now;

            string devId; float sysPeak;
            bool haveDevice = SystemAudio.Read(out devId, out sysPeak);

            Dictionary<string, object> st;
            try { st = engine.Get("/api/xhaptics/status"); }
            catch (EngineException ex)
            {
                if (engineReachable) { Log.Write("engine unreachable: " + ex.Status + " " + Trim(ex.Message)); LastAction = ex.Status == 0 ? EngineProblemShort() : "Engine error"; }
                engineReachable = false;
                return;
            }
            if (!engineReachable) { Log.Write("engine reachable"); LastAction = "AUTO on"; }
            engineReachable = true;

            bool hid = EngineClient.Bool(st, "hid.connected");
            string sessionState = EngineClient.Str(st, "session.state") ?? "";
            string scope = EngineClient.Str(st, "session.scope");
            bool capRunning = EngineClient.Bool(st, "realtimeAudio.wasapi.running");
            bool outRunning = EngineClient.Bool(st, "realtimeAudio.output.running");
            string capErr = EngineClient.Str(st, "realtimeAudio.wasapi.lastError") ?? "";
            string outErr = EngineClient.Str(st, "realtimeAudio.output.lastError") ?? "";
            bool hidPaused = EngineClient.Bool(st, "hid.paused");
            long seq = EngineClient.Long(st, "realtimeAudio.wasapi.frameSeq");
            double enginePeak = EngineClient.Num(st, "realtimeAudio.wasapi.peak");
            // Per-game scopes capture only the game's process (process-loopback), so the system-wide
            // meter says nothing about what the engine should hear; only system capture is comparable.
            bool systemCapture = (EngineClient.Str(st, "realtimeAudio.wasapi.source") ?? "system") == "system";
            long capturePid = EngineClient.Long(st, "realtimeAudio.wasapi.processId");
            if (sessionState == "Running" && !string.IsNullOrEmpty(scope)) activeScope = scope;

            if (!initialised)
            {
                initialised = true; hidConnected = hid; defaultDeviceId = devId; lastSeq = seq;
                Log.Write(string.Format("monitoring: hid={0} session={1} scope={2} capture={3} output={4}", hid, sessionState, scope, capRunning, outRunning));
                return;
            }

            // 1) controller connected
            if (hid && !hidConnected) Schedule(now.AddSeconds(Settings.ReconnectKickDelaySec), "controller connected");
            hidConnected = hid;

            // 2) default playback device changed
            if (haveDevice && devId != defaultDeviceId)
            {
                Log.Write("default output changed: " + defaultDeviceId + " -> " + devId);
                defaultDeviceId = devId;
                Schedule(now.AddSeconds(Settings.DeviceChangeKickDelaySec), "default output device changed");
            }

            // 3) engine says the pipeline is down / errored.
            // An Idle session without errors is also what the engine shows while the Space Station page
            // holds the controller lease (mapping, firmware, screen upload), so Idle is only treated as
            // broken when no web client is attached and earlier re-arms did not bounce straight back.
            bool hasError = capErr.Length > 0 || outErr.Length > 0;
            bool idle = sessionState == "Idle" || (!capRunning && !outRunning);
            bool webAttached = WebUiAttached();
            if (sessionState == "Running" && capRunning && outRunning) idleBounces = 0;
            double sinceKick = (now - lastKickAt).TotalSeconds;
            if (lastKickWasIdle && sinceKick > 15) lastKickWasIdle = false;
            if (lastKickWasIdle && sinceKick >= 4 && idle) { idleBounces++; lastKickWasIdle = false; }
            bool broken = hid && !hidPaused && sessionState.IndexOf("Pause", StringComparison.OrdinalIgnoreCase) < 0 &&
                          (hasError || ((!capRunning || !outRunning) && !(idle && (webAttached || idleBounces >= Settings.MaxIdleBounces))));
            if (broken) { if (brokenSince == DateTime.MaxValue) brokenSince = now; }
            else brokenSince = DateTime.MaxValue;
            if (idle && !broken && hid && !loggedIdleHold)
            {
                loggedIdleHold = true;
                Log.Write(webAttached ? "engine idle while Space Station page is attached -> not interfering"
                                      : "engine keeps going idle right after re-arm -> waiting for it to resume on its own");
            }
            if (!idle) loggedIdleHold = false;

            // 4) audio is playing but capture counter is frozen
            if (seq != lastSeq) { lastSeq = seq; seqChangedAt = now; audioSecondsWhileStalled = 0; }
            else if (systemCapture && sysPeak > Settings.AudioPeakThreshold) audioSecondsWhileStalled++;
            bool stalled = hid && capRunning && (now - seqChangedAt).TotalSeconds >= Settings.StallSec &&
                           audioSecondsWhileStalled >= Settings.StallAudioSec;

            // 5) loud system audio while the engine hears silence (bound to the wrong endpoint)
            if (hid && capRunning && systemCapture && sysPeak > Settings.LoudPeakThreshold && enginePeak < Settings.EngineSilentPeak) silentMismatch++;
            else if ((!systemCapture || enginePeak >= Settings.EngineSilentPeak) && silentMismatch > 0) silentMismatch--;
            bool deaf = silentMismatch >= Settings.SilentMismatchKick;

            // 6) per-game capture still bound to a game process that has exited
            if (hid && capRunning && !systemCapture && capturePid > 0 && !ProcessAlive((int)capturePid))
            { if (deadPidSince == DateTime.MaxValue) deadPidSince = now; }
            else deadPidSince = DateTime.MaxValue;
            bool orphaned = (now - deadPidSince).TotalSeconds >= Settings.BrokenPersistSec;

            string reason = null;
            if (pendingKickAt.HasValue && now >= pendingKickAt.Value)
            {
                reason = pendingReason; pendingKickAt = null;
                if (hid && idle && webAttached) Log.Write("skip re-arm (" + reason + "): Space Station page is holding the controller");
                else if (hid) { Kick(reason, false); return; }
                else Log.Write("skip re-arm (" + reason + "): controller not connected");
                reason = null;
            }
            bool idleKick = false;
            // the engine needs a few seconds to come back after a re-arm; don't judge it before that
            if (broken && (now - brokenSince).TotalSeconds >= Settings.BrokenPersistSec && sinceKick >= Settings.RearmGraceSec)
            {
                idleKick = idle && !hasError;
                reason = string.Format("engine stopped (session={0}, capture={1}, output={2}{3}{4})", sessionState, capRunning, outRunning,
                                       capErr.Length > 0 ? ", capErr=" + capErr : "", outErr.Length > 0 ? ", outErr=" + outErr : "");
            }
            else if (stalled) reason = "audio is playing but the engine capture is frozen";
            else if (deaf) reason = "audio is playing but the engine only hears silence";
            else if (orphaned) { reason = "game exited but the engine is still bound to its process (PID " + capturePid + ")"; activeScope = "global"; }

            if (reason != null) { Kick(reason, false); lastKickWasIdle = idleKick; return; }

            if (!broken && !stalled && !deaf && !orphaned && hid)
            {
                if ((now - healthySince).TotalSeconds >= Settings.HealthyResetSec) backoffIndex = 0;
            }
            else healthySince = now;
        }

        void Schedule(DateTime at, string reason)
        {
            if (pendingKickAt.HasValue && pendingKickAt.Value > at) return;
            pendingKickAt = at; pendingReason = reason;
            Log.Write("scheduled re-arm at " + at.ToString("HH:mm:ss") + ": " + reason);
        }

        void Kick(string reason, bool manual) { Kick(reason, manual, true); }

        void Kick(string reason, bool manual, bool announce)
        {
            DateTime now = DateTime.Now;
            if (!manual && now < nextKickAllowed) return;

            try
            {
                var st = engine.Get("/api/xhaptics/status");
                if (!EngineClient.Bool(st, "hid.connected"))
                {
                    Log.Write("skip re-arm (" + reason + "): controller not connected");
                    if (manual) Notify("Controller is not connected.", true);
                    return;
                }
                string scope = EngineClient.Str(st, "session.scope");
                if (string.IsNullOrEmpty(scope)) scope = activeScope;

                var configs = EngineClient.Obj(EngineClient.At(engine.Get("/api/v1/xhaptics/configurations"), "configurations"));
                Dictionary<string, object> c = null;
                if (configs != null && configs.ContainsKey(scope)) c = EngineClient.Obj(configs[scope]);
                if ((c == null || !EngineClient.Bool(c, "enabled")) && scope != "global" && configs != null && configs.ContainsKey("global"))
                { scope = "global"; c = EngineClient.Obj(configs["global"]); }
                if (c == null || !EngineClient.Bool(c, "enabled"))
                {
                    Log.Write("skip re-arm (" + reason + "): X-Haptics is turned off in scope '" + scope + "'");
                    if (manual) Notify("X-Haptics is turned off. Enable it in Space Station first.", true);
                    nextKickAllowed = now.AddSeconds(30);
                    return;
                }

                Toggle(scope, c, false);
                Thread.Sleep(600);
                Toggle(scope, c, true);

                int wait = Settings.BackoffSec[Math.Min(backoffIndex, Settings.BackoffSec.Length - 1)];
                if (!manual) backoffIndex++;
                nextKickAllowed = DateTime.Now.AddSeconds(wait);
                brokenSince = DateTime.MaxValue; deadPidSince = DateTime.MaxValue; silentMismatch = 0; audioSecondsWhileStalled = 0;
                seqChangedAt = DateTime.Now; pendingKickAt = null; healthySince = DateTime.Now; lastKickAt = DateTime.Now;
                if (manual) { lastKickWasIdle = false; idleBounces = 0; loggedIdleHold = false; }

                LastAction = "Re-armed " + DateTime.Now.ToString("HH:mm:ss") + (manual ? "" : " (auto)");
                Log.Write("RE-ARMED scope=" + scope + " reason=" + reason + (manual ? "" : " (next auto re-arm allowed in " + wait + "s)"));
                if (manual && announce) Notify("X-Haptics re-armed. Press any controller button to wake it.", true);
                else Notify("X-Haptics re-armed automatically: " + reason, false);
            }
            catch (Exception ex)
            {
                Log.Write("re-arm failed (" + reason + "): " + Trim(ex.Message));
                if (manual) Notify("Re-arm failed. " + Explain(ex), true);
                nextKickAllowed = now.AddSeconds(10);
            }
        }

        // Manual messages always show; automatic ones at most once a minute.
        void Notify(string message, bool always)
        {
            if (!always && (DateTime.Now - lastBalloon).TotalSeconds < 60) return;
            lastBalloon = DateTime.Now;
            notify(message);
        }

        void Toggle(string scope, Dictionary<string, object> c, bool enabled)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                long rev = EngineClient.Long(engine.Get("/api/v1/xhaptics/configurations"), "revision");
                var body = new Dictionary<string, object>();
                body["expectedRevision"] = rev;
                body["enabled"] = enabled;
                body["selectedBackend"] = EngineClient.Str(c, "selectedBackend") ?? "realtime-audio";
                body["ui"] = EngineClient.At(c, "ui") ?? new Dictionary<string, object>();
                try { engine.Patch("/api/v1/xhaptics/configurations/" + Uri.EscapeDataString(scope), body); return; }
                catch (EngineException ex) { if (ex.Status != 409 || attempt == 1) throw; }
            }
        }

        static bool WebUiAttached()
        {
            try
            {
                foreach (var c in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                    if (c.State == System.Net.NetworkInformation.TcpState.Established && c.RemoteEndPoint.Port == Settings.WebUiPort) return true;
            }
            catch { }
            return false;
        }

        // ---- engine availability (only reported when the user triggers something; never at startup)

        public enum EngineAvailability { Installed, NotInstalled, NotRunning }

        public static EngineAvailability EngineStatus()
        {
            if (!File.Exists(Settings.EngineExePath)) return EngineAvailability.NotInstalled;
            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Settings.EngineExePath));
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return running ? EngineAvailability.Installed : EngineAvailability.NotRunning;
        }

        static bool EngineDown(Exception ex) { var ee = ex as EngineException; return ee != null && ee.Status == 0; }

        static string EngineProblemShort()
        {
            switch (EngineStatus())
            {
                case EngineAvailability.NotInstalled: return "Engine not installed";
                case EngineAvailability.NotRunning: return "Engine not running";
                default: return "Engine not responding";
            }
        }

        // What to tell the user after a failed action.
        static string Explain(Exception ex)
        {
            if (!EngineDown(ex)) return "Error: " + Trim(ex.Message);
            switch (EngineStatus())
            {
                case EngineAvailability.NotInstalled:
                    return "Flydigi xHaptics Engine is not installed (" + Settings.EngineExePath + "). Install it with Flydigi Space Station first, or fix engine.exePath in config.json.";
                case EngineAvailability.NotRunning:
                    return "Flydigi xHaptics Engine is not running. Start it (tray menu > Start xHaptics Engine) and try again.";
                default:
                    return "Flydigi xHaptics Engine is running but not answering on " + Settings.EngineBase + ". Wait a few seconds after it starts, or restart it.";
            }
        }

        static bool ProcessAlive(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch (ArgumentException) { return false; }
            catch { return true; }   // access denied etc. -> assume alive
        }

        static string Trim(string s) { s = (s ?? "").Replace("\r", " ").Replace("\n", " "); return s.Length > 200 ? s.Substring(0, 200) : s; }
    }

    // ---------------------------------------------------------------- Logging
    static class Log
    {
        static readonly object gate = new object();
        public static readonly string Dir = AppPaths.DataDir;
        public static readonly string FilePath = Path.Combine(Dir, "guard.log");
        public static void Write(string msg)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024) { File.Copy(FilePath, FilePath + ".old", true); File.Delete(FilePath); }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    // ---------------------------------------------------------------- Tray + hotkey
    class HotkeyWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int WM_HOTKEY = 0x0312; const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
        readonly List<Action> actions = new List<Action>();
        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        // Registers a hotkey like "Ctrl+Alt+R" / "Ctrl+Shift+F9"; returns an error message or null.
        public string Add(string spec, Action action)
        {
            if (string.IsNullOrEmpty(spec)) return null;   // disabled in config
            uint mods; Keys key;
            if (!TryParse(spec, out mods, out key)) return "cannot parse hotkey '" + spec + "'";
            actions.Add(action);
            if (!RegisterHotKey(Handle, actions.Count, mods | MOD_NOREPEAT, (uint)key)) return spec + " is already used by another app";
            return null;
        }

        public void Clear()
        {
            for (int i = 1; i <= actions.Count; i++) UnregisterHotKey(Handle, i);
            actions.Clear();
        }

        static bool TryParse(string spec, out uint mods, out Keys key)
        {
            mods = 0; key = Keys.None;
            foreach (var raw in spec.Split('+'))
            {
                string p = raw.Trim();
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= MOD_CONTROL; continue;
                    case "alt": mods |= MOD_ALT; continue;
                    case "shift": mods |= MOD_SHIFT; continue;
                    case "win": mods |= MOD_WIN; continue;
                }
                if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;   // "1" -> Keys.D1
                try { key = (Keys)Enum.Parse(typeof(Keys), p, true); }
                catch { return false; }
            }
            return key != Keys.None;
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id >= 1 && id <= actions.Count) actions[id - 1]();
            }
            base.WndProc(ref m);
        }
        public void Dispose() { Clear(); DestroyHandle(); }
    }

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly Guard guard;
        readonly HotkeyWindow hotkey;
        readonly ToolStripMenuItem autoItem, sourceItem, rearmItem;
        readonly System.Windows.Forms.Timer uiTimer;
        readonly SynchronizationContext ui;

        public TrayApp()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            string configError = Settings.Load();
            Icon icon = SystemIcons.Application;
            try { if (File.Exists(Settings.EngineExePath)) icon = Icon.ExtractAssociatedIcon(Settings.EngineExePath); } catch { }

            guard = new Guard(Notify, AutoFlag.Load());

            var menu = new ContextMenuStrip();
            autoItem = new ToolStripMenuItem("", null, delegate { ToggleAuto(); });
            autoItem.Checked = guard.AutoEnabled;
            menu.Items.Add(autoItem);
            sourceItem = new ToolStripMenuItem("", null, delegate { guard.RequestCycle(); });
            menu.Items.Add(sourceItem);
            var sourcesMenu = new ToolStripMenuItem("Audio sources");
            sourcesMenu.DropDownItems.Add("(loading)");   // placeholder so the submenu arrow shows
            sourcesMenu.DropDownOpening += delegate { FillSourcesMenu(sourcesMenu); };
            menu.Items.Add(sourcesMenu);
            rearmItem = new ToolStripMenuItem("", null, delegate { guard.RequestKick(); });
            menu.Items.Add(rearmItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open X-Haptics settings", null, delegate { OpenUi(); });
            menu.Items.Add("Edit config", null, delegate { try { Process.Start("notepad.exe", "\"" + AppPaths.ConfigPath + "\""); } catch { } });
            menu.Items.Add("Reload config", null, delegate { Reload(true); });
            menu.Items.Add("Reset config to defaults...", null, delegate { ResetConfig(); });
            menu.Items.Add("View log", null, delegate { try { Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            var startItem = new ToolStripMenuItem("Start with Windows");
            startItem.Click += delegate
            {
                bool on = !AutoStart.IsEnabled();
                try
                {
                    AutoStart.Set(on);
                    Log.Write("start with Windows " + (on ? "ON" : "OFF"));
                    Notify(on ? "X-Haptics Guard will start when you sign in." : "X-Haptics Guard will no longer start with Windows.");
                }
                catch (Exception ex) { Log.Write("start with Windows failed: " + ex.Message); Notify("Could not change start with Windows: " + ex.Message); }
            };
            menu.Items.Add(startItem);
            var engineItem = new ToolStripMenuItem("Start xHaptics Engine", null, delegate { StartEngine(); });
            menu.Items.Insert(0, engineItem);
            menu.Opening += delegate
            {
                startItem.Checked = AutoStart.IsEnabled();
                autoItem.Checked = guard.AutoEnabled;   // a failed switch keeps the previous state
                var status = Guard.EngineStatus();
                engineItem.Visible = status == Guard.EngineAvailability.NotRunning;
            };
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            tray = new NotifyIcon { Icon = icon, Text = "X-Haptics Guard", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { OpenUi(); };

            hotkey = new HotkeyWindow();
            if (configError != null)
            {
                Log.Write("config.json error: " + configError + " (using defaults)");
                Notify("config.json error: " + configError + ". Using defaults. Fix it or use tray > Reset config to defaults.");
            }
            RegisterHotkeys();

            var t = new Thread(guard.Run) { IsBackground = true, Name = "guard" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();

            // "HapticsGuard.exe --toggle" / "--kick" / "--cycle" / "--reload" from another process
            var reloadEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ReloadEventName);
            new Thread(delegate() { while (reloadEvent.WaitOne()) ui.Post(delegate { Reload(true); }, null); }) { IsBackground = true }.Start();
            var cycleEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.CycleEventName);
            new Thread(delegate() { while (cycleEvent.WaitOne()) guard.RequestCycle(); }) { IsBackground = true }.Start();
            var toggleEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ToggleEventName);
            new Thread(delegate() { while (toggleEvent.WaitOne()) ui.Post(delegate { ToggleAuto(); }, null); }) { IsBackground = true }.Start();
            var kickEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.KickEventName);
            new Thread(delegate() { while (kickEvent.WaitOne()) guard.RequestKick(); }) { IsBackground = true }.Start();

            uiTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            uiTimer.Tick += delegate
            {
                string text = "X-Haptics Guard [" + (guard.AutoEnabled ? "ON" : "OFF") + "] " + guard.LastAction;
                tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            };
            uiTimer.Start();
        }

        void ToggleAuto()
        {
            autoItem.Checked = guard.ToggleAuto();   // the guard thread reports the result with a balloon
        }

        // Lists processes playing audio + running registered games. Checked = in the switch-source cycle.
        void FillSourcesMenu(ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();
            var header = new ToolStripMenuItem("Checked = in the switch-source cycle") { Enabled = false };
            parent.DropDownItems.Add(header);
            parent.DropDownItems.Add(new ToolStripMenuItem("Mix (all apps)") { Checked = true, Enabled = false });
            parent.DropDownItems.Add(new ToolStripSeparator());
            List<Guard.SourceEntry> entries;
            try { entries = guard.SourceEntries(); }
            catch (Exception ex) { Log.Write("cannot list audio sources: " + ex.Message); entries = new List<Guard.SourceEntry>(); }
            if (entries.Count == 0) parent.DropDownItems.Add(new ToolStripMenuItem("(no app is playing audio)") { Enabled = false });
            foreach (var e in entries)
            {
                var entry = e;
                string text = entry.Label + "  [" + entry.Process + ".exe]" + (entry.GameId == null ? "  - not registered" : "");
                var item = new ToolStripMenuItem(text) { Checked = entry.Included };
                if (entry.GameId == null && string.IsNullOrEmpty(entry.Path)) item.Enabled = false;
                item.Click += delegate { guard.RequestSourceInclude(entry, !entry.Included); };
                parent.DropDownItems.Add(item);
            }
        }

        void StartEngine()
        {
            try
            {
                // same command line the engine's installer registers for autostart
                Process.Start(new ProcessStartInfo(Settings.EngineExePath, "--catalog-environment production")
                { WorkingDirectory = Path.GetDirectoryName(Settings.EngineExePath), UseShellExecute = true });
                Log.Write("started " + Settings.EngineExePath);
                Notify("Starting xHaptics Engine...");
            }
            catch (Exception ex) { Log.Write("cannot start engine: " + ex.Message); Notify("Cannot start xHaptics Engine: " + ex.Message); }
        }

        static string Shown(string hotkey) { return string.IsNullOrEmpty(hotkey) ? "" : " (" + hotkey + ")"; }

        void RegisterHotkeys()
        {
            hotkey.Clear();
            var errors = new List<string>();
            string e;
            if ((e = hotkey.Add(Settings.HotkeyToggle, ToggleAuto)) != null) errors.Add(e);
            if ((e = hotkey.Add(Settings.HotkeySwitchSource, guard.RequestCycle)) != null) errors.Add(e);
            if ((e = hotkey.Add(Settings.HotkeyRearmOnce, guard.RequestKick)) != null) errors.Add(e);
            autoItem.Text = "X-Haptics on + auto re-arm" + Shown(Settings.HotkeyToggle);
            sourceItem.Text = "Switch audio source" + Shown(Settings.HotkeySwitchSource);
            rearmItem.Text = "Re-arm X-Haptics once" + Shown(Settings.HotkeyRearmOnce);
            Log.Write("hotkeys: toggle=" + Settings.HotkeyToggle + " switchSource=" + Settings.HotkeySwitchSource + " rearmOnce=" + Settings.HotkeyRearmOnce);
            foreach (var err in errors) Log.Write("hotkey warning: " + err);
            if (errors.Count > 0) Notify("Hotkey problem: " + string.Join("; ", errors));
        }

        void Reload(bool announce)
        {
            string error = Settings.Load();
            if (error != null)
            {
                Log.Write("config.json error: " + error + " (keeping previous settings)");
                Notify("config.json error: " + error + ". Previous settings kept. Fix it or use tray > Reset config to defaults.");
                return;
            }
            RegisterHotkeys();
            Log.Write("config reloaded");
            if (announce) Notify("Config reloaded.");
        }

        void ResetConfig()
        {
            var answer = MessageBox.Show(
                "Replace config.json with the default settings?\n\nThe current file will be saved as data\\config.backup.json.",
                "X-Haptics Guard", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            try
            {
                string backup = Settings.ResetToDefaults();
                Log.Write("config reset to defaults" + (backup != null ? " (backup: " + backup + ")" : ""));
                Reload(false);
                Notify("Config reset to defaults." + (backup != null ? " Old file saved as data\\config.backup.json." : ""));
            }
            catch (Exception ex)
            {
                Log.Write("config reset failed: " + ex.Message);
                Notify("Config reset failed: " + ex.Message);
            }
        }

        static void OpenUi()
        {
            string browser = Settings.Browser;
            bool appWindow = false;
            if (string.IsNullOrEmpty(browser))
                foreach (var p in new[] {
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
                    Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Google\Chrome\Application\chrome.exe") })
                    if (File.Exists(p)) { browser = p; appWindow = true; break; }
            try
            {
                if (string.IsNullOrEmpty(browser)) Process.Start(Settings.UiUrl);
                else Process.Start(browser, appWindow ? "--app=\"" + Settings.UiUrl + "\"" : "\"" + Settings.UiUrl + "\"");
            }
            catch { }
        }

        void Notify(string message)
        {
            ui.Post(delegate { tray.ShowBalloonTip(3000, "X-Haptics Guard", message, ToolTipIcon.Info); }, null);
        }

        protected override void ExitThreadCore()
        {
            guard.Stop();
            uiTimer.Stop();
            hotkey.Dispose();
            tray.Visible = false;
            tray.Dispose();
            Log.Write("guard stopped");
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        public const string KickEventName = @"Local\FlydigiHapticsGuard.Kick";
        public const string ToggleEventName = @"Local\FlydigiHapticsGuard.Toggle";
        public const string CycleEventName = @"Local\FlydigiHapticsGuard.Cycle";
        public const string ReloadEventName = @"Local\FlydigiHapticsGuard.Reload";

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--kick" || args[0] == "--toggle" || args[0] == "--cycle" || args[0] == "--reload"))
            {
                string name = args[0] == "--kick" ? KickEventName : args[0] == "--toggle" ? ToggleEventName
                            : args[0] == "--cycle" ? CycleEventName : ReloadEventName;
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(name, out ev)) { ev.Set(); ev.Dispose(); }
                return;
            }
            bool created;
            using (var mutex = new Mutex(true, @"Local\FlydigiHapticsGuard", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
