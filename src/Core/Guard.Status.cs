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
        // ---- current source + controller state, shown at the top of the tray menu

        public volatile string SourceInfo = "Source: checking...", ControllerInfo = "Controller: checking...";
        DateTime lastInfoRefresh = DateTime.MinValue;
        string lastControllerState;

        // ---- X-Haptics turned off outside the guard (Space Station toggle) while the master switch is ON.
        // The guard respects that choice and does not turn it back on; it only says so, once.
        bool turnedOffNoticed;

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
                var configs = Configs();
                var global = configs != null && configs.ContainsKey("global") ? EngineClient.Obj(configs["global"]) : null;
                off = global != null && !EngineClient.Bool(global, "enabled");
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

        string GameName(string scopeId)
        {
            var games = LoadGames(false);
            if (games != null)
                foreach (var o in games)
                {
                    var g = EngineClient.Obj(o);
                    if (g != null && EngineClient.Str(g, "gameId") == scopeId) return EngineClient.Str(g, "displayName") ?? scopeId;
                }
            return scopeId;
        }

        void RefreshInfo(Dictionary<string, object> st)
        {
            lastInfoRefresh = DateTime.Now;
            if (st == null)
            {
                try { st = engine.Get("/api/xhaptics/status"); }
                catch (EngineException) { SourceInfo = "Source: engine not running"; ControllerInfo = "Controller: unknown (engine not running)"; return; }
            }

            // source
            string state = EngineClient.Str(st, "session.state") ?? "";
            string src = EngineClient.Str(st, "realtimeAudio.wasapi.source");
            long pid = EngineClient.Long(st, "realtimeAudio.wasapi.processId");
            string scope = EngineClient.Str(st, "session.scope");
            string text;
            if (!autoEnabled && state != "Running") text = "X-Haptics off";
            else if (state != "Running") text = exclusiveHold ? "audio device in exclusive use by another app (exclusive/ASIO player): haptics resume when it is released"
                                                : WebUiAttached() ? "paused (Space Station page is open)"
                                                : TurnedOffElsewhere() ? "X-Haptics off (turned off in Space Station)" + TurnOnHint()
                                                : "X-Haptics idle";
            else if (src == "process") text = GameName(scope) + " (PID " + pid + ")";
            else text = "Mix (all apps)";
            if (state == "Running") turnedOffNoticed = false;
            if (followParentScope != null)
                text += followViaMix ? "  - following " + followChildName + " of " + followParentName : "  (in " + followParentName + ", following)";
            SourceInfo = "Source: " + text;

            // controller
            bool connected = EngineClient.Bool(st, "hid.connected");
            long battery = EngineClient.Long(st, "hid.controllerDeviceInfo.batteryLevel");
            long charging = EngineClient.Long(st, "hid.controllerDeviceInfo.chargingStatus");
            long mode = EngineClient.Long(st, "hid.controllerDeviceInfo.connectionMode");
            long writeFailures = EngineClient.Long(st, "hid.writeFailures");
            string hidError = EngineClient.Str(st, "hid.lastError") ?? "";
            bool opened = EngineClient.Bool(st, "hid.opened"), paused = EngineClient.Bool(st, "hid.paused");
            bool gateOpen = EngineClient.Bool(st, "realtimeAudio.gripDsp.diagnostics.motorOutput.gripActivityGateOpen");
            string vidHex = EngineClient.Str(st, "hid.vendorIdHex"), pidHex = EngineClient.Str(st, "hid.productIdHex");
            if (vidHex != null && pidHex != null && vidHex.StartsWith("0x") && pidHex.StartsWith("0x"))
                controllerUsbId = "VID_" + vidHex.Substring(2).ToUpperInvariant() + "&PID_" + pidHex.Substring(2).ToUpperInvariant();
            // The controller's USB device is also presented by the wireless receiver while the controller is
            // linked, so it cannot tell cable/dock from wireless; it is only logged for diagnosis.
            bool usbPresent = UsbDevices.IsPresent(controllerUsbId);
            ControllerAsleep = connected && state == "Running" && !gateOpen;
            ControllerConnected = connected;

            string ctl;
            if (!connected) ctl = "not connected (off, out of range, or taken off the cable/dock): press Home";
            else
            {
                ctl = "connected" + (battery >= 0 ? ", battery " + battery + "%" : "") + (charging > 0 ? ", charging" : "");
                if (paused) ctl += ", paused";
                if (writeFailures > lastWriteFailures && lastWriteFailures >= 0 || hidError.Length > 0) ctl += ", output errors";
                if (ControllerAsleep) ctl += ", haptics asleep (press a controller button)";
            }
            lastWriteFailures = writeFailures;
            ControllerInfo = "Controller: " + ctl;

            // one balloon per state change, once the state has lasted controllerNotifyDelaySec
            string stateKey = !connected ? "disconnected" : (ControllerAsleep ? "asleep" : "connected");
            if (stateKey != candidateKey) { candidateKey = stateKey; candidateSince = DateTime.Now; }
            if (candidateKey != lastStableKey && (DateTime.Now - candidateSince).TotalSeconds >= Settings.ControllerNotifyDelaySec)
            {
                string previous = lastStableKey;
                lastStableKey = candidateKey;
                NotifyControllerState(previous, candidateKey, battery);
            }
            if (connected && Settings.LowBatteryPercent > 0 && battery >= 0)
            {
                if (battery <= Settings.LowBatteryPercent && !lowBatteryNotified)
                { lowBatteryNotified = true; Log.Write("controller battery low: " + battery + "%"); Notify("Controller battery low (" + battery + "%, as reported by the engine).", true); }
                else if (battery > Settings.LowBatteryPercent + 5) lowBatteryNotified = false;
            }

            // log every change of the raw controller state (helps with power-off / charging cases)
            string raw = string.Format("connected={0} opened={1} paused={2} battery={3} charging={4} mode={5} usb={6} asleep={7} writeFailures={8} error='{9}'",
                                       connected, opened, paused, battery, charging, mode, usbPresent, ControllerAsleep, writeFailures, hidError);
            if (raw != lastControllerState) { lastControllerState = raw; Log.Write("controller: " + raw); }
        }

        public volatile bool ControllerConnected, ControllerAsleep;
        string controllerUsbId = "VID_37D7&PID_2502", candidateKey, lastStableKey;
        DateTime candidateSince = DateTime.MinValue;
        long lastWriteFailures = -1;
        bool lowBatteryNotified;

        void NotifyControllerState(string previous, string now, long battery)
        {
            string msg = null;
            switch (now)
            {
                case "disconnected":
                    msg = previous == null ? "Controller not connected. Turn it on (Home) or put it on the cable/dock."
                                           : "Controller disconnected (turned off, out of range, or taken off the cable/dock). Press Home to reconnect.";
                    break;
                case "connected":
                    if (previous == "disconnected") msg = "Controller connected.";      // start-up or waking up: nothing to report
                    break;
                case "asleep":
                    if (previous == "disconnected") msg = "Controller connected. Press any controller button to wake haptics.";
                    else if (previous == "connected" && (DateTime.Now - lastKickAt).TotalSeconds >= 15)   // a re-arm balloon already says so
                        msg = "Haptics asleep (no controller input). Press any controller button to wake them.";
                    break;
            }
            Log.Write("controller state: " + (previous ?? "start") + " -> " + now);
            if (msg != null) Notify(msg, true);
        }
    }
}
