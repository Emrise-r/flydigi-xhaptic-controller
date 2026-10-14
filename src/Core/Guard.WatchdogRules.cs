using System;

namespace FlydigiHapticsGuard
{
    // The watchdog's re-arm rules. Tick() calls them in priority order once per poll. A rule that keeps a
    // counter updates it on every poll, and returns the reason to re-arm now (or null).
    partial class Guard
    {
        // What one watchdog poll sees; read once by ReadPoll() and shared by the rules.
        internal class Poll
        {
            public DateTime Now;
            public XHapticsStatus St;
            public bool HaveDevice;
            public string DeviceId;      // Windows default playback device
            public float SystemPeak;     // its peak meter
            public bool WebAttached;     // the Space Station page is connected to the engine
            public bool Idle;            // session Idle, or capture and output both stopped
            public bool Awake;           // controller connected and haptics not asleep: audio levels mean something
            public double SinceKick;     // seconds since the last re-arm or source switch
            public bool Broken;          // rule 3, set by EngineBroken()
        }

        // reasons of the re-arms that rules 1 and 2 schedule
        internal const string ReasonReconnect = "controller connected", ReasonDeviceChanged = "default output device changed";

        // set while the default output device is held by an exclusive-mode / ASIO player
        bool exclusiveHold;
        DateTime nextExclusiveProbe = DateTime.MinValue;

        // 1) controller (re)connected -> re-arm after reconnectDelaySec, unless the engine recovers by itself
        void ScheduleOnReconnect(Poll p)
        {
            if (p.St.HidConnected && !hidConnected) Schedule(p.Now.AddSeconds(Settings.ReconnectKickDelaySec), ReasonReconnect);
            hidConnected = p.St.HidConnected;
        }

        // 2) Windows default playback device changed
        void ScheduleOnDeviceChange(Poll p)
        {
            if (!p.HaveDevice || p.DeviceId == defaultDeviceId) return;
            Log.Write("default output changed: " + defaultDeviceId + " -> " + p.DeviceId);
            defaultDeviceId = p.DeviceId;
            Schedule(p.Now.AddSeconds(Settings.DeviceChangeKickDelaySec), ReasonDeviceChanged);
        }

        // 3) the engine says the pipeline is down or errored; brokenSince tracks for how long.
        // An Idle session without errors is also what the engine shows while the Space Station page holds the
        // controller lease (mapping, firmware, screen upload), so Idle only counts as broken when no web client
        // is attached and earlier re-arms did not bounce straight back to Idle.
        internal bool EngineBroken(Poll p)
        {
            var st = p.St;
            if (st.Running && st.CaptureRunning && st.OutputRunning) idleBounces = 0;
            if (lastKickWasIdle && p.SinceKick > 15) lastKickWasIdle = false;
            if (lastKickWasIdle && p.SinceKick >= 4 && p.Idle) { idleBounces++; lastKickWasIdle = false; }

            bool idleOnPurpose = p.Idle && (p.WebAttached || idleBounces >= Settings.MaxIdleBounces);
            bool broken = st.HidConnected && !st.HidPaused && !st.PausedSession &&
                          (st.HasError || ((!st.CaptureRunning || !st.OutputRunning) && !idleOnPurpose));
            if (!broken) brokenSince = DateTime.MaxValue;
            else if (brokenSince == DateTime.MaxValue) brokenSince = p.Now;
            return broken;
        }

        // 3b) the output device is held by an exclusive-mode / ASIO player (TIDAL "Exclusive Mode", foobar2000
        // WASAPI exclusive...): re-arming is useless until it lets go, so wait, then re-arm once.
        // Returns true when it re-armed.
        bool WaitForExclusivePlayer(Poll p)
        {
            if ((p.Broken || exclusiveHold) && p.Now >= nextExclusiveProbe)
            {
                nextExclusiveProbe = p.Now.AddSeconds(2);
                bool? inUse = SystemAudio.DefaultDeviceInExclusiveUse();
                if (inUse == true && !exclusiveHold)
                {
                    exclusiveHold = true;
                    Log.Write("output device is in exclusive use by another app (" + Trim(p.St.CaptureError) + "); waiting for it to be released");
                    Notify("Another app is using the audio device in exclusive mode (e.g. TIDAL Exclusive Mode, foobar2000 WASAPI exclusive/ASIO). " +
                           "Haptics resume when it releases the device.", true);
                }
                else if (inUse == false && exclusiveHold)
                {
                    exclusiveHold = false;
                    Log.Write("output device released by the exclusive-mode app -> re-arm");
                    backoffIndex = 0; nextKickAllowed = DateTime.MinValue;
                    Kick("output device released by an exclusive-mode app", false);
                    return true;
                }
            }
            if (exclusiveHold) brokenSince = DateTime.MaxValue;   // no futile re-arms while the device is held
            return false;
        }

        // says once why an idle engine is left alone
        void LogIdleHold(Poll p)
        {
            if (!p.Idle) { loggedIdleHold = false; return; }
            if (p.Broken || !p.St.HidConnected || loggedIdleHold) return;
            loggedIdleHold = true;
            Log.Write(p.WebAttached ? "engine idle while Space Station page is attached -> not interfering"
                                    : "engine keeps going idle right after re-arm -> waiting for it to resume on its own");
        }

        // 3) as a re-arm reason: broken for brokenPersistSec, and not judged right after a re-arm (the engine
        // needs a few seconds to come back)
        internal string EngineStopped(Poll p)
        {
            if (!p.Broken || (p.Now - brokenSince).TotalSeconds < Settings.BrokenPersistSec || p.SinceKick < Settings.RearmGraceSec) return null;
            var st = p.St;
            return string.Format("engine stopped (session={0}, capture={1}, output={2}{3}{4})", st.SessionState, st.CaptureRunning, st.OutputRunning,
                                 st.CaptureError.Length > 0 ? ", capErr=" + st.CaptureError : "",
                                 st.OutputError.Length > 0 ? ", outErr=" + st.OutputError : "");
        }

        // 4) audio is playing but the engine's capture counter is frozen.
        // Per-game scopes capture only the game's process (process-loopback), so the system-wide meter says
        // nothing about what the engine should hear; rules 4 and 5 only compare levels for system capture.
        internal string CaptureFrozen(Poll p)
        {
            var st = p.St;
            if (st.FrameSeq != lastSeq) { lastSeq = st.FrameSeq; seqChangedAt = p.Now; audioSecondsWhileStalled = 0; }
            else if (p.Awake && st.SystemCapture && p.SystemPeak > Settings.AudioPeakThreshold) audioSecondsWhileStalled++;
            bool frozen = p.Awake && st.CaptureRunning && (p.Now - seqChangedAt).TotalSeconds >= Settings.StallSec &&
                          audioSecondsWhileStalled >= Settings.StallAudioSec;
            return frozen ? "audio is playing but the engine capture is frozen" : null;
        }

        // 5) loud system audio while the engine hears silence (bound to the wrong endpoint)
        internal string EngineDeaf(Poll p)
        {
            var st = p.St;
            bool engineSilent = st.EnginePeak < Settings.EngineSilentPeak;
            if (p.Awake && st.CaptureRunning && st.SystemCapture && p.SystemPeak > Settings.LoudPeakThreshold && engineSilent) silentMismatch++;
            else if ((!st.SystemCapture || !engineSilent) && silentMismatch > 0) silentMismatch--;
            return silentMismatch >= Settings.SilentMismatchKick ? "audio is playing but the engine only hears silence" : null;
        }

        // 6) per-game capture still bound to a game process that has exited
        internal string CaptureOrphaned(Poll p)
        {
            var st = p.St;
            if (st.HidConnected && st.CaptureRunning && !st.SystemCapture && st.CapturePid > 0 && !ProcessAlive((int)st.CapturePid))
            { if (deadPidSince == DateTime.MaxValue) deadPidSince = p.Now; }
            else deadPidSince = DateTime.MaxValue;
            return (p.Now - deadPidSince).TotalSeconds >= Settings.BrokenPersistSec
                ? "game exited but the engine is still bound to its process (PID " + st.CapturePid + ")" : null;
        }

        // 2) skipped when the engine already follows the new device (engine 5.0.2.4+ does). That is only
        // provable while something plays: system capture still counting frames and hearing audio while Windows
        // plays. Engines that stay on the old device hear silence here, so they are still re-armed.
        internal bool EngineFollowsDevice(Poll p)
        {
            var st = p.St;
            return st.Running && st.CaptureRunning && st.OutputRunning && !st.HasError && st.SystemCapture &&
                   (p.Now - seqChangedAt).TotalSeconds < 2 &&
                   p.SystemPeak > Settings.AudioPeakThreshold && st.EnginePeak >= Settings.EngineSilentPeak;
        }

        // Resets the re-arm back-off once everything has been fine for healthyResetSec.
        void TrackHealthy(Poll p, bool faulty)
        {
            if (faulty || !p.St.HidConnected) healthySince = p.Now;
            else if ((p.Now - healthySince).TotalSeconds >= Settings.HealthyResetSec) backoffIndex = 0;
        }
    }
}
