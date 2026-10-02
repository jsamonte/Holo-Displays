using System;
using Godot;

namespace HoloDisplays;

/// <summary>
/// Phase 1 capture: DisplayServer.ScreenGetImage, optional downscale, JPEG.
///
/// Simple on purpose. Every call is timed and the numbers are shown per display
/// in the UI, so when this turns out to be too slow it is obvious where the
/// time goes and Phase 2 (DXGI Desktop Duplication) can replace just the
/// capture half.
/// </summary>
public sealed class CaptureEngine
{
    /// <summary>Downscale anything whose long edge exceeds this. 0 disables.</summary>
    public int MaxLongEdge = 1920;

    /// <summary>JPEG quality, 0..1.</summary>
    public float Quality = 0.7f;

    public sealed class Frame
    {
        public byte[] Jpeg = Array.Empty<byte>();
        public int Width;
        public int Height;
        public double CaptureMs;
        public double EncodeMs;

        /// <summary>
        /// Time spent on the unchanged-frame hash. Measured because it sits
        /// between the other two timers and would otherwise be invisible.
        /// </summary>
        public double HashMs;

        /// <summary>True when the picture matched the last one sent and nothing was encoded.</summary>
        public bool Unchanged;
    }

    /// <summary>
    /// Captures and encodes one display.
    ///
    /// Returns null if the screen could not be read at all, which happens
    /// transiently while the VDD driver reloads and its monitors blink out.
    /// </summary>
    public Frame? Capture(TargetDisplay d)
    {
        if (!d.Capturable) return null;

        var frame = new Frame();

        // ---- capture ----
        ulong t0 = Time.GetTicksUsec();
        Image image;
        try
        {
            image = DisplayServer.ScreenGetImage(d.ScreenIndex);
        }
        catch (Exception e)
        {
            GD.PushWarning($"capture failed for {d.Name}: {e.Message}");
            return null;
        }
        if (image == null || image.IsEmpty()) return null;
        frame.CaptureMs = (Time.GetTicksUsec() - t0) / 1000.0;

        // ---- dedup ----
        ulong tHash = Time.GetTicksUsec();
        ulong hash = CheapHash(image);
        frame.HashMs = (Time.GetTicksUsec() - tHash) / 1000.0;
        if (!d.ForceNextFrame && hash == d.LastHash)
        {
            frame.Unchanged = true;
            return frame;
        }
        d.LastHash = hash;
        d.ForceNextFrame = false;

        // ---- downscale ----
        int w = image.GetWidth(), h = image.GetHeight();
        if (MaxLongEdge > 0 && Math.Max(w, h) > MaxLongEdge)
        {
            double scale = (double)MaxLongEdge / Math.Max(w, h);
            int nw = Math.Max(1, (int)Math.Round(w * scale));
            int nh = Math.Max(1, (int)Math.Round(h * scale));
            image.Resize(nw, nh, Image.Interpolation.Bilinear);
        }

        // ---- encode ----
        ulong t1 = Time.GetTicksUsec();
        frame.Jpeg = image.SaveJpgToBuffer(Quality);
        frame.EncodeMs = (Time.GetTicksUsec() - t1) / 1000.0;

        frame.Width = image.GetWidth();
        frame.Height = image.GetHeight();

        d.CaptureMs = frame.CaptureMs;
        d.EncodeMs = frame.EncodeMs;
        return frame;
    }

    /// <summary>
    /// Cheap hash of the frame, used only to notice "nothing changed".
    ///
    /// Samples the raw buffer at a stride rather than resizing a copy. The
    /// earlier version did CopyFrom + Resize, which duplicated the entire
    /// image every frame — about 10 MB of copying per 1920x1280 capture, for a
    /// number thrown away immediately. Worse, it sat between the capture and
    /// encode timers, so none of that cost showed up in the measurements.
    ///
    /// Not cryptographic and does not need to be: a collision costs one skipped
    /// frame, and the next change will land on a different byte.
    /// </summary>
    private static ulong CheapHash(Image source)
    {
        byte[] data = source.GetData();
        if (data.Length == 0) return 0;

        // ~16k samples regardless of resolution, so the cost does not grow with
        // the monitor. Prime stride so it does not align with row boundaries and
        // miss changes confined to one column.
        const int targetSamples = 16384;
        int stride = Math.Max(1, data.Length / targetSamples);
        if (stride % 2 == 0) stride++;

        ulong hash = 1469598103934665603UL; // FNV-1a offset basis
        for (int i = 0; i < data.Length; i += stride)
        {
            hash ^= data[i];
            hash *= 1099511628211UL;
        }

        // Mix the length in, so a resolution change can never hash equal.
        hash ^= (ulong)data.Length;
        hash *= 1099511628211UL;
        return hash;
    }
}
