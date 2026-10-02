using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HoloDisplays;

/// <summary>
/// Client for the Virtual Display Driver's control pipe.
///
/// This is how the app owns the number of virtual monitors instead of asking
/// the user to hand-edit vdd_settings.xml. The pipe is created by the driver
/// with the security descriptor D:(A;;GA;;;WD) — full access for Everyone — so
/// none of this needs elevation.
///
/// Commands were read out of the driver's Driver.cpp; they are not documented
/// anywhere. Messages are UTF-16 and the pipe is in message mode.
///
/// There is deliberately no resolution command here, because the driver has
/// none. The mode list lives in C:\VirtualDisplayDriver\vdd_settings.xml and
/// only changes on a driver reload.
/// </summary>
public static class VddPipe
{
    public const string PipeName = "MTTVirtualDisplayPipe";

    /// <summary>How long to wait for the driver to answer before giving up.</summary>
    private const int ConnectTimeoutMs = 2000;

    /// <summary>
    /// Sends one command. Returns the driver's reply, or null if it sent none.
    /// Throws if the pipe is not there — use <see cref="IsAvailableAsync"/> to
    /// test for the driver instead of catching this.
    /// </summary>
    public static async Task<string?> SendAsync(string command, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);

        await pipe.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
        pipe.ReadMode = PipeTransmissionMode.Message;

        var payload = Encoding.Unicode.GetBytes(command);
        await pipe.WriteAsync(payload, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);

        // Not every command replies. Give it a short window and move on.
        var buffer = new byte[512];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(750);
            int read = await pipe.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            return read > 0 ? Encoding.Unicode.GetString(buffer, 0, read).TrimEnd('\0') : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the driver is installed and running. This is the dependency
    /// check the app does at startup — the pipe only exists while the driver
    /// is live, so a failure here means "VDD is missing or disabled", not
    /// "something went wrong".
    /// </summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            await SendAsync("PING", ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets how many virtual monitors exist.
    ///
    /// The driver writes the new count into its XML and then reloads itself, so
    /// every virtual monitor disappears and comes back, and any resolution the
    /// user had set reverts to default. Callers must re-enumerate displays
    /// afterwards rather than trusting what they had.
    /// </summary>
    public static Task<string?> SetDisplayCountAsync(int count, CancellationToken ct = default)
    {
        if (count < 0 || count > 10)
            throw new ArgumentOutOfRangeException(nameof(count), "expected 0..10");
        return SendAsync($"SETDISPLAYCOUNT {count}", ct);
    }

    /// <summary>Reloads the driver, picking up hand edits to vdd_settings.xml.</summary>
    public static Task<string?> ReloadAsync(CancellationToken ct = default)
        => SendAsync("RELOAD_DRIVER", ct);

    /// <summary>Current driver settings, as a raw string.</summary>
    public static Task<string?> GetSettingsAsync(CancellationToken ct = default)
        => SendAsync("GETSETTINGS", ct);
}
