using System;
using System.Threading;

namespace FlydigiHapticsGuard
{
    partial class Guard
    {
        void CheckWebHold()
        {
            if (engine.Status().Idle && WebUiAttached())
            {
                Log.Write("engine went idle again: Space Station page is holding the controller");
                Notify("The Space Station page is holding the controller, so X-Haptics is paused. Close that page to resume.", true);
                LastAction = "Paused by Space Station page";
            }
        }

        void Tick()
        {
            var p = ReadPoll();
            if (p == null) return;
            if (!initialised) { StartMonitoring(p); return; }

            // 0) a child process of the captured game plays audio the engine does not hear
            // (skipped while the controller is asleep: nothing would vibrate anyway)
            if ((followParentScope != null || !ControllerAsleep) && FollowChildAudio(p.Now, p.St)) return;

            ScheduleOnReconnect(p);                  // 1) re-armed later by RunScheduledKick
            ScheduleOnDeviceChange(p);               // 2)
            p.Broken = EngineBroken(p);              // 3)
            if (WaitForExclusivePlayer(p)) return;   // 3b)
            LogIdleHold(p);

            // every rule runs on every poll so its counters stay current; the first reason wins
            string stopped = EngineStopped(p), frozen = CaptureFrozen(p), deaf = EngineDeaf(p), orphaned = CaptureOrphaned(p);
            if (RunScheduledKick(p)) return;

            string reason = stopped ?? frozen ?? deaf ?? orphaned;
            if (reason != null)
            {
                if (reason == orphaned) activeScope = EngineApi.GlobalScope;   // its game is gone
                Kick(reason, false);
                lastKickWasIdle = reason == stopped && p.Idle && !p.St.HasError;
                return;
            }
            TrackHealthy(p, p.Broken || frozen != null || deaf != null || orphaned != null);
        }

        // Reads everything one poll needs. Returns null while the engine cannot be reached.
        Poll ReadPoll()
        {
            var p = new Poll { Now = DateTime.Now };
            p.HaveDevice = SystemAudio.Read(out p.DeviceId, out p.SystemPeak);
            try { p.St = engine.Status(); }
            catch (EngineException ex)
            {
                if (engineReachable) { Log.Write("engine unreachable: " + ex.Status + " " + Trim(ex.Message)); LastAction = ex.Status == 0 ? EngineProblemShort() : "Engine error"; }
                ShowEngineNotRunning();
                engineReachable = false;
                return null;
            }
            if (!engineReachable) { Log.Write("engine reachable (" + engine.Version + ")"); LastAction = "AUTO on"; }
            engineReachable = true;

            RefreshInfo(p.St);
            if (p.St.Running && !string.IsNullOrEmpty(p.St.Scope)) activeScope = p.St.Scope;
            p.Idle = p.St.Idle || (!p.St.CaptureRunning && !p.St.OutputRunning);
            p.Awake = p.St.HidConnected && !ControllerAsleep;
            p.SinceKick = (p.Now - lastKickAt).TotalSeconds;
            p.WebAttached = WebUiAttached();
            p.NativeGame = NativeGameRunning(p);
            return p;
        }

        // first poll after start or after the switch is turned on: remember the baseline, judge nothing
        void StartMonitoring(Poll p)
        {
            var st = p.St;
            initialised = true; hidConnected = st.HidConnected; defaultDeviceId = p.DeviceId; lastSeq = st.FrameSeq;
            Log.Write(string.Format("monitoring: engine={0} hid={1} session={2} scope={3} capture={4} output={5}",
                                    engine.Version, st.HidConnected, st.SessionState, st.Scope, st.CaptureRunning, st.OutputRunning));
        }

        // A re-arm scheduled by rule 1 or 2 is due. Returns true when it re-armed.
        internal bool RunScheduledKick(Poll p)
        {
            if (!pendingKickAt.HasValue || p.Now < pendingKickAt.Value) return false;
            if (p.Now < nextKickAllowed)
            {
                // still in the back-off of the previous re-arm (Kick would drop it): keep it scheduled
                pendingKickAt = nextKickAllowed;
                Log.Write("re-arm (" + pendingReason + ") postponed to " + nextKickAllowed.ToString("HH:mm:ss") + ": back-off after the previous re-arm");
                return false;
            }
            string reason = pendingReason;
            pendingKickAt = null;
            var st = p.St;
            bool healthy = st.Running && st.CaptureRunning && st.OutputRunning && !st.HasError && st.HidOpened;
            if (!st.HidConnected) Log.Write("skip re-arm (" + reason + "): controller not connected");
            else if (p.Idle && p.WebAttached) Log.Write("skip re-arm (" + reason + "): Space Station page is holding the controller");
            else if (p.Idle && p.NativeGame != null) Log.Write("skip re-arm (" + reason + "): " + p.NativeGame + " drives the haptics itself");
            else if (healthy && reason == ReasonReconnect) Log.Write("skip re-arm (" + reason + "): the engine already recovered on its own");
            else if (reason == ReasonDeviceChanged && EngineFollowsDevice(p)) Log.Write("skip re-arm (" + reason + "): the engine already follows the new device");
            else { Kick(reason, false); return true; }
            return false;
        }

        internal void Schedule(DateTime at, string reason)
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
                var st = engine.Status();
                if (!st.HidConnected)
                {
                    Log.Write("skip re-arm (" + reason + "): controller not connected");
                    if (manual) Notify("Controller is not connected.", true);
                    return;
                }
                string scope = st.Scope;
                if (string.IsNullOrEmpty(scope)) scope = activeScope;

                var configs = engine.Configs();
                if (!configs.IsEnabled(scope) && scope != EngineApi.GlobalScope && configs.Has(EngineApi.GlobalScope))
                    scope = EngineApi.GlobalScope;
                if (!configs.IsEnabled(scope))
                {
                    Log.Write("skip re-arm (" + reason + "): X-Haptics is turned off in scope '" + scope + "'");
                    if (manual) Notify("X-Haptics is turned off in Space Station" + TurnOnHint() + ".", true);
                    nextKickAllowed = now.AddSeconds(30);
                    return;
                }

                engine.SetEnabled(configs, scope, false);
                Thread.Sleep(ToggleGapMs);
                engine.SetEnabled(configs, scope, true);

                int wait = Settings.BackoffSec[Math.Min(backoffIndex, Settings.BackoffSec.Length - 1)];
                if (!manual) backoffIndex++;
                nextKickAllowed = DateTime.Now.AddSeconds(wait);
                ResetAfterSourceChange();
                audioSecondsWhileStalled = 0; seqChangedAt = DateTime.Now; pendingKickAt = null; healthySince = DateTime.Now;
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
    }
}
