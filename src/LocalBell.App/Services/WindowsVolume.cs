using System.Runtime.InteropServices;

namespace LocalBell.App.Services;

/// <summary>
/// "Ring even when the Windows sound is off": while the bell rings, the sound of the PC is turned back on (and raised
/// to a quarter when it is lower), then put back as it was, unless the user changed it meanwhile. Through the Windows
/// Core Audio API (the default output device).
/// </summary>
public sealed class WindowsVolume
{
    private const float Minimum = 0.25f;

    private readonly IAudioEndpointVolume endpoint;
    private readonly bool wasMuted;
    private readonly float level;
    private readonly float raisedTo;

    private WindowsVolume(IAudioEndpointVolume endpoint, bool wasMuted, float level, float raisedTo)
    {
        this.endpoint = endpoint;
        this.wasMuted = wasMuted;
        this.level = level;
        this.raisedTo = raisedTo;
    }

    /// <summary>Makes the PC audible; null when it already was (nothing to put back) or there is no sound device.</summary>
    public static WindowsVolume? MakeAudible()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device));
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 1 /* in process */, IntPtr.Zero, out object activated));
            var endpoint = (IAudioEndpointVolume)activated;

            Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
            Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out float level));
            if (!muted && level >= Minimum) return null;

            var context = Guid.Empty;
            if (muted) endpoint.SetMute(false, ref context);
            float raisedTo = level;
            if (level < Minimum)
            {
                endpoint.SetMasterVolumeLevelScalar(Minimum, ref context);
                raisedTo = Minimum;
            }
            return new WindowsVolume(endpoint, muted, level, raisedTo);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Puts the sound back as it was, unless the user changed it while the bell rang.</summary>
    public void Restore()
    {
        try
        {
            var context = Guid.Empty;
            if (endpoint.GetMasterVolumeLevelScalar(out float now) == 0 && Math.Abs(now - raisedTo) < 0.01f && raisedTo != level)
                endpoint.SetMasterVolumeLevelScalar(level, ref context);
            if (wasMuted && endpoint.GetMute(out bool muted) == 0 && !muted)
                endpoint.SetMute(true, ref context);
        }
        catch (COMException) { }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDb, ref Guid context);
        int SetMasterVolumeLevelScalar(float level, ref Guid context);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        int GetChannelVolumeLevel(uint channel, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
