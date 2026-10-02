using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace HoloDisplays;

/// <summary>
/// One display the host knows about: the Win32 facts, the Godot screen index
/// needed to capture it, and the streaming state.
/// </summary>
public sealed class TargetDisplay
{
    public int Id;                    // protocol id == Windows display number
    public string DeviceName = "";    // \\.\DISPLAY2
    public string Name = "";
    public bool IsVirtual;
    public bool IsPrimary;

    public int X, Y, Width, Height;
    public List<(int W, int H)> Modes = new();

    /// <summary>Godot screen index, or -1 if it could not be matched.</summary>
    public int ScreenIndex = -1;

    /// <summary>Whether the user has ticked this display for streaming.</summary>
    public bool Stream;

    // --- streaming state, owned by CaptureEngine / StreamServer ---
    public string Tier = "off";
    public uint Seq;
    public uint AwaitingAck;
    public ulong LastSentMsec;
    public ulong FrameSentMsec;
    public ulong LastHash;
    public bool ForceNextFrame;

    /// <summary>When a frame last actually went out, for measuring the real rate.</summary>
    public ulong LastDeliveredMsec;

    /// <summary>True when the most recent capture matched the last one sent.</summary>
    public bool LastWasUnchanged;

    // --- stats for the UI ---
    public double CaptureMs;
    public double EncodeMs;
    public double Fps;
    public double KbPerSec;
    public int LastBytes;

    public bool Capturable => ScreenIndex >= 0;
    public override string ToString() => $"{Name} ({DeviceName}) {Width}x{Height}";
}

/// <summary>
/// Discovers displays and reconciles two different views of them.
///
/// Godot's DisplayServer can capture a screen but only knows it by index.
/// Win32 knows device names, friendly names, virtual-ness and the mode list but
/// cannot capture. The two are matched by desktop rectangle, which is reliable
/// because Windows does not let two displays occupy the same origin.
/// </summary>
public sealed class DisplayManager
{
    public List<TargetDisplay> Displays { get; private set; } = new();

    /// <summary>
    /// Set HOLO_STREAM_PRIMARY=1 to offer the primary display for streaming by
    /// default. For testing the capture and streaming path on a machine with no
    /// virtual monitors. Never affects resolution changes, which refuse the
    /// primary unconditionally.
    /// </summary>
    public static bool StreamPrimaryRequested =>
        System.Environment.GetEnvironmentVariable("HOLO_STREAM_PRIMARY") == "1";

    /// <summary>Raised whenever the display set or any mode changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Re-reads displays from Windows. Preserves per-display streaming state
    /// across the refresh, so a mode switch or a driver reload does not reset
    /// the user's tick boxes or a client's tier.
    /// </summary>
    public void Refresh()
    {
        var previous = Displays.ToDictionary(d => d.Id);
        var adapters = Win32Display.Enumerate();
        var fresh = new List<TargetDisplay>();

        foreach (var a in adapters)
        {
            var d = new TargetDisplay
            {
                Id = a.Id,
                DeviceName = a.DeviceName,
                Name = a.FriendlyName,
                IsVirtual = a.IsVirtual,
                IsPrimary = a.IsPrimary,
                X = a.X,
                Y = a.Y,
                Width = a.Width,
                Height = a.Height,
                Modes = a.Modes,
                ScreenIndex = MatchGodotScreen(a),
            };

            if (previous.TryGetValue(d.Id, out var old))
            {
                d.Stream = old.Stream;
                d.Tier = old.Tier;
                d.Seq = old.Seq;
                // A refresh usually means the picture changed, so do not let the
                // unchanged-frame check suppress the next send.
                d.ForceNextFrame = true;
            }
            else
            {
                // Default: stream every virtual monitor, never the primary.
                d.Stream = d.IsVirtual && !d.IsPrimary;

                // Development escape hatch. With no VDD installed there are no
                // virtual monitors, so nothing streams and the whole frame path
                // is untestable. HOLO_STREAM_PRIMARY=1 offers the primary
                // instead. This is mirroring, which is explicitly not the point
                // of the project, so it is opt-in and never a default.
                if (!d.Stream && d.IsPrimary && StreamPrimaryRequested)
                    d.Stream = true;
            }

            fresh.Add(d);
        }

        Displays = fresh;
        Changed?.Invoke();
    }

    /// <summary>
    /// Finds the Godot screen index whose rectangle matches this adapter.
    ///
    /// Exact origin match first. Falls back to nearest origin within a small
    /// tolerance, because DPI scaling can make Godot and Win32 disagree by a
    /// pixel or two on some setups.
    /// </summary>
    private static int MatchGodotScreen(Win32Display.Adapter a)
    {
        int count = DisplayServer.GetScreenCount();

        for (int i = 0; i < count; i++)
        {
            var p = DisplayServer.ScreenGetPosition(i);
            var s = DisplayServer.ScreenGetSize(i);
            if (p.X == a.X && p.Y == a.Y && s.X == a.Width && s.Y == a.Height)
                return i;
        }

        int best = -1;
        long bestDist = long.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var p = DisplayServer.ScreenGetPosition(i);
            long dx = p.X - a.X, dy = p.Y - a.Y;
            long dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; best = i; }
        }

        return bestDist <= 4 * 4 ? best : -1;
    }

    public TargetDisplay? ById(int id) => Displays.FirstOrDefault(d => d.Id == id);

    /// <summary>Displays currently offered to clients.</summary>
    public IEnumerable<TargetDisplay> Streamed => Displays.Where(d => d.Stream && d.Capturable);

    /// <summary>
    /// Switches a display's resolution.
    ///
    /// Refuses the primary display outright. That is a hard rule, not a
    /// preference: a bad mode on the panel you are looking at can leave the
    /// machine unusable, and nothing in this project needs it.
    /// </summary>
    public (bool Ok, string Message) ChangeResolution(int id, int w, int h)
    {
        var d = ById(id);
        if (d == null) return (false, $"unknown display {id}");
        if (d.IsPrimary) return (false, "refusing to change the primary display");

        if (!d.Modes.Any(m => m.W == w && m.H == h))
            return (false, $"{w}x{h} is not an advertised mode for this display");

        var result = Win32Display.ChangeResolution(d.DeviceName, w, h);
        if (result.Ok) Refresh();
        return result;
    }
}
