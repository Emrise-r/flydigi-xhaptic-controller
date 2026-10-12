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
    // ---------------------------------------------------------------- Watchdog
    // Everything engine-related runs on this one worker thread; the UI only sets flags.
    partial class Guard
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


        readonly Queue<Action> work = new Queue<Action>();
        void Enqueue(Action a) { lock (work) work.Enqueue(a); }


        Dictionary<string, object> Configs()
        {
            return EngineClient.Obj(EngineClient.At(engine.Get("/api/v1/xhaptics/configurations"), "configurations"));
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
    }
}
