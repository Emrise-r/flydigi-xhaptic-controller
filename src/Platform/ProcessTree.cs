using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FlydigiHapticsGuard
{
    // Process tree, foreground window and executable paths (the engine's process-loopback includes child processes).
    static class ProcessTree
    {
        // Runs `action` for each process and disposes all of them. A process that exits meanwhile (its
        // properties then throw) is skipped.
        public static void ForEach(Process[] processes, Action<Process> action)
        {
            foreach (var p in processes)
            {
                try { action(p); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                finally { p.Dispose(); }
            }
        }

        const uint TH32CS_SNAPPROCESS = 0x2;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);

        // pid -> parent pid of every running process
        public static Dictionary<int, int> ParentMap()
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (Process32FirstW(snap, ref e))
                    do map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; while (Process32NextW(snap, ref e));
            }
            finally { CloseHandle(snap); }
            var startTimes = new Dictionary<int, long>();
            return WithoutReusedParents(map, pid =>
            {
                long t;
                if (!startTimes.TryGetValue(pid, out t)) startTimes[pid] = t = StartTime(pid);
                return t;
            });
        }

        // Windows keeps a process's parent PID after the parent exits, and may give that PID to a new process.
        // A "parent" that started after its child is such a reuse (e.g. Chrome started by a launcher that has
        // exited, whose PID now belongs to a game), so the link is dropped. startTime returns 0 when unknown.
        internal static Dictionary<int, int> WithoutReusedParents(Dictionary<int, int> parents, Func<int, long> startTime)
        {
            var result = new Dictionary<int, int>();
            foreach (var kv in parents)
            {
                long child = startTime(kv.Key), parent = startTime(kv.Value);
                if (child > 0 && parent > 0 && parent > child) continue;
                result[kv.Key] = kv.Value;
            }
            return result;
        }

        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        // process creation time (FILETIME ticks), 0 when the process is gone or not accessible
        static long StartTime(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                long creation, exit, kernel, user;
                return GetProcessTimes(h, out creation, out exit, out kernel, out user) ? creation : 0;
            }
            finally { CloseHandle(h); }
        }

        public static bool IsDescendant(int pid, int ancestor, Dictionary<int, int> parents)
        {
            int cur = pid;
            for (int depth = 0; depth < 32; depth++)
            {
                int parent;
                if (!parents.TryGetValue(cur, out parent) || parent == cur || parent <= 0) return false;
                if (parent == ancestor) return true;
                cur = parent;
            }
            return false;
        }

        // True when pid itself, or one of its descendants, owns an audio session.
        public static bool TreeHasSession(int pid, HashSet<int> sessionPids, Dictionary<int, int> parents)
        {
            foreach (int s in sessionPids)
            {
                int cur = s;
                for (int depth = 0; depth < 32 && cur > 0; depth++)
                {
                    if (cur == pid) return true;
                    int parent;
                    if (!parents.TryGetValue(cur, out parent) || parent == cur) break;
                    cur = parent;
                }
            }
            return false;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        public static int ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return (int)pid; }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // Works for most processes, including ones Process.MainModule refuses (other bitness, elevated).
        public static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
    }
}
