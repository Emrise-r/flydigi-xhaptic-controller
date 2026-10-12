using System;
using System.Collections.Generic;

namespace FlydigiHapticsGuard
{
    partial class Guard
    {
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
            var configs = engine.Configs();
            if (configs.IsEmpty) throw new EngineException(0, "no configurations");
            if (!on)
            {
                // Disable the running scope and global, otherwise the engine just falls back to global.
                string scope = engine.Status().Scope;
                var targets = new List<string>();
                if (!string.IsNullOrEmpty(scope)) targets.Add(scope);
                if (!targets.Contains(EngineApi.GlobalScope)) targets.Add(EngineApi.GlobalScope);
                var disabled = new List<string>();
                foreach (var s in targets)
                    if (configs.IsEnabled(s)) { engine.SetEnabled(configs, s, false); disabled.Add(s); }
                AutoFlag.Save(false, disabled);
                Log.Write("X-Haptics turned OFF (scopes: " + (disabled.Count > 0 ? string.Join(",", disabled) : "already off") + ")");
                LastAction = "X-Haptics off";
                Notify("X-Haptics OFF. It stays off until you press the button again.", true);
            }
            else
            {
                var scopes = AutoFlag.LoadScopes();
                if (scopes.Count == 0) scopes.Add(EngineApi.GlobalScope);
                var enabled = new List<string>();
                foreach (var s in scopes)
                    if (configs.Has(s) && !configs.IsEnabled(s)) { engine.SetEnabled(configs, s, true); enabled.Add(s); }
                AutoFlag.Save(true, null);
                // enabling starts a fresh pipeline; if it was already on, re-arm it instead
                if (enabled.Count == 0) Kick("switch on", true, false);
                else { lastKickAt = DateTime.Now; Log.Write("X-Haptics turned ON (scopes: " + string.Join(",", enabled) + ")"); }
                LastAction = "X-Haptics on, auto re-arm active";
                Notify("X-Haptics ON. Auto re-arm is active. Press any controller button to wake it.", true);
            }
        }

        public void RequestKick() { manualKick = true; }
    }
}
