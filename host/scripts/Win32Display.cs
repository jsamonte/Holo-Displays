using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HoloDisplays;

/// <summary>
/// Win32 display enumeration and mode switching.
///
/// Godot's DisplayServer gives us screen count, size and position, but not the
/// device name (\\.\DISPLAY2), the adapter's friendly name, or the list of modes
/// a monitor supports. All of that comes from here, and the two views get
/// matched up by desktop rectangle in <see cref="DisplayManager"/>.
///
/// Everything is P/Invoke against user32 — no native dependencies to ship.
/// </summary>
public static class Win32Display
{
    // ---- structures --------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        // POINTL dmPosition, part of the union used for display devices.
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    // ---- imports -----------------------------------------------------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum,
        ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, ref DEVMODE lpDevMode,
        IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, IntPtr lpDevMode,
        IntPtr hwnd, uint dwflags, IntPtr lParam);

    // ---- constants ---------------------------------------------------------

    private const int ENUM_CURRENT_SETTINGS = -1;

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYFREQUENCY = 0x00400000;
    private const uint DM_BITSPERPEL = 0x00040000;
    private const uint DM_POSITION = 0x00000020;

    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_NORESET = 0x10000000;

    /// <summary>ChangeDisplaySettingsEx return codes, for readable errors.</summary>
    public static string DescribeChangeResult(int code) => code switch
    {
        0 => "DISP_CHANGE_SUCCESSFUL",
        1 => "DISP_CHANGE_RESTART (needs a reboot)",
        -1 => "DISP_CHANGE_FAILED",
        -2 => "DISP_CHANGE_BADMODE (the display does not support that mode)",
        -3 => "DISP_CHANGE_NOTUPDATED",
        -4 => "DISP_CHANGE_BADFLAGS",
        -5 => "DISP_CHANGE_BADPARAM",
        -6 => "DISP_CHANGE_BADDUALVIEW",
        _ => $"unknown ({code})",
    };

    // ---- public model ------------------------------------------------------

    public sealed class Adapter
    {
        /// <summary>e.g. <c>\\.\DISPLAY2</c></summary>
        public string DeviceName = "";

        /// <summary>Adapter name, e.g. "Virtual Display Driver".</summary>
        public string AdapterString = "";

        /// <summary>Monitor name, e.g. "Generic PnP Monitor". Empty if none attached.</summary>
        public string MonitorString = "";

        public string DeviceId = "";
        public bool IsPrimary;
        public int X, Y, Width, Height;
        public int RefreshHz;

        /// <summary>Display number parsed out of DeviceName. This is the protocol's <c>id</c>.</summary>
        public int Id;

        /// <summary>True when this looks like a VDD virtual monitor.</summary>
        public bool IsVirtual;

        /// <summary>Distinct [w,h] pairs this display can switch to, largest first.</summary>
        public List<(int W, int H)> Modes = new();

        public string FriendlyName => string.IsNullOrWhiteSpace(MonitorString) ? AdapterString : MonitorString;
        public override string ToString() => $"{DeviceName} {FriendlyName} {Width}x{Height}";
    }

    // ---- enumeration -------------------------------------------------------

    /// <summary>
    /// Every display currently attached to the desktop, with its current mode
    /// and the list of modes it supports.
    /// </summary>
    public static List<Adapter> Enumerate()
    {
        var result = new List<Adapter>();

        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;

            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var a = new Adapter
            {
                DeviceName = dd.DeviceName,
                AdapterString = dd.DeviceString,
                DeviceId = dd.DeviceID,
                IsPrimary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                Id = ParseDisplayNumber(dd.DeviceName),
            };

            // Second call, scoped to this adapter, gets the attached monitor.
            var mon = new DISPLAY_DEVICE();
            mon.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if (EnumDisplayDevicesW(dd.DeviceName, 0, ref mon, 0))
            {
                a.MonitorString = mon.DeviceString;
                // The monitor's DeviceID carries the hardware id, which is where
                // MttVDD shows up. Keep it for virtual detection.
                if (!string.IsNullOrEmpty(mon.DeviceID)) a.DeviceId = mon.DeviceID;
            }

            var cur = new DEVMODE();
            cur.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            if (EnumDisplaySettingsW(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur))
            {
                a.X = cur.dmPositionX;
                a.Y = cur.dmPositionY;
                a.Width = (int)cur.dmPelsWidth;
                a.Height = (int)cur.dmPelsHeight;
                a.RefreshHz = (int)cur.dmDisplayFrequency;
            }

            a.IsVirtual = LooksVirtual(a);
            a.Modes = EnumerateModes(dd.DeviceName);
            result.Add(a);
        }

        return result;
    }

    /// <summary>
    /// Distinct resolutions for a device, largest area first. Refresh rates are
    /// collapsed away — the protocol only deals in [w,h], and the mode switch
    /// picks the highest rate available for the chosen size.
    /// </summary>
    public static List<(int W, int H)> EnumerateModes(string deviceName)
    {
        var seen = new HashSet<(int, int)>();
        var modes = new List<(int W, int H)>();

        for (int m = 0; ; m++)
        {
            var dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            if (!EnumDisplaySettingsW(deviceName, m, ref dm)) break;

            // Ignore anything below 640x480; VDD and real panels both advertise
            // junk legacy modes that are useless as a monitor.
            if (dm.dmPelsWidth < 640 || dm.dmPelsHeight < 480) continue;

            var key = ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
            if (seen.Add(key)) modes.Add(key);
        }

        modes.Sort((a, b) => (b.W * b.H).CompareTo(a.W * a.H));
        return modes;
    }

    /// <summary>Highest refresh rate the device reports for a given size.</summary>
    public static int BestRefreshFor(string deviceName, int w, int h)
    {
        int best = 0;
        for (int m = 0; ; m++)
        {
            var dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            if (!EnumDisplaySettingsW(deviceName, m, ref dm)) break;
            if (dm.dmPelsWidth == (uint)w && dm.dmPelsHeight == (uint)h)
                best = Math.Max(best, (int)dm.dmDisplayFrequency);
        }
        return best;
    }

    // ---- mode switching ----------------------------------------------------

    /// <summary>
    /// Switches a display to a new resolution.
    ///
    /// Two-step on purpose: CDS_UPDATEREGISTRY|CDS_NORESET writes the change
    /// without applying it, then the null-device call applies every pending
    /// change at once. Doing it in one shot makes Windows reshuffle the desktop
    /// layout far more aggressively.
    ///
    /// The monitor's desktop position is carried over so the arrangement does
    /// not jump when a panel is resized.
    /// </summary>
    public static (bool Ok, string Message) ChangeResolution(string deviceName, int w, int h)
    {
        var dm = new DEVMODE();
        dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
        if (!EnumDisplaySettingsW(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
            return (false, "could not read current settings");

        dm.dmPelsWidth = (uint)w;
        dm.dmPelsHeight = (uint)h;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_POSITION;

        int hz = BestRefreshFor(deviceName, w, h);
        if (hz > 0)
        {
            dm.dmDisplayFrequency = (uint)hz;
            dm.dmFields |= DM_DISPLAYFREQUENCY;
        }

        int rc = ChangeDisplaySettingsExW(deviceName, ref dm, IntPtr.Zero,
            CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);

        if (rc != 0)
            return (false, Win32Display.DescribeChangeResult(rc));

        // Apply everything staged above.
        int applied = ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        return applied == 0
            ? (true, "DISP_CHANGE_SUCCESSFUL")
            : (false, Win32Display.DescribeChangeResult(applied));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary><c>\\.\DISPLAY2</c> becomes 2.</summary>
    public static int ParseDisplayNumber(string deviceName)
    {
        int i = deviceName.Length;
        while (i > 0 && char.IsDigit(deviceName[i - 1])) i--;
        return i < deviceName.Length && int.TryParse(deviceName[i..], out int n) ? n : 0;
    }

    /// <summary>
    /// Recognises a VDD monitor. The INF names the device "Virtual Display
    /// Driver" with hardware id Root\MttVDD, so either string is a good signal.
    /// Kept deliberately loose — other virtual display drivers are fine to pick
    /// up too, since the point is "not a physical panel".
    /// </summary>
    private static bool LooksVirtual(Adapter a)
    {
        var hay = (a.AdapterString + " " + a.MonitorString + " " + a.DeviceId).ToUpperInvariant();
        return hay.Contains("MTTVDD")
            || hay.Contains("VIRTUAL DISPLAY")
            || hay.Contains("IDDSAMPLE")
            || hay.Contains("VIRTUALDISPLAY");
    }
}
