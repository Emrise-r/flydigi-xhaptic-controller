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

namespace FlydigiHapticsGuard
{
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
