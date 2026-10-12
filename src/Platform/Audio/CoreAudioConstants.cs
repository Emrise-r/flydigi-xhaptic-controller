namespace FlydigiHapticsGuard
{
    // Ids and values from the Windows SDK headers (mmdeviceapi.h, audiopolicy.h, endpointvolume.h,
    // audioclient.h). Keep them here; the interop declarations and the audio code refer to them by name.

    // COM class id (CLSID) and interface ids (IID). They are const so [Guid] attributes can use them.
    static class CoreAudioGuids
    {
        public const string MMDeviceEnumerator      = "BCDE0395-E52F-467C-8E3D-C4579291692E";   // CLSID
        public const string IMMDeviceEnumerator     = "A95664D2-9614-4F35-A746-DE8DB63617E6";
        public const string IMMDeviceCollection     = "0BD7A1BE-7A1A-44DB-8397-CC5392387B5E";
        public const string IMMDevice               = "D666063F-1587-4E43-81F1-B948E807363F";
        public const string IAudioSessionManager2   = "77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F";
        public const string IAudioSessionEnumerator = "E2F5BB11-0570-40CA-ACDD-3AA01277DEE8";
        public const string IAudioSessionControl2   = "BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D";
        public const string IAudioMeterInformation  = "C02216F6-8C67-4B5B-9D00-D008E73E0064";
        public const string IAudioClient            = "1CB9AD4C-DBFA-4C32-B178-C2F568A703B2";
    }

    static class HResult
    {
        public const int S_OK = 0;
        public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    }

    // REFERENCE_TIME, in 100 ns units
    static class ReferenceTime
    {
        public const long OneSecond = 10000000;
    }

    enum EDataFlow { Render = 0, Capture = 1, All = 2 }

    enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [System.Flags]
    enum DeviceState { Active = 1, Disabled = 2, NotPresent = 4, Unplugged = 8 }

    enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    enum AudioClientShareMode { Shared = 0, Exclusive = 1 }

    [System.Flags]
    enum ClsCtx { InprocServer = 1, InprocHandler = 2, LocalServer = 4, RemoteServer = 16, All = InprocServer | InprocHandler | LocalServer | RemoteServer }
}
