using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Godot;

namespace HoloDisplays;

/// <summary>
/// Host control panel.
///
/// Shows the LAN address to type into the lens, a row per display with live
/// capture/encode timings, and the virtual monitor count — which this app owns
/// via the driver's control pipe rather than asking anyone to edit XML.
/// </summary>
public partial class Main : Control
{
    private DisplayManager _displays = null!;
    private CaptureEngine _capture = null!;
    private StreamServer _server = null!;

    private Label _address = null!;
    private Label _status = null!;
    private Label _vddStatus = null!;
    private RichTextLabel _log = null!;
    private VBoxContainer _rows = null!;
    private TextureRect _preview = null!;
    private OptionButton _previewPick = null!;
    private SpinBox _monitorCount = null!;
    private Button _applyCount = null!;
    private Button _installVdd = null!;
    private Button _uninstallVdd = null!;
    private ConfirmationDialog _consent = null!;
    private HSlider _quality = null!;
    private Label _qualityLabel = null!;
    private SpinBox _fullFps = null!;
    private SpinBox _maxEdge = null!;

    private readonly ImageTexture _previewTexture = new();
    private double _previewAccum;
    private double _statsAccum;
    private bool _vddPresent;

    public override void _Ready()
    {
        _address = GetNode<Label>("%Address");
        _status = GetNode<Label>("%Status");
        _vddStatus = GetNode<Label>("%VddStatus");
        _log = GetNode<RichTextLabel>("%Log");
        _rows = GetNode<VBoxContainer>("%Rows");
        _preview = GetNode<TextureRect>("%Preview");
        _previewPick = GetNode<OptionButton>("%PreviewPick");
        _monitorCount = GetNode<SpinBox>("%MonitorCount");
        _applyCount = GetNode<Button>("%ApplyCount");
        _installVdd = GetNode<Button>("%InstallVdd");
        _uninstallVdd = GetNode<Button>("%UninstallVdd");
        _consent = GetNode<ConfirmationDialog>("%Consent");
        _quality = GetNode<HSlider>("%Quality");
        _qualityLabel = GetNode<Label>("%QualityLabel");
        _fullFps = GetNode<SpinBox>("%FullFps");
        _maxEdge = GetNode<SpinBox>("%MaxEdge");

        _displays = new DisplayManager();
        _capture = new CaptureEngine();
        _server = new StreamServer(_displays, _capture);
        _server.Log += Log;

        _displays.Changed += RebuildRows;

        _quality.ValueChanged += v => {
            _capture.Quality = (float)v;
            _qualityLabel.Text = $"{v:0.00}";
        };
        _fullFps.ValueChanged += v => _server.FullFps = v;
        _maxEdge.ValueChanged += v => _capture.MaxLongEdge = (int)v;
        _applyCount.Pressed += OnApplyCount;
        _installVdd.Pressed += OnInstallVddPressed;
        _uninstallVdd.Pressed += OnUninstallVddPressed;
        _consent.Confirmed += OnInstallConfirmed;
        _previewPick.ItemSelected += _ => { };

        Log($"Godot {Engine.GetVersionInfo()["string"]}  {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

        _displays.Refresh();

        // Start first: the port may fall forward if the preferred one is taken,
        // and the address shown has to be the one that actually works.
        _server.Start(StreamServer.DefaultPort);
        _address.Text = $"{LanAddress()}:{_server.Port}";

        _ = CheckVdd();

        if (OS.GetCmdlineUserArgs().Contains("--bench")) RunBenchmark();
    }

    /// <summary>
    /// Times raw capture and encode, bypassing the unchanged-frame check.
    ///
    /// Needed because the dedup makes an idle desktop look infinitely fast —
    /// nothing is encoded, so the frame rate says nothing about whether Phase 1
    /// is quick enough. Run with:  Godot --path host -- --bench
    /// </summary>
    private void RunBenchmark()
    {
        Log("");
        Log("[b]benchmark: 20 capture+encode passes per display[/b]");

        foreach (var d in _displays.Displays)
        {
            if (!d.Capturable) continue;

            double capTotal = 0, encTotal = 0, hashTotal = 0, bytesTotal = 0;
            int n = 0;

            for (int i = 0; i < 20; i++)
            {
                d.ForceNextFrame = true;          // defeat the dedup
                var f = _capture.Capture(d);
                if (f == null || f.Unchanged) continue;
                capTotal += f.CaptureMs;
                encTotal += f.EncodeMs;
                hashTotal += f.HashMs;
                bytesTotal += f.Jpeg.Length;
                n++;
            }

            if (n == 0) { Log($"  {d.Name}: no frames captured"); continue; }

            double cap = capTotal / n, enc = encTotal / n, hash = hashTotal / n;
            double ceiling = 1000.0 / (cap + enc + hash);
            Log($"  {d.Name} {d.Width}x{d.Height}");
            Log($"    capture {cap,6:0.0} ms   hash {hash,5:0.0} ms   encode {enc,6:0.0} ms   " +
                $"{bytesTotal / n / 1024,5:0} KB/frame");
            Log($"    ceiling {ceiling,6:0.0} fps on this thread" +
                (ceiling < 15 ? "  [color=#ffd43b](under the 15 fps target — Phase 2 territory)[/color]" : ""));
        }
        Log("");
    }

    public override void _Process(double delta)
    {
        _server.Poll();

        _previewAccum += delta;
        if (_previewAccum >= 1.0 / 10.0)
        {
            _previewAccum = 0;
            UpdatePreview();
        }

        _statsAccum += delta;
        if (_statsAccum >= 0.5)
        {
            _statsAccum = 0;
            UpdateStatus();
            UpdateRowStats();
        }
    }

    // ---- VDD ---------------------------------------------------------------

    /// <summary>
    /// VDD is a driver and cannot ship inside a Godot project, so the app
    /// detects it and offers to set it up. A missing pipe means the driver is
    /// absent or disabled — not that anything crashed.
    /// </summary>
    private async Task CheckVdd()
    {
        var status = await VddInstaller.CheckAsync();
        _vddPresent = status.State == VddInstaller.Readiness.Running;

        _vddStatus.Text = status.Message;
        _vddStatus.Modulate = status.State switch
        {
            VddInstaller.Readiness.Running => new Color("#51cf66"),
            VddInstaller.Readiness.BlockedByArm64Signing => new Color("#ff6b6b"),
            _ => new Color("#ffd43b"),
        };

        _monitorCount.Editable = _vddPresent;
        _applyCount.Disabled = !_vddPresent;

        // Only offer the install when it could actually succeed. Offering a
        // button that cannot work is worse than saying why.
        _installVdd.Visible = status.CanOfferInstall;
        _uninstallVdd.Visible = _vddPresent;

        if (status.Detail.Length > 0) Log($"[color=#ffd43b]{status.Detail}[/color]");
        if (_vddPresent) Log("VDD control pipe responded to PING");
    }

    /// <summary>
    /// Shows exactly what the install will do, and only acts if the user
    /// confirms. Nothing about driver installation happens on its own.
    /// </summary>
    private void OnInstallVddPressed()
    {
        _consent.DialogText = VddInstaller.ConsentText(VddInstaller.WouldNeedDownload());
        _consent.PopupCentered();
    }

    private async void OnInstallConfirmed()
    {
        _installVdd.Disabled = true;
        try
        {
            Log("[b]installing the Virtual Display Driver[/b]");

            var source = await VddInstaller.AcquireAsync(Log);
            if (source == null)
            {
                Log("[color=#ff6b6b]could not obtain the driver; nothing was installed[/color]");
                return;
            }

            var (ok, message) = await VddInstaller.InstallAsync(source.Directory, Log);
            if (!ok)
            {
                Log($"[color=#ff6b6b]{message}[/color]");
                return;
            }

            // The driver takes a moment to start and publish its pipe.
            await Task.Delay(3000);
            _displays.Refresh();
            await CheckVdd();
        }
        finally
        {
            _installVdd.Disabled = false;
        }
    }

    private async void OnUninstallVddPressed()
    {
        _uninstallVdd.Disabled = true;
        try
        {
            await VddInstaller.UninstallAsync(Log);
            await Task.Delay(2000);
            _displays.Refresh();
            await CheckVdd();
        }
        finally
        {
            _uninstallVdd.Disabled = false;
        }
    }

    private async void OnApplyCount()
    {
        int want = (int)_monitorCount.Value;
        _applyCount.Disabled = true;
        Log($"SETDISPLAYCOUNT {want} ...");

        try
        {
            await VddPipe.SetDisplayCountAsync(want);

            // The driver reloads itself, so monitors blink out and back. Wait
            // for them to settle before re-enumerating, or we read the gap.
            await Task.Delay(2500);

            _displays.Refresh();
            Log($"display count now {want}; re-enumerated");
        }
        catch (Exception e)
        {
            Log($"[color=#ff6b6b]SETDISPLAYCOUNT failed: {e.Message}[/color]");
        }
        finally
        {
            _applyCount.Disabled = !_vddPresent;
        }
    }

    // ---- UI ----------------------------------------------------------------

    private void RebuildRows()
    {
        foreach (var child in _rows.GetChildren()) child.QueueFree();

        int previewSelection = _previewPick.Selected;
        _previewPick.Clear();

        foreach (var d in _displays.Displays)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);

            // The primary is never streamed by default, but it can be ticked.
            // Capturing it is harmless (it is just a screenshot) and it is the
            // only way to exercise the frame path on a machine with no virtual
            // monitors yet. Changing its resolution stays blocked outright in
            // DisplayManager.ChangeResolution — that is the rule that matters.
            var tick = new CheckBox { ButtonPressed = d.Stream, Disabled = !d.Capturable };
            var captured = d;
            tick.Toggled += on => { captured.Stream = on; _server.SendDisplays(); };
            row.AddChild(tick);

            var tag = d.IsPrimary ? " [PRIMARY]" : d.IsVirtual ? " [virtual]" : "";
            var name = new Label
            {
                Text = $"{d.Id}  {d.Name}{tag}",
                CustomMinimumSize = new Vector2(300, 0),
            };
            if (d.IsVirtual) name.Modulate = new Color("#4dabf7");
            row.AddChild(name);

            row.AddChild(new Label
            {
                Text = $"{d.Width}x{d.Height}",
                CustomMinimumSize = new Vector2(110, 0),
            });

            var stats = new Label { Name = "Stats", CustomMinimumSize = new Vector2(330, 0) };
            row.AddChild(stats);

            _rows.AddChild(row);
            _previewPick.AddItem($"{d.Id}  {d.Name}", d.Id);
        }

        if (previewSelection >= 0 && previewSelection < _previewPick.ItemCount)
            _previewPick.Selected = previewSelection;
        else if (_previewPick.ItemCount > 0)
        {
            // Prefer a virtual monitor for the preview, else the first.
            var v = _displays.Displays.FindIndex(d => d.IsVirtual);
            _previewPick.Selected = v >= 0 ? v : 0;
        }

        _monitorCount.Value = _displays.Displays.Count(d => d.IsVirtual);
    }

    private void UpdateRowStats()
    {
        var rows = _rows.GetChildren();
        for (int i = 0; i < rows.Count && i < _displays.Displays.Count; i++)
        {
            if (rows[i] is not HBoxContainer row) continue;
            if (row.GetNodeOrNull<Label>("Stats") is not Label label) continue;

            var d = _displays.Displays[i];
            if (!d.Stream || !_server.ClientConnected)
            {
                label.Text = d.Capturable ? "idle" : "not capturable";
                label.Modulate = new Color("#868e96");
                continue;
            }

            // "static" rather than a misleading 0 fps: an unchanged desktop is
            // deliberately costing no bandwidth, which is the system working.
            string rate = d.LastWasUnchanged
                ? "  static      "
                : $"{d.Fps,5:0.0} fps {d.KbPerSec,6:0} KB/s";

            label.Text = $"{d.Tier,-5} {rate}  cap {d.CaptureMs,5:0.0} ms  enc {d.EncodeMs,5:0.0} ms";
            label.Modulate = d.Tier switch
            {
                "full" => new Color("#51cf66"),
                "low" => new Color("#ffd43b"),
                _ => new Color("#868e96"),
            };
        }
    }

    private void UpdatePreview()
    {
        if (_previewPick.ItemCount == 0) return;
        int id = _previewPick.GetItemId(_previewPick.Selected);
        var d = _displays.ById(id);
        if (d == null || !d.Capturable) return;

        Image image;
        try { image = DisplayServer.ScreenGetImage(d.ScreenIndex); }
        catch { return; }
        if (image == null || image.IsEmpty()) return;

        // Keep the preview small; this runs every frame and is only for eyeballing.
        int longEdge = Math.Max(image.GetWidth(), image.GetHeight());
        if (longEdge > 640)
        {
            double s = 640.0 / longEdge;
            image.Resize((int)(image.GetWidth() * s), (int)(image.GetHeight() * s), Image.Interpolation.Bilinear);
        }

        if (_previewTexture.GetImage() == null || _previewTexture.GetSize() != image.GetSize())
            _previewTexture.SetImage(image);
        else
            _previewTexture.Update(image);

        _preview.Texture = _previewTexture;
    }

    private void UpdateStatus()
    {
        if (!_server.Listening)
        {
            _status.Text = "server: not listening";
            _status.Modulate = new Color("#ff6b6b");
        }
        else if (_server.ClientConnected)
        {
            _status.Text = $"client connected: {_server.ClientName}";
            _status.Modulate = new Color("#51cf66");
        }
        else
        {
            _status.Text = "waiting for a client";
            _status.Modulate = new Color("#ffd43b");
        }
    }

    private void Log(string text)
    {
        _log.AppendText(text + "\n");
        GD.Print(System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-z]+(=[^\]]*)?\]", ""));
    }

    /// <summary>Best guess at the LAN address to type into the lens.</summary>
    private static string LanAddress()
    {
        try
        {
            // Connecting a UDP socket picks the interface Windows would route
            // over, without sending anything.
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            if (s.LocalEndPoint is IPEndPoint ep) return ep.Address.ToString();
        }
        catch { }

        foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString();

        return "127.0.0.1";
    }

    public override void _ExitTree() => _server.Stop();
}
