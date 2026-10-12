// X-Haptics Guard: tray helper for the Flydigi xHaptics Engine's local API (127.0.0.1:18757).
// Master switch, watchdog (re-arms X-Haptics when it stops) and audio source switching; see README.md and
// docs/HOW-IT-WORKS.md. Build with build.cmd (.NET Framework csc, C# 5).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace FlydigiHapticsGuard
{
    static class Program
    {
        public const string KickEventName = @"Local\FlydigiHapticsGuard.Kick";
        public const string ToggleEventName = @"Local\FlydigiHapticsGuard.Toggle";
        public const string CycleEventName = @"Local\FlydigiHapticsGuard.Cycle";
        public const string ReloadEventName = @"Local\FlydigiHapticsGuard.Reload";

        // "HapticsGuard.exe --toggle" etc. signals the running instance through a named event.
        static readonly Dictionary<string, string> Commands = new Dictionary<string, string>
        {
            { "--kick", KickEventName },
            { "--toggle", ToggleEventName },
            { "--cycle", CycleEventName },
            { "--reload", ReloadEventName },
        };

        [STAThread]
        static void Main(string[] args)
        {
            string eventName;
            if (args.Length > 0 && Commands.TryGetValue(args[0], out eventName))
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(eventName, out ev)) { ev.Set(); ev.Dispose(); }
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
