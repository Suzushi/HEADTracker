using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace HeadTracker.App.Services;

/// <summary>
/// Enumerates DirectShow video-capture devices. The enumeration walks
/// <c>ICreateDevEnum</c> over <c>CLSID_VideoInputDeviceCategory</c> — the very
/// same path OpenCV's DSHOW backend (used by <c>CameraCapture</c>) takes — so
/// a device's position in the returned list equals the <c>camera_id</c> that
/// opens it. Enumeration is best-effort: any COM failure yields an empty list
/// and the caller falls back to numeric ids.
/// </summary>
public static class CameraEnumerator
{
    /// <summary>One selectable camera: its DSHOW index and a ready-to-show label.</summary>
    public sealed class CameraDevice
    {
        public int Index { get; }
        public string Display { get; }

        public CameraDevice(int index, string display)
        {
            Index = index;
            Display = display;
        }
    }

    // CLSID_SystemDeviceEnum
    private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    // CLSID_VideoInputDeviceCategory
    private static readonly Guid ClsidVideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");

    private static List<CameraDevice>? _cache;

    /// <summary>Enumerates once and remembers the result. Call at startup, before any capture
    /// graph exists: re-enumerating while a stream is running has been seen to take the whole
    /// process down silently on some camera drivers.</summary>
    public static void WarmCache() => _cache = GetVideoCaptureDevices();

    /// <summary>The cached device list; enumerates on first use only.</summary>
    public static List<CameraDevice> GetCached() => _cache ??= GetVideoCaptureDevices();

    /// <summary>Re-enumerates and replaces the cache. Only safe while no capture is streaming.</summary>
    public static void RefreshCache() => _cache = GetVideoCaptureDevices();

    /// <summary>
    /// Returns the connected capture devices in DSHOW order. Must be called on an
    /// STA thread (the WPF UI thread is STA).
    /// </summary>
    public static List<CameraDevice> GetVideoCaptureDevices()
    {
        var result = new List<CameraDevice>();
        object? devEnum = null;
        IEnumMoniker? enumMoniker = null;
        try
        {
            var enumType = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum);
            if (enumType == null)
            {
                return result;
            }
            devEnum = Activator.CreateInstance(enumType);
            if (devEnum == null)
            {
                return result;
            }

            var category = ClsidVideoInputDeviceCategory;
            // S_OK -> an enumerator; S_FALSE -> category present but no devices.
            int hr = ((ICreateDevEnum)devEnum).CreateClassEnumerator(ref category, out enumMoniker, 0);
            if (hr != 0 || enumMoniker == null)
            {
                return result;
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                if (moniker == null)
                {
                    continue;
                }
                string name = ReadFriendlyName(moniker);
                int index = result.Count;
                result.Add(new CameraDevice(index, $"{index} — {name}"));
                Marshal.ReleaseComObject(moniker);
            }
        }
        catch
        {
            // Best-effort: on any COM/marshalling problem the caller shows numeric ids.
        }
        finally
        {
            if (enumMoniker != null)
            {
                Marshal.ReleaseComObject(enumMoniker);
            }
            if (devEnum != null)
            {
                Marshal.ReleaseComObject(devEnum);
            }
        }
        return result;
    }

    private static string ReadFriendlyName(IMoniker moniker)
    {
        object? bagObj = null;
        try
        {
            var iid = typeof(IPropertyBag).GUID;
            moniker.BindToStorage(null!, null!, ref iid, out bagObj);
            if (bagObj is IPropertyBag bag)
            {
                bag.Read("FriendlyName", out object value, IntPtr.Zero);
                if (value is string { Length: > 0 } name)
                {
                    return name;
                }
            }
        }
        catch
        {
            // Fall through to the generic label.
        }
        finally
        {
            if (bagObj != null)
            {
                Marshal.ReleaseComObject(bagObj);
            }
        }
        return "Camera";
    }

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid deviceType, [MarshalAs(UnmanagedType.Interface)] out IEnumMoniker? enumMoniker, [In] int flags);
    }

    [ComImport]
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([MarshalAs(UnmanagedType.LPWStr)] string propName, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);

        [PreserveSig]
        int Write([MarshalAs(UnmanagedType.LPWStr)] string propName, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
