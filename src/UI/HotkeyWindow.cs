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
}
