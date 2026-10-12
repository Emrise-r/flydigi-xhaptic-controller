using System;
using Poll = FlydigiHapticsGuard.Guard.Poll;

namespace FlydigiHapticsGuard.Tests
{
    // The re-arm rules with made-up engine states; no engine is contacted (none of these rules calls it).
    static class WatchdogRuleTests
    {
        static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0);

        static Guard NewGuard()
        {
            Sandbox.DefaultSettings();
            return new Guard(message => { }, true);
        }

        // a healthy engine on system capture (Mix), controller awake, nothing playing
        static XHapticsStatus Healthy()
        {
            return new XHapticsStatus
            {
                SessionState = "Running", Scope = "global", CaptureRunning = true, OutputRunning = true,
                CaptureError = "", OutputError = "", CaptureSource = "system", CapturePid = 0, FrameSeq = 1,
                HidConnected = true, HidOpened = true, HidError = "", GripGateOpen = true,
            };
        }

        static Poll At(int second, XHapticsStatus st, float systemPeak = 0, bool awake = true)
        {
            return new Poll
            {
                Now = T0.AddSeconds(second), St = st, HaveDevice = true, DeviceId = "dev", SystemPeak = systemPeak,
                Idle = st.Idle || (!st.CaptureRunning && !st.OutputRunning), Awake = awake, SinceKick = 1000,
            };
        }

        [Test]
        static void Healthy_engine_breaks_no_rule()
        {
            var g = NewGuard();
            for (int s = 0; s < 20; s++)
            {
                var p = At(s, Healthy());
                p.St.FrameSeq = s;   // capture keeps counting
                p.Broken = g.EngineBroken(p);
                Assert.False(p.Broken, "broken at " + s);
                Assert.Null(g.EngineStopped(p), "stopped");
                Assert.Null(g.CaptureFrozen(p), "frozen");
                Assert.Null(g.EngineDeaf(p), "deaf");
                Assert.Null(g.CaptureOrphaned(p), "orphaned");
            }
        }

        [Test]
        static void Stopped_capture_rearms_after_brokenPersistSec()
        {
            var g = NewGuard();
            var st = Healthy(); st.CaptureRunning = false;
            for (int s = 0; s < Settings.BrokenPersistSec; s++)
            {
                var p = At(s, st); p.Broken = g.EngineBroken(p);
                Assert.True(p.Broken, "broken");
                Assert.Null(g.EngineStopped(p), "too early at " + s);
            }
            var due = At(Settings.BrokenPersistSec, st); due.Broken = g.EngineBroken(due);
            Assert.Contains("engine stopped (session=Running, capture=False", g.EngineStopped(due));

            due.SinceKick = Settings.RearmGraceSec - 1;
            Assert.Null(g.EngineStopped(due), "not right after a re-arm");
        }

        [Test]
        static void Idle_engine_held_by_the_web_page_is_not_broken()
        {
            var g = NewGuard();
            var st = Healthy(); st.SessionState = "Idle"; st.CaptureRunning = false; st.OutputRunning = false;
            var p = At(0, st); p.WebAttached = true;
            Assert.False(g.EngineBroken(p), "page attached");
            p.WebAttached = false;
            Assert.True(g.EngineBroken(p), "no page");

            var paused = Healthy(); paused.CaptureRunning = false; paused.HidPaused = true;
            Assert.False(g.EngineBroken(At(0, paused)), "HID paused");
            var errored = Healthy(); errored.CaptureError = "0x88890004";
            Assert.True(g.EngineBroken(At(0, errored)), "capture error");
        }

        [Test]
        static void Frozen_capture_needs_audio_and_an_awake_controller()
        {
            var g = NewGuard();
            string reason = null;
            for (int s = 0; s <= Settings.StallSec && reason == null; s++)
                reason = g.CaptureFrozen(At(s, Healthy(), 0.5f));   // FrameSeq stays 1
            Assert.Equal("audio is playing but the engine capture is frozen", reason);

            g = NewGuard();
            for (int s = 0; s <= 2 * Settings.StallSec; s++)
                Assert.Null(g.CaptureFrozen(At(s, Healthy(), 0.5f, false)), "asleep at " + s);
            g = NewGuard();
            for (int s = 0; s <= 2 * Settings.StallSec; s++)
                Assert.Null(g.CaptureFrozen(At(s, Healthy(), 0f)), "silence at " + s);
        }

        [Test]
        static void Deaf_engine_needs_loud_system_audio_on_system_capture()
        {
            var g = NewGuard();
            for (int s = 1; s < Settings.SilentMismatchKick; s++)
                Assert.Null(g.EngineDeaf(At(s, Healthy(), 0.5f)), "poll " + s);
            Assert.Equal("audio is playing but the engine only hears silence", g.EngineDeaf(At(99, Healthy(), 0.5f)));

            g = NewGuard();
            var perApp = Healthy(); perApp.CaptureSource = "process"; perApp.CapturePid = 1234;
            for (int s = 0; s < 3 * Settings.SilentMismatchKick; s++)
                Assert.Null(g.EngineDeaf(At(s, perApp, 0.5f)), "per-app capture at " + s);
        }

        [Test]
        static void Capture_bound_to_an_exited_process_is_orphaned()
        {
            var g = NewGuard();
            var st = Healthy(); st.CaptureSource = "process"; st.CapturePid = int.MaxValue - 1;   // no such process
            for (int s = 0; s < Settings.BrokenPersistSec; s++)
                Assert.Null(g.CaptureOrphaned(At(s, st)), "too early at " + s);
            Assert.Contains("game exited", g.CaptureOrphaned(At(Settings.BrokenPersistSec, st)));

            g = NewGuard();
            st.CapturePid = System.Diagnostics.Process.GetCurrentProcess().Id;   // alive
            for (int s = 0; s < 3 * Settings.BrokenPersistSec; s++)
                Assert.Null(g.CaptureOrphaned(At(s, st)), "live process at " + s);
        }
    }
}
