using System;
using System.Linq;

namespace FlydigiHapticsGuard
{
    partial class Guard
    {
        // ---- current source + controller state, shown at the top of the tray menu

        public volatile string SourceInfo = "Source: checking...", ControllerInfo = "Controller: checking...";
        public volatile bool ControllerConnected, ControllerAsleep;
        DateTime lastInfoRefresh = DateTime.MinValue;

        internal enum ControllerState { Disconnected, Connected, Asleep }

        // one balloon per controller state change, once the state has lasted controllerNotifyDelaySec
        ControllerState? candidateState, notifiedState;
        DateTime candidateSince = DateTime.MinValue;

        string controllerUsbId = "VID_37D7&PID_2502", lastControllerLine;
        long lastWriteFailures = -1;
        bool lowBatteryNotified;

        // X-Haptics turned off outside the guard (Space Station toggle) while the master switch is ON.
        // The guard respects that choice and does not turn it back on; it only says so, once.
        bool turnedOffNoticed;

        // Called by the watchdog with the status it already read, or with null (reads it) while the switch is OFF.
        void RefreshInfo(XHapticsStatus status)
        {
            lastInfoRefresh = DateTime.Now;
            if (status == null)
            {
                try { status = engine.Status(); }
                catch (EngineException) { ShowEngineNotRunning(); return; }
            }

            if (status.Running) turnedOffNoticed = false;
            SourceInfo = "Source: " + DescribeSource(status) + DescribeFollowing();
            UpdateController(status);
        }

        void ShowEngineNotRunning()
        {
            SourceInfo = "Source: engine not running";
            ControllerInfo = "Controller: unknown (engine not running)";
        }

        // ---- source line

        string DescribeSource(XHapticsStatus s)
        {
            if (!s.Running) return DescribeNotRunning();
            return s.ProcessCapture ? GameName(s.Scope) + " (PID " + s.CapturePid + ")" : "Mix (all apps)";
        }

        string DescribeNotRunning()
        {
            if (!autoEnabled) return "X-Haptics off";
            if (exclusiveHold) return "audio device in exclusive use by another app (exclusive/ASIO player): haptics resume when it is released";
            if (WebUiAttached()) return "paused (Space Station page is open)";
            if (TurnedOffElsewhere()) return "X-Haptics off (turned off in Space Station)" + TurnOnHint();
            return "X-Haptics idle";
        }

        string DescribeFollowing()
        {
            if (followParentScope == null) return "";
            return followViaMix ? "  - following " + followChildName + " of " + followParentName
                                : "  (in " + followParentName + ", following)";
        }

        string GameName(string scopeId)
        {
            var games = LoadGames(false);
            var game = games == null ? null : games.FirstOrDefault(g => g.Id == scopeId);
            return game != null && game.Name != null ? game.Name : scopeId;
        }

        string TurnOnHint()
        {
            return Settings.HotkeyToggle.Length > 0 ? ": press " + Settings.HotkeyToggle + " twice to turn it on" : "";
        }

        // Only called while the engine is not running, so the extra request costs nothing in normal use.
        bool TurnedOffElsewhere()
        {
            if (!autoEnabled) return false;
            bool off;
            try
            {
                var configs = engine.Configs();
                off = configs.Has(EngineApi.GlobalScope) && !configs.IsEnabled(EngineApi.GlobalScope);
            }
            catch { return false; }
            if (off && !turnedOffNoticed)
            {
                turnedOffNoticed = true;
                Log.Write("X-Haptics is turned off in Space Station while the switch is ON; leaving it off");
                Notify("X-Haptics was turned off in Space Station" + TurnOnHint() + ".", true);
            }
            return off;
        }

        // ---- controller line, balloons and log

        void UpdateController(XHapticsStatus s)
        {
            if (s.UsbId != null) controllerUsbId = s.UsbId;
            ControllerConnected = s.HidConnected;
            ControllerAsleep = s.HidConnected && s.Running && !s.GripGateOpen;

            ControllerInfo = "Controller: " + DescribeController(s);
            lastWriteFailures = s.WriteFailures;

            NotifyWhenStateSettles(!s.HidConnected ? ControllerState.Disconnected
                                 : ControllerAsleep ? ControllerState.Asleep
                                 : ControllerState.Connected);
            CheckLowBattery(s);
            LogControllerChange(s);
        }

        string DescribeController(XHapticsStatus s)
        {
            if (!s.HidConnected) return "not connected (off, out of range, or taken off the cable/dock): press Home";
            string text = "connected";
            if (s.BatteryPercent >= 0) text += ", battery " + s.BatteryPercent + "%";
            if (s.IsCharging) text += ", charging";
            else if (s.IsFull) text += ", fully charged";
            if (s.HidPaused) text += ", paused";
            bool newWriteFailures = lastWriteFailures >= 0 && s.WriteFailures > lastWriteFailures;
            if (newWriteFailures || s.HidError.Length > 0) text += ", output errors";
            if (ControllerAsleep) text += ", haptics asleep (press a controller button)";
            return text;
        }

        void NotifyWhenStateSettles(ControllerState state)
        {
            if (state != candidateState) { candidateState = state; candidateSince = DateTime.Now; }
            if (candidateState == notifiedState || (DateTime.Now - candidateSince).TotalSeconds < Settings.ControllerNotifyDelaySec) return;
            var previous = notifiedState;
            notifiedState = state;
            NotifyControllerState(previous, state);
        }

        void NotifyControllerState(ControllerState? previous, ControllerState now)
        {
            Log.Write("controller state: " + StateName(previous) + " -> " + StateName(now));
            bool rearmedRecently = (DateTime.Now - lastKickAt).TotalSeconds < 15;   // the re-arm balloon already says so
            string msg = ControllerStateMessage(previous, now, rearmedRecently);
            if (msg != null) Notify(msg, true);
        }

        // null = nothing worth a balloon (start-up while connected, waking up, right after a re-arm)
        internal static string ControllerStateMessage(ControllerState? previous, ControllerState now, bool rearmedRecently)
        {
            bool wasDisconnected = previous == ControllerState.Disconnected;
            switch (now)
            {
                case ControllerState.Disconnected:
                    return previous == null ? "Controller not connected. Turn it on (Home) or put it on the cable/dock."
                                            : "Controller disconnected (turned off, out of range, or taken off the cable/dock). Press Home to reconnect.";
                case ControllerState.Connected:
                    return wasDisconnected ? "Controller connected." : null;
                case ControllerState.Asleep:
                    if (wasDisconnected) return "Controller connected. Press any controller button to wake haptics.";
                    if (previous == ControllerState.Connected && !rearmedRecently)
                        return "Haptics asleep (no controller input). Press any controller button to wake them.";
                    return null;
            }
            return null;
        }

        internal static string StateName(ControllerState? state)
        {
            return state == null ? "start" : state.Value.ToString().ToLowerInvariant();
        }

        void CheckLowBattery(XHapticsStatus s)
        {
            long percent = s.BatteryPercent;
            if (!s.HidConnected || Settings.LowBatteryPercent <= 0 || percent < 0 || s.IsCharging || s.IsFull) return;
            if (percent <= Settings.LowBatteryPercent && !lowBatteryNotified)
            {
                lowBatteryNotified = true;
                Log.Write("controller battery low: " + percent + "% (level " + s.BatteryLevel + "/10)");
                Notify("Controller battery low (" + percent + "%).", true);
            }
            else if (percent > Settings.LowBatteryPercent + 5) lowBatteryNotified = false;
        }

        // Logs every change of the raw controller state (helps with power-off / charging cases).
        // The controller's USB device is also presented by the wireless receiver while the controller is
        // linked, so "usb" cannot tell cable/dock from wireless; it is only logged for diagnosis.
        void LogControllerChange(XHapticsStatus s)
        {
            string line = string.Format("connected={0} opened={1} paused={2} battery={3}/10 charge={4} (raw {10}) mode={5} usb={6} asleep={7} writeFailures={8} error='{9}'",
                                        s.HidConnected, s.HidOpened, s.HidPaused, s.BatteryLevel, s.ChargeState, s.ConnectionMode,
                                        UsbDevices.IsPresent(controllerUsbId), ControllerAsleep, s.WriteFailures, s.HidError, s.BatteryByte);
            if (line == lastControllerLine) return;
            lastControllerLine = line;
            Log.Write("controller: " + line);
        }
    }
}
