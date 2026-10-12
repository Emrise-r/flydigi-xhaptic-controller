using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace FlydigiHapticsGuard
{
    partial class Guard
    {
        public void RequestCycle() { cycleRequested = true; }
        volatile bool cycleRequested;
        DateTime? sourceCheckAt; string sourceCheckLabel;

        // Multi-process apps (e.g. Chrome plays audio from a separate audio-service process): the engine captures
        // the process tree of the app's foreground-window process while that window is focused, and otherwise a
        // process it picked by name (often one that plays nothing). The guard cannot change that; it only says so.
        void CheckSourceProcess()
        {
            var st = engine.Status();
            if (!st.ProcessCapture) return;
            long pid = st.CapturePid;
            if (pid <= 0) return;
            var sessions = AudioSessions.ProcessIds();
            var parents = ProcessTree.ParentMap();
            if (ProcessTree.TreeHasSession((int)pid, sessions, parents)) return;
            // only complain when another process of the same app is the one playing
            string name = ProcessName((int)pid);
            if (!sessions.Any(s => string.Equals(ProcessName(s), name, StringComparison.OrdinalIgnoreCase))) return;
            Log.Write("audio source " + sourceCheckLabel + ": engine captures PID " + pid + ", which plays no audio (multi-process app; only captured while its window is focused)");
            Notify(sourceCheckLabel + " plays audio from another process. The engine only captures it while the " + sourceCheckLabel +
                   " window is focused. Use Mix to feel it in the background.", true);
        }

        // ---- running processes

        static string ProcessName(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch { return null; }
        }

        // process name -> PIDs of every running process with that name
        static Dictionary<string, List<int>> PidsByName()
        {
            var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            ProcessTree.ForEach(Process.GetProcesses(), p =>
            {
                List<int> pids;
                if (!map.TryGetValue(p.ProcessName, out pids)) map[p.ProcessName] = pids = new List<int>();
                pids.Add(p.Id);
            });
            return map;
        }

        static HashSet<string> RunningProcessNames()
        {
            return new HashSet<string>(PidsByName().Keys, StringComparer.OrdinalIgnoreCase);
        }

        // Which of the candidate process names runs `name` as a descendant (e.g. BH3 for ZFGameBrowser)?
        static string ParentProcessOf(string name, IEnumerable<string> candidates, Dictionary<string, List<int>> pids, Dictionary<int, int> parents)
        {
            List<int> mine;
            if (!pids.TryGetValue(name, out mine)) return null;
            foreach (var cand in candidates)
            {
                List<int> theirs;
                if (string.Equals(cand, name, StringComparison.OrdinalIgnoreCase) || !pids.TryGetValue(cand, out theirs)) continue;
                if (mine.Any(child => theirs.Any(parent => ProcessTree.IsDescendant(child, parent, parents)))) return cand;
            }
            return null;
        }

        // ---- the engine's game list

        class GameInfo { public string Id, Name, Process; }

        List<XHapticsGame> cachedGames; DateTime cachedGamesStamp;
        readonly object gamesGate = new object();

        // GET /api/v1/xhaptics/games makes the engine rescan games and drop to Mix for ~2 s, so the list is
        // read from the engine's own games file instead; the API is only a fallback for user-triggered calls.
        List<XHapticsGame> LoadGames(bool allowApi)
        {
            lock (gamesGate)
            {
                try
                {
                    var fi = new FileInfo(Settings.GameListPath);
                    if (fi.Exists)
                    {
                        if (cachedGames == null || fi.LastWriteTimeUtc != cachedGamesStamp)
                        {
                            cachedGames = ReadGamesFile(fi.FullName);
                            cachedGamesStamp = fi.LastWriteTimeUtc;
                        }
                        if (cachedGames != null) return cachedGames;
                    }
                }
                catch (Exception ex) { Log.Write("cannot read games file: " + Trim(ex.Message)); }
                return allowApi ? engine.Games() : null;
            }
        }

        // The engine keeps the file open, so it is read with full sharing.
        static List<XHapticsGame> ReadGamesFile(string path)
        {
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8)) text = r.ReadToEnd();
            var root = EngineClient.Obj(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text));
            return XHapticsGame.ParseList(EngineClient.At(root, "games"));
        }

        // Registered games/apps whose process is running. applyExclude drops the ones listed in
        // config.json "sources.exclude" (they are never picked as a target, but are still switched off).
        List<GameInfo> RunningGames(bool allowApi, bool applyExclude)
        {
            return MatchGames(LoadGames(allowApi), RunningProcessNames(), applyExclude);
        }

        static List<GameInfo> MatchGames(List<XHapticsGame> games, HashSet<string> processNames, bool applyExclude)
        {
            if (games == null) return new List<GameInfo>();
            return games
                .Select(g => new { Game = g, Process = g.ProcessNames.FirstOrDefault(n => processNames.Contains(n)) })
                .Where(m => m.Process != null && !(applyExclude && Settings.IsExcluded(m.Process)))
                .Select(m => new GameInfo { Id = m.Game.Id, Name = m.Game.Name ?? m.Process, Process = m.Process })
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ---- "Audio sources" tray menu

        public class SourceEntry
        {
            public string Process, Path, Label, GameId;   // GameId == null: not registered in X-Haptics yet
            public bool Included;                         // registered and not excluded = part of the cycle
            public string ParentLabel;                    // set when this runs inside another listed app (BH3 -> ZFGameBrowser)
        }

        // Called on the UI thread when the menu opens: processes with an audio session plus running
        // registered games. Reads only the engine's games file (never the games API).
        public List<SourceEntry> SourceEntries()
        {
            var byName = new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            foreach (int pid in AudioSessions.ProcessIds())
            {
                if (pid == self) continue;
                string path = ProcessTree.ImagePath(pid);
                if (string.IsNullOrEmpty(path)) continue;
                if (string.Equals(path, Settings.EngineExePath, StringComparison.OrdinalIgnoreCase)) continue;   // the engine itself
                string name = Settings.NormalizeProcessName(Path.GetFileName(path));
                if (!byName.ContainsKey(name)) byName[name] = new SourceEntry { Process = name, Path = path, Label = FileDescription(path) ?? name };
            }

            var pids = PidsByName();
            var running = new HashSet<string>(pids.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var g in MatchGames(LoadGames(false), running, false))
            {
                SourceEntry e;
                if (!byName.TryGetValue(g.Process, out e)) byName[g.Process] = e = new SourceEntry { Process = g.Process };
                e.GameId = g.Id; e.Label = g.Name;
            }

            var list = new List<SourceEntry>(byName.Values);
            foreach (var e in list) e.Included = e.GameId != null && !Settings.IsExcluded(e.Process);

            // nest child processes under the listed app that runs them
            var parents = ProcessTree.ParentMap();
            var names = new List<string>(byName.Keys);
            foreach (var e in list)
            {
                string parent = ParentProcessOf(e.Process, names, pids, parents);
                if (parent != null) e.ParentLabel = byName[parent].Label;
            }
            return list.OrderBy(SortKey, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // children sort right after their parent
        static string SortKey(SourceEntry e)
        {
            return e.ParentLabel == null ? e.Label + "\u0001" : e.ParentLabel + "\u0001" + e.Label;
        }

        static string FileDescription(string path)
        {
            try
            {
                string d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                return string.IsNullOrEmpty(d) || d.Trim().Length == 0 ? null : d.Trim();
            }
            catch { return null; }
        }

        // Include: register the process as a disabled custom game if needed and drop it from the exclude list.
        // Exclude: add it to the exclude list (nothing is removed from Flydigi's game list).
        public void RequestSourceInclude(SourceEntry e, bool include)
        {
            Enqueue(delegate
            {
                try
                {
                    var exclude = new List<string>(Settings.SourceExclude);
                    exclude.RemoveAll(x => string.Equals(x, e.Process, StringComparison.OrdinalIgnoreCase));
                    if (!include) exclude.Add(e.Process);
                    else if (e.GameId == null)
                    {
                        if (string.IsNullOrEmpty(e.Path)) throw new InvalidOperationException("unknown path for " + e.Process);
                        RegisterCustomGame(e.Label, e.Path);
                    }
                    Settings.SaveSourceExclude(exclude);
                    Log.Write("audio source " + e.Process + (include ? " included" : " excluded"));
                    Notify(e.Label + (include ? " added to" : " removed from") + " the audio source cycle.", true);
                }
                catch (Exception ex)
                {
                    Log.Write("audio source " + (include ? "include" : "exclude") + " failed (" + e.Process + "): " + Trim(ex.Message));
                    Notify("Could not update audio sources. " + Explain(ex), true);
                }
            });
        }

        // ---- audio source cycling
        // The engine captures one process (process-loopback) while an enabled game/app scope is running,
        // and the whole system mix when none is. Cycling flips the enabled flags of the running scopes:
        //   Mix -> app/game 1 -> app/game 2 -> ... -> Mix
        // The original flags are kept in sources.txt and put back once those processes have exited.

        const string MixSource = "mix";   // cycle position "no game profile enabled"
        DateTime nextRestoreCheck = DateTime.MinValue;

        void Cycle()
        {
            if (!autoEnabled)
            {
                Log.Write("audio source switch ignored: switch is OFF");
                Notify("X-Haptics is off. Turn it on first" + (Settings.HotkeyToggle.Length > 0 ? " (" + Settings.HotkeyToggle + ")." : "."), true);
                return;
            }

            if (followParentScope != null) { Log.Write("manual source switch: stop following " + followChildName); ClearFollow(); }

            var games = RunningGames(true, false);   // all running registered scopes (all get switched off)
            if (games.Count == 0)
            {
                Notify("Audio source: Mix (all apps). No registered game or app is running.", true);
                return;
            }
            var configs = engine.Configs();
            string next = NextSource(games, configs);

            // The engine only picks up a game scope when it is enabled while the engine sits on global,
            // so: disable every running scope (target included) -> engine falls back to Mix -> enable target.
            var originals = SourceOverrides.Load();
            foreach (var g in games) SwitchProfile(configs, originals, g.Id, false);
            SourceOverrides.Save(originals);
            if (next != MixSource && configs.Has(next))
            {
                Thread.Sleep(BindOnMixDelayMs);
                engine.SetEnabled(configs, next, true);
            }
            ResetAfterSourceChange();

            string label = next == MixSource ? "Mix (all apps)" : SourceLabel(games, next);
            Log.Write("audio source -> " + label + " (" + next + ")");
            LastAction = "Source: " + label;
            Notify("Audio source: " + label, true);
            if (next != MixSource) { sourceCheckAt = DateTime.Now.AddSeconds(VerifyBindingAfterSec); sourceCheckLabel = label; }
        }

        // Mix -> each running, non-excluded game -> Mix. An excluded scope that is currently selected moves on to Mix.
        string NextSource(List<GameInfo> games, XHapticsConfigs configs)
        {
            // Current source = the enabled running scope (the engine's own status is unreliable while it is
            // switching or paused by the Space Station page). If several are enabled, ask the engine.
            var enabledIds = games.Where(g => configs.IsEnabled(g.Id)).Select(g => g.Id).ToList();
            string current = MixSource;
            if (enabledIds.Count == 1) current = enabledIds[0];
            else if (enabledIds.Count > 1)
            {
                string scope = engine.Status().Scope;
                current = enabledIds.Contains(scope) ? scope : enabledIds[0];
            }

            var order = new[] { MixSource }.Concat(games.Where(g => !Settings.IsExcluded(g.Process)).Select(g => g.Id)).ToList();
            int idx = order.IndexOf(current);
            return order[(idx + 1) % order.Count];
        }

        // "ZFGameBrowser (in BH3)" when the target runs inside another running game
        static string SourceLabel(List<GameInfo> games, string id)
        {
            var target = games.First(g => g.Id == id);
            string parent = ParentProcessOf(target.Process, games.Select(g => g.Process), PidsByName(), ProcessTree.ParentMap());
            var parentGame = games.FirstOrDefault(g => parent != null && string.Equals(g.Process, parent, StringComparison.OrdinalIgnoreCase));
            return target.Name + (parentGame != null ? " (in " + parentGame.Name + ")" : "");
        }

        // Put back the enabled flags changed by Cycle() once none of those processes is running.
        void RestoreSourcesIfDone()
        {
            if (!autoEnabled || DateTime.Now < nextRestoreCheck) return;
            nextRestoreCheck = DateTime.Now.AddSeconds(5);
            var originals = SourceOverrides.Load();
            if (originals.Count == 0) return;
            var games = RunningGames(false, false);
            if (games.Count == 0 && cachedGames == null) return;   // games file unreadable: don't guess
            if (games.Any(g => originals.ContainsKey(g.Id))) return;

            var configs = engine.Configs();
            foreach (var kv in originals)
                if (configs.Has(kv.Key) && configs.IsEnabled(kv.Key) != kv.Value) engine.SetEnabled(configs, kv.Key, kv.Value);
            SourceOverrides.Save(new Dictionary<string, bool>());
            Log.Write("audio source overrides restored (" + string.Join(",", originals.Keys) + ")");
        }
    }
}
