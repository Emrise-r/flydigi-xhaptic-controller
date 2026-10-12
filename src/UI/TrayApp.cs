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
            // read-only status lines, refreshed when the menu opens
            var sourceInfoItem = new ToolStripMenuItem(guard.SourceInfo) { Enabled = false };
            var controllerInfoItem = new ToolStripMenuItem(guard.ControllerInfo) { Enabled = false };
            menu.Items.Add(sourceInfoItem);
            menu.Items.Add(controllerInfoItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Opening += delegate { sourceInfoItem.Text = guard.SourceInfo; controllerInfoItem.Text = guard.ControllerInfo; };
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
                string text = (entry.ParentLabel != null ? "      ↳ " : "") + entry.Label + "  [" + entry.Process + ".exe]" +
                              (entry.ParentLabel != null ? "  (in " + entry.ParentLabel + ")" : "") +
                              (entry.GameId == null ? "  - not registered" : "");
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
}
