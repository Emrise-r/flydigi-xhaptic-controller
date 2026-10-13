using System.Collections.Generic;

namespace FlydigiHapticsGuard
{
    // Typed view of GET /api/xhaptics/status: the only place that knows the JSON paths.
    // Missing numbers read as -1 (counters, ids, battery) or 0 (levels), missing text as "" unless noted.
    class XHapticsStatus
    {
        // session: which profile (scope) the engine runs, and whether it runs at all
        public string SessionState;        // "Running", "Idle", "Paused..."
        public string Scope;               // "global" or a game id; null when none

        // audio capture (WASAPI loopback) and motor output
        public bool CaptureRunning, OutputRunning;
        public string CaptureError, OutputError;
        public string CaptureSource;       // "system", "process"; null when unknown
        public long CapturePid, FrameSeq;
        public double EnginePeak;
        public bool GripGateOpen;          // false while haptics are asleep (no controller input)

        // controller (HID)
        public bool HidConnected, HidOpened, HidPaused;
        public long BatteryLevel;          // 0-10 (Space Station shows "level/10"); other values are invalid readings
        public long Charging, ConnectionMode, WriteFailures;
        public string HidError;
        public string UsbId;               // "VID_37D7&PID_2502", or null when the engine does not say

        public bool Running { get { return SessionState == "Running"; } }
        public bool Idle { get { return SessionState == "Idle"; } }
        public bool PausedSession { get { return SessionState.IndexOf("Pause", System.StringComparison.OrdinalIgnoreCase) >= 0; } }
        public bool SystemCapture { get { return (CaptureSource ?? "system") == "system"; } }
        public bool ProcessCapture { get { return CaptureSource == "process"; } }
        public bool HasError { get { return CaptureError.Length > 0 || OutputError.Length > 0; } }
        // battery in percent (10 % steps), or -1 when unknown or out of range (the engine sometimes reports e.g. 22)
        public long BatteryPercent { get { return BatteryLevel >= 0 && BatteryLevel <= 10 ? BatteryLevel * 10 : -1; } }

        public static XHapticsStatus Parse(Dictionary<string, object> st)
        {
            return new XHapticsStatus
            {
                SessionState   = EngineClient.Str(st, "session.state") ?? "",
                Scope          = EngineClient.Str(st, "session.scope"),

                CaptureRunning = EngineClient.Bool(st, "realtimeAudio.wasapi.running"),
                OutputRunning  = EngineClient.Bool(st, "realtimeAudio.output.running"),
                CaptureError   = EngineClient.Str(st, "realtimeAudio.wasapi.lastError") ?? "",
                OutputError    = EngineClient.Str(st, "realtimeAudio.output.lastError") ?? "",
                CaptureSource  = EngineClient.Str(st, "realtimeAudio.wasapi.source"),
                CapturePid     = EngineClient.Long(st, "realtimeAudio.wasapi.processId"),
                FrameSeq       = EngineClient.Long(st, "realtimeAudio.wasapi.frameSeq"),
                EnginePeak     = EngineClient.Num(st, "realtimeAudio.wasapi.peak"),
                GripGateOpen   = EngineClient.Bool(st, "realtimeAudio.gripDsp.diagnostics.motorOutput.gripActivityGateOpen"),

                HidConnected   = EngineClient.Bool(st, "hid.connected"),
                HidOpened      = EngineClient.Bool(st, "hid.opened"),
                HidPaused      = EngineClient.Bool(st, "hid.paused"),
                BatteryLevel   = EngineClient.Long(st, "hid.controllerDeviceInfo.batteryLevel"),
                Charging       = EngineClient.Long(st, "hid.controllerDeviceInfo.chargingStatus"),
                ConnectionMode = EngineClient.Long(st, "hid.controllerDeviceInfo.connectionMode"),
                WriteFailures  = EngineClient.Long(st, "hid.writeFailures"),
                HidError       = EngineClient.Str(st, "hid.lastError") ?? "",
                UsbId          = UsbIdOf(EngineClient.Str(st, "hid.vendorIdHex"), EngineClient.Str(st, "hid.productIdHex")),
            };
        }

        // "0x37d7", "0x2502" -> "VID_37D7&PID_2502" (the Windows device id form)
        static string UsbIdOf(string vidHex, string pidHex)
        {
            if (vidHex == null || pidHex == null || !vidHex.StartsWith("0x") || !pidHex.StartsWith("0x")) return null;
            return "VID_" + vidHex.Substring(2).ToUpperInvariant() + "&PID_" + pidHex.Substring(2).ToUpperInvariant();
        }
    }
}
