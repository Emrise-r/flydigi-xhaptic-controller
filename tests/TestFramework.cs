using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FlydigiHapticsGuard.Tests
{
    // A tiny test runner, so the tests build with the same csc as the app and need no packages.
    // Every static method marked [Test] in this namespace is a test; a test fails when it throws.

    [AttributeUsage(AttributeTargets.Method)]
    class TestAttribute : Attribute { }

    class AssertException : Exception
    {
        public AssertException(string message) : base(message) { }
    }

    static class Assert
    {
        public static void Equal<T>(T expected, T actual, string what = null)
        {
            if (!Equals(expected, actual))
                throw new AssertException(Label(what) + "expected <" + expected + "> but was <" + actual + ">");
        }

        public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what = null)
        {
            var e = expected.ToList(); var a = actual.ToList();
            if (!e.SequenceEqual(a))
                throw new AssertException(Label(what) + "expected [" + string.Join(", ", e) + "] but was [" + string.Join(", ", a) + "]");
        }

        public static void True(bool condition, string what = null)
        {
            if (!condition) throw new AssertException(Label(what) + "expected true");
        }

        public static void False(bool condition, string what = null)
        {
            if (condition) throw new AssertException(Label(what) + "expected false");
        }

        public static void Null(object value, string what = null)
        {
            if (value != null) throw new AssertException(Label(what) + "expected null but was <" + value + ">");
        }

        public static void NotNull(object value, string what = null)
        {
            if (value == null) throw new AssertException(Label(what) + "expected a value but was null");
        }

        public static void Contains(string needle, string text, string what = null)
        {
            if (text == null || text.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new AssertException(Label(what) + "expected text containing <" + needle + "> but was <" + text + ">");
        }

        static string Label(string what) { return what == null ? "" : what + ": "; }
    }

    static class TestRunner
    {
        // HapticsGuard.Tests.exe [name filter]  -> exit code = number of failed tests
        static int Main(string[] args)
        {
            if (AppPaths.UsingFallback)
            {
                Console.WriteLine("tests\\bin is not writable; refusing to run (the tests would touch %LOCALAPPDATA%).");
                return 1;
            }
            Sandbox.Clean();

            string filter = args.Length > 0 ? args[0] : null;
            var tests = typeof(TestRunner).Assembly.GetTypes()
                .Where(t => t.Namespace == typeof(TestRunner).Namespace)
                .OrderBy(t => t.Name)
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                  .Where(m => m.IsDefined(typeof(TestAttribute), false))
                                  .OrderBy(m => m.MetadataToken))
                .Where(m => filter == null || (m.DeclaringType.Name + "." + m.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            int failed = 0;
            foreach (var test in tests)
            {
                string name = test.DeclaringType.Name + "." + test.Name;
                try
                {
                    test.Invoke(null, null);
                    Console.WriteLine("  pass  " + name);
                }
                catch (TargetInvocationException ex)
                {
                    failed++;
                    var inner = ex.InnerException;
                    Console.WriteLine("  FAIL  " + name + ": " + (inner is AssertException ? inner.Message : inner.ToString()));
                }
            }
            Sandbox.Clean();
            Console.WriteLine();
            Console.WriteLine((tests.Count - failed) + " passed, " + failed + " failed");
            return failed;
        }
    }

    // The test exe lives in tests\bin, so AppPaths puts config.json and data\ there: a throw-away sandbox.
    static class Sandbox
    {
        public static readonly string RepoDir = Path.GetFullPath(Path.Combine(AppPaths.BaseDir, "..", ".."));

        public static void Clean()
        {
            if (File.Exists(AppPaths.ConfigPath)) File.Delete(AppPaths.ConfigPath);
            if (Directory.Exists(AppPaths.DataDir)) Directory.Delete(AppPaths.DataDir, true);
        }

        // Fresh config.json with the built-in defaults, loaded into Settings.
        public static void DefaultSettings()
        {
            Clean();
            string error = Settings.Load();
            if (error != null) throw new AssertException("default config does not load: " + error);
        }

        public static void WriteConfig(string json)
        {
            File.WriteAllText(AppPaths.ConfigPath, Json(json));
        }

        // Lets tests write JSON with single quotes.
        public static string Json(string singleQuoted) { return singleQuoted.Replace('\'', '"'); }
    }
}
