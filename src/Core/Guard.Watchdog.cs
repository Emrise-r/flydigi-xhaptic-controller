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
        // set while the default output device is held by an exclusive-mode / ASIO player
        bool exclusiveHold;
        DateTime nextExclusiveProbe = DateTime.MinValue;

        void CheckWebHold()
        {
            var st = engine.Get("/api/xhaptics/status");
            if (EngineClient.Str(st, "session.state") == "Idle" && WebUiAttached())
            {
                Log.Write("engine went idle again: Space Station page is holding the controller");
                Notify("The Space Station page is holding the controller, so X-Haptics is paused. Close that page to resume.", true);
                LastAction = "Paused by Space Station page";
            }
        }

        void Tick()
        {
            DateTime now = DateTime.Now;

            string devId; float sysPeak;
            bool haveDevice = SystemAudio.Read(out devId, out sysPeak);

            Dictionary<string, object> st;
            try { st = engine.Get("/api/xhaptics/status"); }
            catch (EngineException ex)
            {
                if (engineReachable) { Log.Write("engine unreachable: " + ex.Status + " " + Trim(ex.Message)); LastAction = ex.Status == 0 ? EngineProblemShort() : "Engine error"; }
                SourceInfo = "Source: engine not running"; ControllerInfo = "Controller: unknown (engine not running)";
                engineReachable = false;
                return;
            }
            if (!engineReachable) { Log.Write("engine reachable"); LastAction = "AUTO on"; }
            engineReachable = true;

            RefreshInfo(st);
            bool hid = EngineClient.Bool(st, "hid.connected");
            string sessionState = EngineClient.Str(st, "session.state") ?? "";
            string scope = EngineClient.Str(st, "session.scope");
            bool capRunning = EngineClient.Bool(st, "realtimeAudio.wasapi.running");
            bool outRunning = EngineClient.Bool(st, "realtimeAudio.output.running");
            string capErr = EngineClient.Str(st, "realtimeAudio.wasapi.lastError") ?? "";
            string outErr = EngineClient.Str(st, "realtimeAudio.output.lastError") ?? "";
            bool hidPaused = EngineClient.Bool(st, "hid.paused");
            long seq = EngineClient.Long(st, "realtimeAudio.wasapi.frameSeq");
            double enginePeak = EngineClient.Num(st, "realtimeAudio.wasapi.peak");
            // Per-game scopes capture only the game's process (process-loopback), so the system-wide
            // meter says nothing about what the engine should hear; only system capture is comparable.
            bool systemCapture = (EngineClient.Str(st, "realtimeAudio.wasapi.source") ?? "system") == "system";
            long capturePid = EngineClient.Long(st, "realtimeAudio.wasapi.processId");
            if (sessionState == "Running" && !string.IsNullOrEmpty(scope)) activeScope = scope;

            if (!initialised)
            {
                initialised = true; hidConnected = hid; defaultDeviceId = devId; lastSeq = seq;
                Log.Write(string.Format("monitoring: hid={0} session={1} scope={2} capture={3} output={4}", hid, sessionState, scope, capRunning, outRunning));
                return;
            }

            // 0) a child process of the captured game plays audio the engine does not hear
            // (skipped while the controller is asleep: nothing would vibrate anyway)
            if ((followParentScope != null || !ControllerAsleep) &&
                FollowChildAudio(now, hid, capRunning, systemCapture, capturePid, scope, enginePeak)) return;

            // 1) controller connected
            if (hid && !hidConnected) Schedule(now.AddSeconds(Settings.ReconnectKickDelaySec), "controller connected");
            hidConnected = hid;

            // 2) default playback device changed
            if (haveDevice && devId != defaultDeviceId)
            {
                Log.Write("default output changed: " + defaultDeviceId + " -> " + devId);
                defaultDeviceId = devId;
                Schedule(now.AddSeconds(Settings.DeviceChangeKickDelaySec), "default output device changed");
            }

            // 3) engine says the pipeline is down / errored.
            // An Idle session without errors is also what the engine shows while the Space Station page
            // holds the controller lease (mapping, firmware, screen upload), so Idle is only treated as
            // broken when no web client is attached and earlier re-arms did not bounce straight back.
            bool hasError = capErr.Length > 0 || outErr.Length > 0;
            bool idle = sessionState == "Idle" || (!capRunning && !outRunning);
            bool webAttached = WebUiAttached();
            if (sessionState == "Running" && capRunning && outRunning) idleBounces = 0;
            double sinceKick = (now - lastKickAt).TotalSeconds;
            if (lastKickWasIdle && sinceKick > 15) lastKickWasIdle = false;
            if (lastKickWasIdle && sinceKick >= 4 && idle) { idleBounces++; lastKickWasIdle = false; }
            bool broken = hid && !hidPaused && sessionState.IndexOf("Pause", StringComparison.OrdinalIgnoreCase) < 0 &&
                          (hasError || ((!capRunning || !outRunning) && !(idle && (webAttached || idleBounces >= Settings.MaxIdleBounces))));
            if (broken) { if (brokenSince == DateTime.MaxValue) brokenSince = now; }
            else brokenSince = DateTime.MaxValue;

            // 3b) the output device is held by an exclusive-mode / ASIO player (TIDAL "Exclusive Mode",
            // foobar2000 WASAPI exclusive...): re-arming is useless until it lets go, so wait, then re-arm once.
            if ((broken || exclusiveHold) && now >= nextExclusiveProbe)
            {
                nextExclusiveProbe = now.AddSeconds(2);
                bool? inUse = SystemAudio.DefaultDeviceInExclusiveUse();
                if (inUse == true && !exclusiveHold)
                {
                    exclusiveHold = true;
                    Log.Write("output device is in exclusive use by another app (" + Trim(capErr) + "); waiting for it to be released");
                    Notify("Another app is using the audio device in exclusive mode (e.g. TIDAL Exclusive Mode, foobar2000 WASAPI exclusive/ASIO). " +
                           "Haptics resume when it releases the device.", true);
                }
                else if (inUse == false && exclusiveHold)
                {
                    exclusiveHold = false;
                    Log.Write("output device released by the exclusive-mode app -> re-arm");
                    backoffIndex = 0; nextKickAllowed = DateTime.MinValue;
                    Kick("output device released by an exclusive-mode app", false);
                    return;
                }
            }
            if (exclusiveHold) brokenSince = DateTime.MaxValue;   // no futile re-arms while the device is held
            if (idle && !broken && hid && !loggedIdleHold)
            {
                loggedIdleHold = true;
                Log.Write(webAttached ? "engine idle while Space Station page is attached -> not interfering"
                                      : "engine keeps going idle right after re-arm -> waiting for it to resume on its own");
            }
            if (!idle) loggedIdleHold = false;

            // 4) and 5) compare audio levels: skipped while the controller is asleep (no output either way)
            bool awake = hid && !ControllerAsleep;

            // 4) audio is playing but capture counter is frozen
            if (seq != lastSeq) { lastSeq = seq; seqChangedAt = now; audioSecondsWhileStalled = 0; }
            else if (awake && systemCapture && sysPeak > Settings.AudioPeakThreshold) audioSecondsWhileStalled++;
            bool stalled = awake && capRunning && (now - seqChangedAt).TotalSeconds >= Settings.StallSec &&
                           audioSecondsWhileStalled >= Settings.StallAudioSec;

            // 5) loud system audio while the engine hears silence (bound to the wrong endpoint)
            if (awake && capRunning && systemCapture && sysPeak > Settings.LoudPeakThreshold && enginePeak < Settings.EngineSilentPeak) silentMismatch++;
            else if ((!systemCapture || enginePeak >= Settings.EngineSilentPeak) && silentMismatch > 0) silentMismatch--;
            bool deaf = silentMismatch >= Settings.SilentMismatchKick;

            // 6) per-game capture still bound to a game process that has exited
            if (hid && capRunning && !systemCapture && capturePid > 0 && !ProcessAlive((int)capturePid))
            { if (deadPidSince == DateTime.MaxValue) deadPidSince = now; }
            else deadPidSince = DateTime.MaxValue;
            bool orphaned = (now - deadPidSince).TotalSeconds >= Settings.BrokenPersistSec;

            string reason = null;
            if (pendingKickAt.HasValue && now >= pendingKickAt.Value)
            {
                reason = pendingReason; pendingKickAt = null;
                bool healthy = sessionState == "Running" && capRunning && outRunning && !hasError && EngineClient.Bool(st, "hid.opened");
                if (hid && idle && webAttached) Log.Write("skip re-arm (" + reason + "): Space Station page is holding the controller");
                else if (hid && healthy && reason == "controller connected") Log.Write("skip re-arm (" + reason + "): the engine already recovered on its own");
                else if (hid) { Kick(reason, false); return; }
                else Log.Write("skip re-arm (" + reason + "): controller not connected");
                reason = null;
            }
            bool idleKick = false;
            // the engine needs a few seconds to come back after a re-arm; don't judge it before that
            if (broken && (now - brokenSince).TotalSeconds >= Settings.BrokenPersistSec && sinceKick >= Settings.RearmGraceSec)
            {
                idleKick = idle && !hasError;
                reason = string.Format("engine stopped (session={0}, capture={1}, output={2}{3}{4})", sessionState, capRunning, outRunning,
                                       capErr.Length > 0 ? ", capErr=" + capErr : "", outErr.Length > 0 ? ", outErr=" + outErr : "");
            }
            else if (stalled) reason = "audio is playing but the engine capture is frozen";
            else if (deaf) reason = "audio is playing but the engine only hears silence";
            else if (orphaned) { reason = "game exited but the engine is still bound to its process (PID " + capturePid + ")"; activeScope = "global"; }

            if (reason != null) { Kick(reason, false); lastKickWasIdle = idleKick; return; }

            if (!broken && !stalled && !deaf && !orphaned && hid)
            {
                if ((now - healthySince).TotalSeconds >= Settings.HealthyResetSec) backoffIndex = 0;
            }
            else healthySince = now;
        }

        void Schedule(DateTime at, string reason)
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
                var st = engine.Get("/api/xhaptics/status");
                if (!EngineClient.Bool(st, "hid.connected"))
                {
                    Log.Write("skip re-arm (" + reason + "): controller not connected");
                    if (manual) Notify("Controller is not connected.", true);
                    return;
                }
                string scope = EngineClient.Str(st, "session.scope");
                if (string.IsNullOrEmpty(scope)) scope = activeScope;

                var configs = EngineClient.Obj(EngineClient.At(engine.Get("/api/v1/xhaptics/configurations"), "configurations"));
                Dictionary<string, object> c = null;
                if (configs != null && configs.ContainsKey(scope)) c = EngineClient.Obj(configs[scope]);
                if ((c == null || !EngineClient.Bool(c, "enabled")) && scope != "global" && configs != null && configs.ContainsKey("global"))
                { scope = "global"; c = EngineClient.Obj(configs["global"]); }
                if (c == null || !EngineClient.Bool(c, "enabled"))
                {
                    // logged once by TurnedOffElsewhere() for the global case; other scopes still get a line
                    if (manual || scope != "global" || !turnedOffNoticed)
                        Log.Write("skip re-arm (" + reason + "): X-Haptics is turned off in scope '" + scope + "'");
                    if (manual) Notify("X-Haptics is turned off in Space Station" + TurnOnHint() + ".", true);
                    nextKickAllowed = now.AddSeconds(30);
                    return;
                }

                Toggle(scope, c, false);
                Thread.Sleep(600);
                Toggle(scope, c, true);

                int wait = Settings.BackoffSec[Math.Min(backoffIndex, Settings.BackoffSec.Length - 1)];
                if (!manual) backoffIndex++;
                nextKickAllowed = DateTime.Now.AddSeconds(wait);
                brokenSince = DateTime.MaxValue; deadPidSince = DateTime.MaxValue; silentMismatch = 0; audioSecondsWhileStalled = 0;
                seqChangedAt = DateTime.Now; pendingKickAt = null; healthySince = DateTime.Now; lastKickAt = DateTime.Now;
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
