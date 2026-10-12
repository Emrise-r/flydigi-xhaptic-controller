using System.Collections.Generic;
using System.Windows.Forms;
using State = FlydigiHapticsGuard.Guard.ControllerState;

namespace FlydigiHapticsGuard.Tests
{
    static class HotkeyTests
    {
        static void Parses(string spec, uint mods, Keys key)
        {
            uint m; Keys k;
            Assert.True(HotkeyWindow.TryParse(spec, out m, out k), spec);
            Assert.Equal(mods, m, spec + " modifiers");
            Assert.Equal(key, k, spec + " key");
        }

        static void Rejects(string spec)
        {
            uint m; Keys k;
            Assert.False(HotkeyWindow.TryParse(spec, out m, out k), spec);
        }

        [Test]
        static void Parses_modifiers_and_key()
        {
            Parses("Ctrl+Alt+R", HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, Keys.R);
            Parses("ctrl + alt + p", HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, Keys.P);
            Parses("Control+Shift+F12", HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_SHIFT, Keys.F12);
            Parses("Win+1", HotkeyWindow.MOD_WIN, Keys.D1);
            Parses("F9", 0, Keys.F9);
        }

        [Test]
        static void Rejects_unknown_keys_and_missing_key()
        {
            Rejects("Ctrl+Alt+Banana");
            Rejects("Ctrl+Alt");
            Rejects("");
        }
    }

    static class ProcessTreeTests
    {
        // 1 -> 10 -> 20 -> 30, 40 is its own parent, 60 <-> 61 is a loop
        static readonly Dictionary<int, int> Parents = new Dictionary<int, int>
        {
            { 10, 1 }, { 20, 10 }, { 30, 20 }, { 40, 40 }, { 50, 0 }, { 60, 61 }, { 61, 60 },
        };

        [Test]
        static void IsDescendant_walks_up_the_parents()
        {
            Assert.True(ProcessTree.IsDescendant(30, 10, Parents), "grandchild");
            Assert.True(ProcessTree.IsDescendant(30, 1, Parents), "great-grandchild");
            Assert.False(ProcessTree.IsDescendant(10, 30, Parents), "ancestor is not a descendant");
            Assert.False(ProcessTree.IsDescendant(30, 30, Parents), "not its own descendant");
            Assert.False(ProcessTree.IsDescendant(40, 1, Parents), "self-parented");
            Assert.False(ProcessTree.IsDescendant(99, 1, Parents), "unknown pid");
            Assert.False(ProcessTree.IsDescendant(60, 1, Parents), "parent loop terminates");
        }

        [Test]
        static void TreeHasSession_includes_the_process_and_its_children_only()
        {
            Assert.True(ProcessTree.TreeHasSession(10, new HashSet<int> { 30 }, Parents), "child plays");
            Assert.True(ProcessTree.TreeHasSession(20, new HashSet<int> { 20 }, Parents), "process itself plays");
            Assert.False(ProcessTree.TreeHasSession(30, new HashSet<int> { 10 }, Parents), "only the parent plays");
            Assert.False(ProcessTree.TreeHasSession(10, new HashSet<int>(), Parents), "nothing plays");
            Assert.False(ProcessTree.TreeHasSession(1, new HashSet<int> { 60 }, Parents), "parent loop terminates");
        }
    }

    static class ControllerStateTests
    {
        static string Message(State? previous, State now, bool rearmedRecently = false)
        {
            return Guard.ControllerStateMessage(previous, now, rearmedRecently);
        }

        [Test]
        static void Disconnected_always_gets_a_balloon()
        {
            Assert.Contains("Controller not connected", Message(null, State.Disconnected), "at start");
            Assert.Contains("Controller disconnected", Message(State.Connected, State.Disconnected));
            Assert.Contains("Controller disconnected", Message(State.Asleep, State.Disconnected));
        }

        [Test]
        static void Reconnecting_gets_a_balloon()
        {
            Assert.Equal("Controller connected.", Message(State.Disconnected, State.Connected));
            Assert.Contains("Press any controller button", Message(State.Disconnected, State.Asleep));
        }

        [Test]
        static void Quiet_changes_get_no_balloon()
        {
            Assert.Null(Message(null, State.Connected), "connected at start");
            Assert.Null(Message(State.Asleep, State.Connected), "woke up");
            Assert.Null(Message(null, State.Asleep), "asleep at start");
        }

        [Test]
        static void Falling_asleep_is_reported_unless_a_rearm_just_said_so()
        {
            Assert.Contains("Haptics asleep", Message(State.Connected, State.Asleep));
            Assert.Null(Message(State.Connected, State.Asleep, true), "right after a re-arm");
        }

        [Test]
        static void State_names_in_the_log()
        {
            Assert.Equal("start", Guard.StateName(null));
            Assert.Equal("disconnected", Guard.StateName(State.Disconnected));
            Assert.Equal("asleep", Guard.StateName(State.Asleep));
        }
    }
}
