using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace FlydigiHapticsGuard
{
    // Everything engine-related runs on this one worker thread; the UI only sets flags.
    partial class Guard
    {
        readonly EngineClient engine = new EngineClient();
        readonly Action<string> notify;
        volatile bool autoEnabled, manualKick, stopping;

        // monitor state (reset whenever AUTO is switched on)
        bool engineReachable = true, hidConnected, initialised;
        string defaultDeviceId, activeScope = EngineApi.GlobalScope;
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


        // a re-arm is disable -> ToggleGapMs -> enable
        const int ToggleGapMs = 600;
        // the engine only binds a game/app profile that is enabled while it sits on Mix, so a source switch
        // disables the running profiles and waits this long before enabling the target
        const int BindOnMixDelayMs = 1200;
        // after a source switch, check which process the engine actually captures
        const int VerifyBindingAfterSec = 4;

        readonly Queue<Action> work = new Queue<Action>();
        void Enqueue(Action a) { lock (work) work.Enqueue(a); }

        public void Stop() { stopping = true; }

        void ResetMonitor()
        {
            DateTime now = DateTime.Now;
            initialised = false; pendingKickAt = null;
            brokenSince = DateTime.MaxValue; deadPidSince = DateTime.MaxValue;
            healthySince = now; seqChangedAt = now; nextKickAllowed = DateTime.MinValue; lastKickAt = DateTime.MinValue;
            lastSeq = -1; audioSecondsWhileStalled = 0; silentMismatch = 0; backoffIndex = 0; idleBounces = 0;
            lastKickWasIdle = false; loggedIdleHold = false;
            exclusiveHold = false; nextExclusiveProbe = DateTime.MinValue;
            ClearFollow();
        }

        public void Run()
        {
            Log.Write("guard " + Application.ProductVersion + " started, switch " + (autoEnabled ? "ON" : "OFF"));
            Log.Write(AppPaths.UsingFallback
                ? "exe folder " + AppPaths.BaseDir + " is not writable; config and data are in " + AppPaths.UserDir
                : "config and data next to the exe: " + AppPaths.UserDir);
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
                    if (sourceCheckAt.HasValue && DateTime.Now >= sourceCheckAt.Value) { sourceCheckAt = null; if (ControllerConnected && !ControllerAsleep) CheckSourceProcess(); }
                    while (true)
                    {
                        Action job = null;
                        lock (work) if (work.Count > 0) job = work.Dequeue();
                        if (job == null) break;
                        job();
                    }
                    RestoreSourcesIfDone();
                    if (autoEnabled) Tick();
                    else if ((DateTime.Now - lastInfoRefresh).TotalSeconds >= 2) RefreshInfo(null);
                }
                catch (Exception ex) { Log.Write("error: " + Trim(ex.Message)); }
                Thread.Sleep(Settings.PollMs);
            }
        }


        // Manual messages always show; automatic ones at most once a minute.
        void Notify(string message, bool always)
        {
            if (!always && (DateTime.Now - lastBalloon).TotalSeconds < 60) return;
            lastBalloon = DateTime.Now;
            notify(message);
        }

        // Switches a profile for source switching / following. Its original flag is remembered in `originals`
        // (saved to sources.txt) and put back once its process has exited.
        void SwitchProfile(XHapticsConfigs configs, Dictionary<string, bool> originals, string scope, bool enabled)
        {
            if (!configs.Has(scope)) return;
            bool have = configs.IsEnabled(scope);
            if (!originals.ContainsKey(scope)) originals[scope] = have;
            if (have != enabled) engine.SetEnabled(configs, scope, enabled);
        }

        // Registers an app as a custom game and makes sure its profile is off, so the engine keeps capturing
        // Mix until the guard selects it. Returns the game id.
        string RegisterCustomGame(string name, string path)
        {
            string id = engine.AddCustomGame(name, path);
            var configs = engine.Configs();
            if (configs.IsEnabled(id)) engine.SetEnabled(configs, id, false);
            lastKickAt = DateTime.Now;
            Log.Write("registered " + path + " as custom game " + id + " (disabled)");
            return id;
        }

        // The engine rebuilds its capture after any profile change: don't judge it for a while.
        void ResetAfterSourceChange()
        {
            lastKickAt = DateTime.Now;
            brokenSince = DateTime.MaxValue; deadPidSince = DateTime.MaxValue; silentMismatch = 0;
        }
    }
}
