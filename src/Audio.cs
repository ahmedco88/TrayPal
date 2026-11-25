// PC sound: volume, mute and device name of the default output, through Core Audio (COM, no extra install).
// The endpoint is fetched fresh per call so a change of default device is followed.
// COM calls go by slot number: do not reorder or remove interface methods.
using System;
using System.Runtime.InteropServices;

static class Audio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(int channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    // Default output device's volume control, or null. Caller releases it.
    static IAudioEndpointVolume Endpoint()
    {
        IMMDeviceEnumerator en = null; IMMDevice dev = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return null;   // eRender, eMultimedia
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object o;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return null;              // CLSCTX_ALL
            return (IAudioEndpointVolume)o;
        }
        catch { return null; }
        finally
        {
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    // false = no default output device (RDP, unplugged): the caller hides the PC sound section.
    public static bool TryGet(out int percent, out bool muted)
    {
        percent = 0; muted = false;
        IAudioEndpointVolume v = null;
        try
        {
            v = Endpoint();
            if (v == null) return false;
            float f;
            if (v.GetMasterVolumeLevelScalar(out f) != 0) return false;
            bool m;
            if (v.GetMute(out m) != 0) return false;
            percent = (int)Math.Round(f * 100);
            muted = m;
            return true;
        }
        catch { return false; }
        finally { if (v != null) Marshal.ReleaseComObject(v); }
    }

    public static void SetVolume(int percent)
    {
        IAudioEndpointVolume v = null;
        try
        {
            v = Endpoint();
            if (v == null) return;
            Guid ctx = Guid.Empty;
            v.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(100, percent)) / 100f, ref ctx);
        }
        catch { }
        finally { if (v != null) Marshal.ReleaseComObject(v); }
    }

    public static void SetMute(bool muted)
    {
        IAudioEndpointVolume v = null;
        try
        {
            v = Endpoint();
            if (v == null) return;
            Guid ctx = Guid.Empty;
            v.SetMute(muted, ref ctx);
        }
        catch { }
        finally { if (v != null) Marshal.ReleaseComObject(v); }
    }

    // Friendly name of the default output, or null when unknown.
    public static string DeviceName()
    {
        IMMDeviceEnumerator en = null; IMMDevice dev = null; IPropertyStore ps = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return null;
            if (dev.OpenPropertyStore(0, out ps) != 0 || ps == null) return null;   // STGM_READ
            var key = new PROPERTYKEY();
            key.fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");           // PKEY_Device_FriendlyName
            key.pid = 14;
            PROPVARIANT pv;
            if (ps.GetValue(ref key, out pv) != 0) return null;
            try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null; }       // VT_LPWSTR
            finally { PropVariantClear(ref pv); }
        }
        catch { return null; }
        finally
        {
            if (ps != null) Marshal.ReleaseComObject(ps);
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }
}
