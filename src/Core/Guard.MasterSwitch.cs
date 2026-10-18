using System;
using System.Collections.Generic;

namespace FlydigiHapticsGuard
{
    partial class Guard
    {
        // Master switch: off = X-Haptics disabled and no auto re-arm; on = X-Haptics enabled and watched.
        // Its real state is the engine's global profile, which Space Station (or an engine update) can change
        // too: a press goes by the engine's state, and the switch follows changes made outside the guard.

        const int SwitchSyncEverySec = 3;

        volatile bool toggleRequested;
        DateTime nextSwitchSync = DateTime.MinValue;
        bool? switchCandidate;       // engine state seen once, waiting for a second reading
        bool switchSyncedOnce;       // false until the first successful engine reading after start

        // Called on the UI thread; the worker decides from the engine. Returns the expected new state.
        public bool ToggleAuto()
        {
            toggleRequested = true;
            return !autoEnabled;
        }

        // Is X-Haptics on in the engine (global profile enabled)? null when the engine cannot be asked.
        bool? EngineSwitchState()
        {
            try
            {
                var configs = engine.Configs();
                return configs.Has(EngineApi.GlobalScope) ? configs.IsEnabled(EngineApi.GlobalScope) : (bool?)null;
            }
            catch (EngineException) { return null; }
        }

        void HandleToggle()
        {
            bool? engineOn = EngineSwitchState();
            bool previous = autoEnabled;
            bool on = !(engineOn ?? previous);
            autoEnabled = on;
            switchCandidate = null;
            Log.Write("switch " + (on ? "ON" : "OFF") +
                      (engineOn.HasValue && engineOn.Value != previous ? " (X-Haptics was already " + (engineOn.Value ? "on" : "off") + " in the engine)" : ""));
            LastAction = on ? "on" : "off";
            ResetMonitor();
            try { ApplySwitch(on); }
            catch (Exception ex)
            {
                // nothing was switched: keep the previous state (menu + auto-mode.txt stay consistent)
                autoEnabled = previous;
                LastAction = EngineDown(ex) ? EngineProblemShort() : "Switch failed";
                Log.Write("switch failed: " + Trim(ex.Message));
                Notify("X-Haptics was not switched " + (on ? "on" : "off") + ". " + Explain(ex), true);
            }
        }

        // What the switch should become after one engine reading, or null to leave it: the reading is equal,
        // unknown, taken during the guard's own re-arm / source switch, or a change not confirmed by a second
        // reading yet. The first reading after start is followed straight away.
        internal static bool? FollowEngine(bool? engineOn, bool switchOn, bool firstReading, bool inGrace, ref bool? candidate)
        {
            if (engineOn == null) return null;
            if (inGrace || engineOn.Value == switchOn) { candidate = null; return null; }
            if (!firstReading && candidate != engineOn) { candidate = engineOn; return null; }
            candidate = null;
            return engineOn.Value;
        }

        void SyncSwitchWithEngine()
        {
            if (toggleRequested || DateTime.Now < nextSwitchSync) return;
            nextSwitchSync = DateTime.Now.AddSeconds(SwitchSyncEverySec);
            bool? engineOn = EngineSwitchState();
            if (engineOn == null) return;
            bool first = !switchSyncedOnce;
            switchSyncedOnce = true;
            bool inGrace = (DateTime.Now - lastKickAt).TotalSeconds < Settings.RearmGraceSec;
            bool? follow = FollowEngine(engineOn, autoEnabled, first, inGrace, ref switchCandidate);
            if (follow == null) return;

            autoEnabled = follow.Value;
            AutoFlag.Save(autoEnabled, null);
            ResetMonitor();
            if (autoEnabled) lastKickAt = DateTime.Now;   // just switched on: give the engine the usual time to start
            Log.Write("switch " + (autoEnabled ? "ON" : "OFF") + ": X-Haptics is " + (autoEnabled ? "on" : "off") + " in the engine" +
                      (first ? " (at start)" : " (changed outside the guard, e.g. in Space Station)"));
            LastAction = autoEnabled ? "X-Haptics on, auto re-arm active" : "X-Haptics off";
            if (!first)
                Notify(autoEnabled ? "X-Haptics was turned on outside the guard. Auto re-arm is active."
                                   : "X-Haptics was turned off outside the guard (e.g. in Space Station). Auto re-arm is off.", true);
        }

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
