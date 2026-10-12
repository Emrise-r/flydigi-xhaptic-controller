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
        // ---- follow child audio
        // A game can play audio from a child process (e.g. BH3 -> ZFGameBrowser for web events) that the
        // engine's per-app capture of the game does not hear. While following, the child's own profile is
        // enabled (or Mix when the engine binds a process of the child that plays nothing):
        //   child closed or silent for followReturnSilenceSec -> back to the parent; parent closed -> Mix.

        string followParentScope, followChildScope, followParentName, followChildName;
        int followParentPid, childAudioPolls;
        bool followViaMix;
        DateTime followVerifyAt = DateTime.MaxValue, childSilentSince = DateTime.MaxValue;

        void ClearFollow()
        {
            followParentScope = followChildScope = followParentName = followChildName = null;
            followParentPid = 0; childAudioPolls = 0; followViaMix = false;
            followVerifyAt = childSilentSince = DateTime.MaxValue;
        }

        // Returns true when it switched sources this tick.
        bool FollowChildAudio(DateTime now, bool hid, bool capRunning, bool systemCapture, long capturePid, string scope, double enginePeak)
        {
            if (!Settings.FollowChildAudio || !hid) return false;
            if (followParentScope != null) return UpdateFollow(now);
            if (!capRunning || systemCapture || capturePid <= 0 || string.IsNullOrEmpty(scope) || scope == "global" ||
                (now - lastKickAt).TotalSeconds < Settings.RearmGraceSec || enginePeak >= Settings.EngineSilentPeak)
            { childAudioPolls = 0; return false; }

            var peaks = AudioSessions.SessionPeaks();
            var parents = ProcessTree.ParentMap();
            int best = 0; float bestPeak = 0;
            foreach (var kv in peaks)
                if (kv.Key != capturePid && kv.Value > bestPeak && ProcessTree.IsDescendant(kv.Key, (int)capturePid, parents))
                { best = kv.Key; bestPeak = kv.Value; }
            if (best == 0 || bestPeak < Settings.AudioPeakThreshold) { childAudioPolls = 0; return false; }
            if (++childAudioPolls < Settings.FollowDetectPolls) return false;
            childAudioPolls = 0;
            StartFollow(scope, (int)capturePid, best);
            return true;
        }

        void SetScope(Dictionary<string, object> configs, Dictionary<string, bool> originals, string id, bool enabled)
        {
            var c = configs != null && id != null && configs.ContainsKey(id) ? EngineClient.Obj(configs[id]) : null;
            if (c == null) return;
            bool have = EngineClient.Bool(c, "enabled");
            if (!originals.ContainsKey(id)) originals[id] = have;
            if (have != enabled) Toggle(id, c, enabled);
        }


        void StartFollow(string parentScope, int parentPid, int childPid)
        {
            followParentScope = parentScope; followParentPid = parentPid;
            followParentName = ProcessName(parentPid) ?? parentScope;
            followChildName = ProcessName(childPid);
            childSilentSince = DateTime.MaxValue;
            Log.Write("child audio: " + followChildName + " (PID " + childPid + ") under " + followParentName + " plays while the engine hears nothing -> following it");

            // the child's own profile (registered as a disabled custom game if needed)
            string childScope = null;
            try
            {
                var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { followChildName };
                foreach (var g in MatchGames(LoadGames(false), running, false)) { childScope = g.Id; break; }
                string path = ProcessTree.ImagePath(childPid);
                if (childScope == null && !string.IsNullOrEmpty(path))
                {
                    var body = new Dictionary<string, object>();
                    body["displayName"] = followChildName;
                    body["processPath"] = path;
                    childScope = EngineClient.Str(engine.Post("/api/v1/xhaptics/games/custom", body), "game.gameId");
                    Log.Write("registered " + path + " as custom game " + childScope + " (disabled)");
                }
            }
            catch (Exception ex) { Log.Write("cannot register " + followChildName + ": " + Trim(ex.Message)); }

            var configs = Configs();
            var originals = SourceOverrides.Load();
            if (childScope != null && configs != null && configs.ContainsKey(childScope)) SetScope(configs, originals, childScope, false);
            SetScope(configs, originals, parentScope, false);
            if (childScope != null && configs != null && configs.ContainsKey(childScope))
            {
                Thread.Sleep(1200);   // the engine only binds a scope enabled while it sits on Mix
                SetScope(configs, originals, childScope, true);
                followChildScope = childScope; followViaMix = false;
                followVerifyAt = DateTime.Now.AddSeconds(4);
            }
            else followViaMix = true;
            SourceOverrides.Save(originals);
            lastKickAt = DateTime.Now; brokenSince = DateTime.MaxValue; deadPidSince = DateTime.MaxValue; silentMismatch = 0;
            LastAction = "Following " + followChildName;
            Notify("Audio from " + followChildName + " (inside " + followParentName + "): following it" + (followViaMix ? " via Mix." : "."), true);
        }

        bool UpdateFollow(DateTime now)
        {
            var parents = ProcessTree.ParentMap();
            if (!ProcessAlive(followParentPid)) { EndFollow(followParentName + " closed", true); return true; }

            // engine bound to a process of the child that plays nothing (multi-process child) -> Mix
            if (!followViaMix && now >= followVerifyAt)
            {
                followVerifyAt = DateTime.MaxValue;
                var st = engine.Get("/api/xhaptics/status");
                long bound = EngineClient.Long(st, "realtimeAudio.wasapi.processId");
                bool ok = EngineClient.Str(st, "realtimeAudio.wasapi.source") == "process" && bound > 0 &&
                          ProcessTree.TreeHasSession((int)bound, AudioSessions.ProcessIds(), parents);
                if (!ok)
                {
                    Log.Write("engine bound PID " + bound + " of " + followChildName + ", which plays nothing -> Mix");
                    var configs = Configs(); var originals = SourceOverrides.Load();
                    SetScope(configs, originals, followChildScope, false);
                    SourceOverrides.Save(originals);
                    followViaMix = true; lastKickAt = DateTime.Now;
                    Notify(followChildName + ": switched to Mix (the engine cannot capture it on its own).", true);
                    return true;
                }
            }

            // is the child (any process of that name under the parent) still alive and playing?
            bool childAlive = false; float childPeak = 0;
            var peaks = AudioSessions.SessionPeaks();
            foreach (var p in Process.GetProcessesByName(followChildName))
            {
                try
                {
                    if (!ProcessTree.IsDescendant(p.Id, followParentPid, parents)) continue;
                    childAlive = true;
                    float pk;
                    if (peaks.TryGetValue(p.Id, out pk) && pk > childPeak) childPeak = pk;
                }
                finally { p.Dispose(); }
            }
            if (!childAlive) { EndFollow(followChildName + " closed", false); return true; }
            if (childPeak >= Settings.AudioPeakThreshold) childSilentSince = DateTime.MaxValue;
            else if (childSilentSince == DateTime.MaxValue) childSilentSince = now;
            if ((now - childSilentSince).TotalSeconds >= Settings.FollowReturnSilenceSec)
            { EndFollow(followChildName + " silent for " + Settings.FollowReturnSilenceSec + "s", false); return true; }
            return false;
        }

        void EndFollow(string reason, bool toMix)
        {
            string parentName = followParentName, parentScope = followParentScope, childScope = followChildScope;
            ClearFollow();
            try
            {
                var configs = Configs(); var originals = SourceOverrides.Load();
                SetScope(configs, originals, childScope, false);
                if (!toMix)
                {
                    Thread.Sleep(1200);
                    SetScope(configs, originals, parentScope, true);
                }
                SourceOverrides.Save(originals);
            }
            catch (Exception ex) { Log.Write("end follow failed: " + Trim(ex.Message)); }
            lastKickAt = DateTime.Now; brokenSince = DateTime.MaxValue; deadPidSince = DateTime.MaxValue; silentMismatch = 0;
            string target = toMix ? "Mix (all apps)" : parentName;
            Log.Write("stop following (" + reason + ") -> " + target);
            LastAction = "Source: " + target;
            Notify(reason + ": audio source back to " + target + ".", true);
        }
    }
}
