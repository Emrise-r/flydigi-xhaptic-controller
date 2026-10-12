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

namespace FlydigiHapticsGuard
{
    partial class Guard
    {
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
}
