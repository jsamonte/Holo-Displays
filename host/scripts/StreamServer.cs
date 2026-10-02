using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace HoloDisplays;

/// <summary>
/// WebSocket server speaking the protocol in docs/PROTOCOL.md.
///
/// One client at a time. Frames go out as a JSON header text message followed
/// immediately by the JPEG as a binary message, which is what lets the lens
/// avoid slicing binary blobs.
/// </summary>
public sealed class StreamServer
{
    /// <summary>Default port. SPEC.md said 8765; this project uses 8880.</summary>
    public const int DefaultPort = 8880;

    /// <summary>Frames per second per tier.</summary>
    public double FullFps = 15.0;
    public double LowFps = 2.0;

    /// <summary>
    /// How long to wait for an ack before sending anyway. Without this a lens
    /// that stops acking would freeze instead of degrading to a low frame rate.
    /// </summary>
    public int AckTimeoutMs = 500;

    private readonly TcpServer _tcp = new();
    private WebSocketPeer? _peer;
    private readonly DisplayManager _displays;
    private readonly CaptureEngine _capture;

    public bool Listening { get; private set; }
    public int Port { get; private set; } = DefaultPort;
    public bool ClientConnected => _peer?.GetReadyState() == WebSocketPeer.State.Open;
    public string ClientName { get; private set; } = "";

    public event Action<string>? Log;

    public StreamServer(DisplayManager displays, CaptureEngine capture)
    {
        _displays = displays;
        _capture = capture;
        _displays.Changed += OnDisplaysChanged;
    }

    // ---- lifecycle ---------------------------------------------------------

    /// <summary>
    /// Starts listening, falling forward a few ports if the preferred one is
    /// taken.
    ///
    /// Binds 0.0.0.0 explicitly rather than "*". On Windows, "*" gives an
    /// IPv6-only socket (::), which accepts loopback tests over [::1] and then
    /// silently refuses the glasses, because Spectacles connect to the laptop's
    /// IPv4 LAN address.
    ///
    /// The fallback matters on this machine: Lens Studio holds 127.0.0.1:8880
    /// while it is open, which is most of the time during lens work. The port
    /// actually bound is what the UI displays, so whatever it lands on is what
    /// you type into the lens.
    /// </summary>
    public Error Start(int port)
    {
        Stop();

        Error last = Error.Failed;
        for (int candidate = port; candidate < port + 10; candidate++)
        {
            last = _tcp.Listen((ushort)candidate, "0.0.0.0");
            if (last == Error.Ok)
            {
                Port = candidate;
                Listening = true;
                if (candidate != port)
                    Log?.Invoke($"[color=#ffd43b]port {port} was busy (Lens Studio holds 8880 while open) — using {candidate}[/color]");
                Log?.Invoke($"listening on 0.0.0.0:{candidate}");
                return Error.Ok;
            }
        }

        Listening = false;
        Log?.Invoke($"[color=#ff6b6b]could not listen on {port}..{port + 9}: {last}[/color]");
        return last;
    }

    public void Stop()
    {
        DropClient("server stopping");
        if (_tcp.IsListening()) _tcp.Stop();
        Listening = false;
    }

    private void DropClient(string why)
    {
        if (_peer != null)
        {
            _peer.Close();
            _peer = null;
            ClientName = "";
            Log?.Invoke($"client gone: {why}");

            // Nothing is watching any more, so stop capturing entirely.
            foreach (var d in _displays.Displays) d.Tier = "off";
        }
    }

    // ---- main loop ---------------------------------------------------------

    public void Poll()
    {
        if (!Listening) return;

        AcceptPending();

        if (_peer == null) return;
        _peer.Poll();

        var state = _peer.GetReadyState();
        if (state == WebSocketPeer.State.Closed)
        {
            DropClient($"close code {_peer.GetCloseCode()}");
            return;
        }
        if (state != WebSocketPeer.State.Open) return;

        DrainIncoming();
        PumpFrames();
    }

    private void AcceptPending()
    {
        if (!_tcp.IsConnectionAvailable()) return;

        var stream = _tcp.TakeConnection();
        if (stream == null) return;

        // v1 is single client: a new connection replaces the old one.
        if (_peer != null) DropClient("replaced by a new connection");

        var peer = new WebSocketPeer();

        // These must be raised BEFORE AcceptStream. The 64 KB defaults are far
        // too small for JPEG frames and sends fail silently once a frame
        // exceeds them.
        peer.InboundBufferSize = 4 * 1024 * 1024;
        peer.OutboundBufferSize = 8 * 1024 * 1024;
        peer.MaxQueuedPackets = 256;

        var err = peer.AcceptStream(stream);
        if (err != Error.Ok)
        {
            Log?.Invoke($"accept failed: {err}");
            return;
        }

        _peer = peer;
        Log?.Invoke("client connecting");
    }

    private void DrainIncoming()
    {
        if (_peer == null) return;

        while (_peer.GetAvailablePacketCount() > 0)
        {
            var packet = _peer.GetPacket();
            if (!_peer.WasStringPacket()) continue;   // lens never sends binary

            var text = System.Text.Encoding.UTF8.GetString(packet);
            try
            {
                HandleMessage(text);
            }
            catch (Exception e)
            {
                Log?.Invoke($"bad message: {e.Message}");
            }
        }
    }

    private void HandleMessage(string text)
    {
        var node = JsonNode.Parse(text);
        if (node is not JsonObject msg) return;

        string t = msg["t"]?.GetValue<string>() ?? "";
        switch (t)
        {
            case "hello":
                ClientName = msg["client"]?.GetValue<string>() ?? "unknown";
                Log?.Invoke($"hello from {ClientName}");
                SendDisplays();
                break;

            case "visibility":
            {
                int id = msg["id"]?.GetValue<int>() ?? -1;
                string tier = msg["tier"]?.GetValue<string>() ?? "off";
                var d = _displays.ById(id);
                if (d == null) break;                      // monitor went away
                if (tier != "full" && tier != "low" && tier != "off") break;

                if (d.Tier != tier)
                {
                    d.Tier = tier;
                    // Coming back from off must produce a frame even if the
                    // desktop has not changed, or the panel sits stale.
                    if (tier != "off") d.ForceNextFrame = true;
                    Log?.Invoke($"display {id} -> {tier}");
                }
                break;
            }

            case "ack":
            {
                int id = msg["id"]?.GetValue<int>() ?? -1;
                uint seq = (uint)(msg["seq"]?.GetValue<int>() ?? 0);
                var d = _displays.ById(id);
                if (d != null && d.AwaitingAck == seq) d.AwaitingAck = 0;
                break;
            }

            case "resize":
            {
                int id = msg["id"]?.GetValue<int>() ?? -1;
                int w = msg["w"]?.GetValue<int>() ?? 0;
                int h = msg["h"]?.GetValue<int>() ?? 0;
                HandleResize(id, w, h);
                break;
            }

            default:
                // Unknown types are ignored on purpose; that is how the
                // protocol grows without breaking older clients.
                break;
        }
    }

    // ---- resolution --------------------------------------------------------

    private readonly HashSet<int> _resizing = new();

    private void HandleResize(int id, int w, int h)
    {
        // Debounce: ignore a second resize for a display still mid-change.
        if (!_resizing.Add(id)) return;

        try
        {
            Log?.Invoke($"resize {id} -> {w}x{h}");
            var (ok, message) = _displays.ChangeResolution(id, w, h);

            var reply = new JsonObject
            {
                ["t"] = "mode_changed",
                ["id"] = id,
                ["w"] = w,
                ["h"] = h,
                ["ok"] = ok,
            };
            if (!ok) reply["err"] = message;
            SendJson(reply);

            Log?.Invoke($"resize {id}: {message}");
            if (ok) SendDisplays();   // ChangeResolution already refreshed
        }
        finally
        {
            _resizing.Remove(id);
        }
    }

    private void OnDisplaysChanged()
    {
        if (ClientConnected) SendDisplays();
    }

    // ---- sending -----------------------------------------------------------

    public void SendDisplays()
    {
        if (!ClientConnected) return;

        var list = new JsonArray();
        foreach (var d in _displays.Streamed)
        {
            var modes = new JsonArray();
            foreach (var (w, h) in d.Modes) modes.Add(new JsonArray { w, h });

            list.Add(new JsonObject
            {
                ["id"] = d.Id,
                ["name"] = d.Name,
                ["w"] = d.Width,
                ["h"] = d.Height,
                ["modes"] = modes,
            });
        }

        SendJson(new JsonObject { ["t"] = "displays", ["list"] = list });
    }

    private void SendJson(JsonNode node)
    {
        if (_peer == null || !ClientConnected) return;
        var err = _peer.SendText(node.ToJsonString());
        if (err != Error.Ok) Log?.Invoke($"send failed: {err}");
    }

    /// <summary>
    /// Decides which displays are due a frame and sends them.
    ///
    /// The backpressure rule lives here: a display with an outstanding ack is
    /// skipped until the ack arrives or AckTimeoutMs passes. That is what keeps
    /// latency bounded on a slow link instead of letting frames queue up.
    /// </summary>
    private void PumpFrames()
    {
        if (!ClientConnected) return;
        ulong now = Time.GetTicksMsec();

        foreach (var d in _displays.Streamed)
        {
            if (d.Tier == "off") continue;

            double fps = d.Tier == "full" ? FullFps : LowFps;
            if (fps <= 0) continue;
            double intervalMs = 1000.0 / fps;

            if (now - d.LastSentMsec < intervalMs) continue;

            if (d.AwaitingAck != 0 && now - d.FrameSentMsec < (ulong)AckTimeoutMs)
                continue;   // one frame in flight per display

            var frame = _capture.Capture(d);
            if (frame == null) continue;

            d.LastSentMsec = now;
            d.LastWasUnchanged = frame.Unchanged;

            if (frame.Unchanged) continue;   // nothing to send, costs no bandwidth

            d.Seq++;
            var header = new JsonObject
            {
                ["t"] = "frame",
                ["id"] = d.Id,
                ["seq"] = (int)d.Seq,
                ["w"] = frame.Width,
                ["h"] = frame.Height,
            };

            SendJson(header);
            var err = _peer!.Send(frame.Jpeg, WebSocketPeer.WriteMode.Binary);
            if (err != Error.Ok)
            {
                Log?.Invoke($"frame send failed for {d.Id}: {err}");
                continue;
            }

            d.AwaitingAck = d.Seq;
            d.FrameSentMsec = now;
            d.LastBytes = frame.Jpeg.Length;

            // Measured rate, not the target. The target is already known; what
            // the UI needs to show is whether the link is keeping up with it.
            if (d.LastDeliveredMsec != 0)
            {
                double gapMs = now - d.LastDeliveredMsec;
                if (gapMs > 0)
                {
                    double instantFps = 1000.0 / gapMs;
                    double instantKb = frame.Jpeg.Length / 1024.0 * instantFps;
                    d.Fps = d.Fps <= 0 ? instantFps : d.Fps * 0.7 + instantFps * 0.3;
                    d.KbPerSec = d.KbPerSec <= 0 ? instantKb : d.KbPerSec * 0.7 + instantKb * 0.3;
                }
            }
            d.LastDeliveredMsec = now;
        }
    }
}
