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
        // Hash a downsampled copy rather than the full image: a 64x64 thumbnail
        // is ~4k pixels instead of ~2M, and still catches any change big enough
        // to be worth a frame.
        ulong hash = CheapHash(image);
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
    /// Cheap perceptual-ish hash of a 64x64 copy. Not cryptographic and does not
    /// need to be — a collision costs one skipped frame, and the next change
    /// will differ somewhere.
    /// </summary>
    private static ulong CheapHash(Image source)
    {
        var small = Image.CreateEmpty(64, 64, false, source.GetFormat());
        small.CopyFrom(source);
        small.Resize(64, 64, Image.Interpolation.Nearest);

        byte[] data = small.GetData();
        ulong hash = 1469598103934665603UL; // FNV-1a offset basis
        for (int i = 0; i < data.Length; i++)
        {
            hash ^= data[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
