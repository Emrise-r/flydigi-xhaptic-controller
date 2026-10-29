using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace FlydigiHapticsGuard.Tests
{
    static class EngineJsonTests
    {
        static Dictionary<string, object> Parse(string json)
        {
            return EngineClient.Obj(new JavaScriptSerializer().DeserializeObject(Sandbox.Json(json)));
        }

        [Test]
        static void At_follows_dotted_paths()
        {
            var o = Parse("{'a':{'b':{'c':5}}}");
            Assert.Equal(5, EngineClient.At(o, "a.b.c"));
            Assert.Null(EngineClient.At(o, "a.x.c"), "missing key");
            Assert.Null(EngineClient.At(o, "a.b.c.d"), "path past a number");
        }

        [Test]
        static void Typed_readers_fall_back_on_missing_or_wrong_values()
        {
            var o = Parse("{'n':5,'s':'text','b':true,'bs':'true','bad':'abc'}");
            Assert.Equal("5", EngineClient.Str(o, "n"));
            Assert.Null(EngineClient.Str(o, "missing"));
            Assert.True(EngineClient.Bool(o, "b"));
            Assert.False(EngineClient.Bool(o, "bs"), "a string is not a bool");
            Assert.Equal(5L, EngineClient.Long(o, "n"));
            Assert.Equal(-1L, EngineClient.Long(o, "missing"));
            Assert.Equal(-1L, EngineClient.Long(o, "bad"));
            Assert.Equal(0.0, EngineClient.Num(o, "missing"));
        }
    }

    static class XHapticsStatusTests
    {
        const string Sample = @"{
          'session': { 'state': 'Running', 'scope': 'bh3' },
          'realtimeAudio': {
            'wasapi': { 'running': true, 'lastError': '', 'source': 'process', 'processId': 1234, 'frameSeq': 140, 'peak': 0.25 },
            'output': { 'running': true, 'lastError': '' },
            'gripDsp': { 'diagnostics': { 'motorOutput': { 'gripActivityGateOpen': false } } }
          },
          'hid': {
            'connected': true, 'opened': true, 'paused': false, 'writeFailures': 9, 'lastError': '',
            'vendorIdHex': '0x37d7', 'productIdHex': '0x2502',
            'controllerDeviceInfo': { 'batteryLevel': 7, 'chargingStatus': 0, 'connectionMode': 1 }
          }
        }";

        static XHapticsStatus Parse(string json)
        {
            return XHapticsStatus.Parse(EngineClient.Obj(new JavaScriptSerializer().DeserializeObject(Sandbox.Json(json))));
        }

        [Test]
        static void Parses_every_field()
        {
            var s = Parse(Sample);
            Assert.Equal("Running", s.SessionState);
            Assert.Equal("bh3", s.Scope);
            Assert.True(s.CaptureRunning, "capture running");
            Assert.True(s.OutputRunning, "output running");
            Assert.Equal("process", s.CaptureSource);
            Assert.Equal(1234L, s.CapturePid);
            Assert.Equal(140L, s.FrameSeq);
            Assert.Equal(0.25, s.EnginePeak);
            Assert.False(s.GripGateOpen, "grip gate");
            Assert.True(s.HidConnected, "connected");
            Assert.True(s.HidOpened, "opened");
            Assert.False(s.HidPaused, "paused");
            Assert.Equal(7L, s.BatteryLevel);
            Assert.Equal(70L, s.BatteryPercent);
            Assert.Equal(0L, s.Charging);
            Assert.Equal(1L, s.ConnectionMode);
            Assert.Equal(9L, s.WriteFailures);
            Assert.Equal("VID_37D7&PID_2502", s.UsbId);
        }

        [Test]
        static void Derived_flags()
        {
            var s = Parse(Sample);
            Assert.True(s.Running, "Running");
            Assert.False(s.Idle, "Idle");
            Assert.False(s.PausedSession, "PausedSession");
            Assert.True(s.ProcessCapture, "ProcessCapture");
            Assert.False(s.SystemCapture, "SystemCapture");
            Assert.False(s.HasError, "HasError");

            var paused = Parse("{'session':{'state':'PausedByOwner'},'realtimeAudio':{'wasapi':{'lastError':'0x88890004'}}}");
            Assert.True(paused.PausedSession, "PausedByOwner");
            Assert.True(paused.HasError, "capture error");
        }

        [Test]
        static void Empty_status_reads_as_defaults()
        {
            var s = Parse("{}");
            Assert.Equal("", s.SessionState);
            Assert.Null(s.Scope, "scope");
            Assert.False(s.Running, "Running");
            Assert.True(s.SystemCapture, "no source = system capture");
            Assert.False(s.ProcessCapture, "ProcessCapture");
            Assert.Equal("", s.CaptureError);
            Assert.Equal(-1L, s.BatteryLevel);
            Assert.Equal(-1L, s.BatteryPercent, "unknown battery");
            Assert.Equal(-1L, s.CapturePid);
            Assert.Equal(0.0, s.EnginePeak);
            Assert.False(s.HidConnected, "connected");
            Assert.Null(s.UsbId, "usb id");
        }

        static XHapticsStatus Battery(int raw) { return Parse("{'hid':{'controllerDeviceInfo':{'batteryLevel':" + raw + ",'chargingStatus':0}}}"); }

        [Test]
        static void Battery_byte_is_level_and_charge_state()
        {
            var full = Battery(10);
            Assert.Equal(100L, full.BatteryPercent, "10/10");
            Assert.False(full.IsCharging, "not charging");

            var charging = Battery(0x15);   // 21: what the engine reported on the dock with the controller at ~60 %
            Assert.Equal(5L, charging.BatteryLevel, "level");
            Assert.Equal(50L, charging.BatteryPercent, "percent");
            Assert.True(charging.IsCharging, "charging");

            Assert.True(Battery(0x2A).IsFull, "0x2A = 10/10, fully charged");
            Assert.Equal(0L, Battery(0).BatteryPercent, "0/10");
            Assert.Equal(-1L, Battery(0x0B).BatteryPercent, "level 11 is invalid");
            Assert.Equal(-1L, Parse("{}").BatteryPercent, "missing");
            Assert.False(Parse("{}").IsCharging, "missing: not charging");
        }

        [Test]
        static void Usb_id_needs_both_hex_ids()
        {
            Assert.Null(Parse("{'hid':{'vendorIdHex':'0x37d7'}}").UsbId, "product id missing");
            Assert.Null(Parse("{'hid':{'vendorIdHex':'37d7','productIdHex':'2502'}}").UsbId, "no 0x prefix");
        }
    }

    static class XHapticsConfigsTests
    {
        const string Sample = @"{
          'revision': 42,
          'configurations': {
            'global': { 'enabled': true, 'selectedBackend': 'realtime-audio', 'ui': { 'gain': 3 } },
            'bh3':    { 'enabled': false },
            'broken': 'not an object'
          }
        }";

        static XHapticsConfigs Parse(string json)
        {
            return XHapticsConfigs.Parse(EngineClient.Obj(new JavaScriptSerializer().DeserializeObject(Sandbox.Json(json))));
        }

        [Test]
        static void Reads_profiles_and_revision()
        {
            var c = Parse(Sample);
            Assert.Equal(42L, c.Revision);
            Assert.False(c.IsEmpty, "IsEmpty");
            Assert.True(c.Has("global"), "global");
            Assert.True(c.IsEnabled("global"), "global enabled");
            Assert.True(c.Has("bh3"), "bh3");
            Assert.False(c.IsEnabled("bh3"), "bh3 disabled");
        }

        [Test]
        static void Missing_or_invalid_scopes_are_absent_and_off()
        {
            var c = Parse(Sample);
            Assert.False(c.Has("chrome"), "unknown scope");
            Assert.False(c.IsEnabled("chrome"), "unknown scope enabled");
            Assert.False(c.Has(null), "null scope");
            Assert.False(c.Has("broken"), "profile that is not an object");
            Assert.True(Parse("{}").IsEmpty, "no configurations");
            Assert.Equal(-1L, Parse("{}").Revision);
        }

        [Test]
        static void Patch_body_keeps_backend_and_ui()
        {
            var c = Parse(Sample);
            var body = XHapticsConfigs.PatchBody(c.Find("global"), false, 43);
            Assert.Equal(43L, body["expectedRevision"]);
            Assert.Equal(false, body["enabled"]);
            Assert.Equal("realtime-audio", body["selectedBackend"]);
            Assert.Equal(3, EngineClient.At(EngineClient.Obj(body), "ui.gain"));

            var minimal = XHapticsConfigs.PatchBody(c.Find("bh3"), true, 43);
            Assert.Equal("realtime-audio", minimal["selectedBackend"], "default backend");
            Assert.NotNull(EngineClient.Obj(minimal["ui"]), "empty ui object");
        }

        [Test]
        static void Scope_urls_are_escaped()
        {
            Assert.Equal("/api/v1/xhaptics/configurations/global", EngineApi.Configuration("global"));
            Assert.Equal("/api/v1/xhaptics/configurations/a%20b%2Fc", EngineApi.Configuration("a b/c"));
            Assert.Equal("/api/v1/xhaptics/realtime-audio/configurations/global", EngineApi.RealtimeAudioConfiguration("global"));
        }
    }

    static class XHapticsGameTests
    {
        [Test]
        static void Parses_the_games_list()
        {
            var root = EngineClient.Obj(new JavaScriptSerializer().DeserializeObject(Sandbox.Json(@"{ 'games': [
                { 'gameId': 'bh3', 'displayName': 'Honkai Impact 3rd', 'processNames': ['BH3.exe', ' ', 'bh3launcher'], 'supportedBackends': ['realtime-audio'] },
                { 'gameId': 'x', 'supportedBackends': ['native'] },
                'junk'
            ] }")));
            var games = XHapticsGame.ParseList(EngineClient.At(root, "games"));
            Assert.Equal(2, games.Count, "entries that are objects");
            Assert.Equal("bh3", games[0].Id);
            Assert.Equal("Honkai Impact 3rd", games[0].Name);
            Assert.SequenceEqual(new[] { "BH3", "bh3launcher" }, games[0].ProcessNames, "names without .exe and blanks");
            Assert.Null(games[1].Name, "no display name");
            Assert.Equal(0, games[1].ProcessNames.Count, "no process names");
            Assert.False(games[0].NativeOnly, "X-Haptics game");
            Assert.True(games[1].NativeOnly, "native haptics game");
            Assert.Null(XHapticsGame.ParseList(null), "missing list");
        }
    }
}
