using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;

namespace HoloDisplays;

/// <summary>
/// Locates and installs the Virtual Display Driver, as a visible, user-driven
/// step.
///
/// The driver is the one dependency that cannot live inside a Godot project, so
/// the app offers to set it up rather than sending the user away to do it. That
/// is a convenience, not a licence to be sneaky, and the design reflects it:
///
///   - Nothing here ever runs on its own. Every entry point is reached from a
///     button the user pressed, after a panel telling them exactly what will
///     happen, where the driver comes from, and that it needs administrator
///     rights.
///   - The elevated step runs in a VISIBLE console window. An app that installs
///     a display driver behind a hidden prompt is shaped exactly like malware,
///     and both the user and their antivirus deserve to see the work.
///   - Every step is logged to the app's own log pane as it happens.
///   - <see cref="UninstallAsync"/> exists so the decision is reversible.
///
/// The driver is MIT-licensed from
/// https://github.com/VirtualDrivers/Virtual-Display-Driver and is not vendored
/// into this repository.
/// </summary>
public static class VddInstaller
{
    public const string HardwareId = @"Root\MttVDD";
    public const string ProjectUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver";

    /// <summary>Display class GUID, taken from the driver's own INF.</summary>
    private const string DisplayClassGuid = "4D36E968-E325-11CE-BFC1-08002BE10318";

    public enum Readiness
    {
        /// <summary>Installed, and its control pipe is answering.</summary>
        Running,

        /// <summary>Not installed. The install button is worth offering.</summary>
        Missing,

        /// <summary>
        /// This machine will reject the driver however it is installed. Windows
        /// on ARM64 only accepts WHQL/Store-signed drivers and this one carries
        /// a commercial code-signing certificate.
        /// </summary>
        BlockedByArm64Signing,
    }

    public sealed class Status
    {
        public Readiness State;
        public string Message = "";
        public string Detail = "";
        public bool CanOfferInstall => State == Readiness.Missing;
    }

    // ---- detection ---------------------------------------------------------

    public static async Task<Status> CheckAsync()
    {
        if (await VddPipe.IsAvailableAsync().ConfigureAwait(false))
        {
            return new Status
            {
                State = Readiness.Running,
                Message = "VDD: running",
            };
        }

        if (RuntimeInformation.OSArchitecture == Architecture.Arm64 && SecureBootLikelyOn())
        {
            return new Status
            {
                State = Readiness.BlockedByArm64Signing,
                Message = "VDD: blocked on ARM64",
                Detail =
                    "Windows on ARM64 only accepts drivers signed with a Windows/WHQL/Store " +
                    "certificate. This driver has a commercial one, so Windows refuses it " +
                    "(error 0x800B0109). Installing it here would mean turning off Secure Boot. " +
                    "See docs/SETUP.md.",
            };
        }

        return new Status
        {
            State = Readiness.Missing,
            Message = "VDD: not installed",
            Detail = "No virtual monitors exist yet. Use Install driver to set it up.",
        };
    }

    /// <summary>
    /// Best-effort Secure Boot read, from the state Windows publishes in the
    /// registry. Errs towards "on": telling an ARM64 user Secure Boot is off
    /// when it is not would send them into an install that cannot succeed.
    /// </summary>
    private static bool SecureBootLikelyOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key?.GetValue("UEFISecureBootEnabled") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    // ---- what the user is agreeing to --------------------------------------

    /// <summary>
    /// The text shown before anything happens. Kept here next to the code it
    /// describes so the two cannot drift apart.
    /// </summary>
    public static string ConsentText(bool willDownload)
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : "x64";
        string source = willDownload
            ? $"• Download the {arch} driver from {ProjectUrl}\n"
            : $"• Use the {arch} driver already present on this machine\n";

        return
            "Install the Virtual Display Driver?\n\n" +
            "This adds virtual monitors to Windows, which is what the glasses show.\n\n" +
            "It will:\n" +
            source +
            "• Ask for administrator rights (you will see a Windows UAC prompt)\n" +
            "• Open a visible console window and install the driver there\n" +
            "• Add a display device named \"Virtual Display Driver\"\n\n" +
            "The driver is open source (MIT). You can remove it later with Uninstall driver, " +
            "or from Device Manager.";
    }

    // ---- locating the driver ----------------------------------------------

    public sealed class DriverSource
    {
        public string Directory = "";
        public bool WasDownloaded;
        public string Description = "";
    }

    /// <summary>True when the driver will have to be fetched from the internet.</summary>
    public static bool WouldNeedDownload() => FindLocal() == null;

    /// <summary>
    /// A directory holding MttVDD.inf/.dll/.cat for this architecture, or null.
    /// Prefers anything already on disk so a repeat install needs no network.
    /// </summary>
    private static string? FindLocal()
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        // 1. Vendored alongside the app, if this build ships it.
        string beside = Path.Combine(AppContext.BaseDirectory, "vdd", arch);
        if (File.Exists(Path.Combine(beside, "MttVDD.inf"))) return beside;

        // 2. A winget install of VDD, if the user already had one. Note winget
        //    names the x64 folder "x86", which is wrong but is what ships.
        string folder = arch == "arm64" ? "ARM64" : "x86";
        string root = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");

        if (!Directory.Exists(root)) return null;

        foreach (var dir in Directory.GetDirectories(root, "VirtualDrivers.Virtual-Display-Driver*"))
        {
            string candidate = Path.Combine(dir, "SignedDrivers", folder, "VDD");
            if (File.Exists(Path.Combine(candidate, "MttVDD.inf"))) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Resolves where the driver will come from, downloading only if it is not
    /// already on disk. The caller has already shown the user which of these
    /// two it will be.
    /// </summary>
    public static async Task<DriverSource?> AcquireAsync(Action<string> log)
    {
        var local = FindLocal();
        if (local != null)
        {
            log($"using the driver already on this machine: {local}");
            return new DriverSource {Directory = local, Description = local};
        }

        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string asset = arch == "arm64" ? "VirtualDisplayDriver-ARM64.zip" : "VirtualDisplayDriver-x64.zip";

        log($"fetching {asset} from {ProjectUrl}/releases");

        try
        {
            using var http = new System.Net.Http.HttpClient {Timeout = TimeSpan.FromMinutes(3)};
            http.DefaultRequestHeaders.Add("User-Agent", "Holo-Displays");

            var json = await http
                .GetStringAsync("https://api.github.com/repos/VirtualDrivers/Virtual-Display-Driver/releases")
                .ConfigureAwait(false);

            string? url = FindAssetUrl(json, asset);
            if (url == null)
            {
                log("no matching driver asset in the releases list");
                return null;
            }

            log($"downloading {url}");
            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);

            string work = Path.Combine(Path.GetTempPath(), "HoloDisplays-vdd");
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, asset);
            await File.WriteAllBytesAsync(zip, bytes).ConfigureAwait(false);
            log($"downloaded {bytes.Length / 1024} KB to {zip}");

            string extracted = Path.Combine(work, arch);
            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, extracted);

            var inf = Directory.GetFiles(extracted, "MttVDD.inf", SearchOption.AllDirectories).FirstOrDefault();
            if (inf == null)
            {
                log("the archive contained no MttVDD.inf");
                return null;
            }

            string dir = Path.GetDirectoryName(inf)!;
            log($"extracted to {dir}");
            return new DriverSource {Directory = dir, WasDownloaded = true, Description = url};
        }
        catch (Exception e)
        {
            log($"download failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pulls one browser_download_url out of the releases JSON by asset name.
    /// Hand-rolled because the payload is large and only this field matters.
    /// </summary>
    private static string? FindAssetUrl(string json, string assetName)
    {
        int at = json.IndexOf($"\"name\":\"{assetName}\"", StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = json.IndexOf($"\"name\": \"{assetName}\"", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        const string marker = "\"browser_download_url\"";
        int urlAt = json.IndexOf(marker, at, StringComparison.Ordinal);
        if (urlAt < 0) return null;

        int start = json.IndexOf('"', urlAt + marker.Length + 1) + 1;
        int end = json.IndexOf('"', start);
        return end > start ? json[start..end] : null;
    }

    // ---- installing --------------------------------------------------------

    /// <summary>
    /// Creates the root device node and binds the INF, in an elevated and
    /// VISIBLE console window.
    ///
    /// A child process rather than in-process, because the app itself should
    /// never run as administrator — it captures screens and listens on a
    /// socket, and neither of those wants those rights.
    ///
    /// SetupAPI rather than devcon: devcon is not part of Windows, and the copy
    /// VDD ships is x86_64, which misreports the architecture under emulation
    /// and installs the wrong driver.
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(string driverDir, Action<string> log)
    {
        string inf = Path.Combine(driverDir, "MttVDD.inf");
        if (!File.Exists(inf)) return (false, $"no MttVDD.inf in {driverDir}");

        string work = Path.Combine(Path.GetTempPath(), "HoloDisplays-vdd");
        Directory.CreateDirectory(work);
        string script = Path.Combine(work, "install-vdd.ps1");
        string resultFile = Path.Combine(work, "install-result.txt");
        if (File.Exists(resultFile)) File.Delete(resultFile);

        await File.WriteAllTextAsync(script, BuildInstallScript(inf, resultFile)).ConfigureAwait(false);

        log($"wrote the install script to {script} — you can read it before approving");
        log("Windows will now ask for administrator rights…");

        var (ok, message) = await RunElevatedAsync(script, resultFile).ConfigureAwait(false);
        log(ok ? $"install: {message}" : $"install failed: {message}");
        return (ok, message);
    }

    /// <summary>Removes the device node, so the install is reversible.</summary>
    public static async Task<(bool Ok, string Message)> UninstallAsync(Action<string> log)
    {
        string work = Path.Combine(Path.GetTempPath(), "HoloDisplays-vdd");
        Directory.CreateDirectory(work);
        string script = Path.Combine(work, "uninstall-vdd.ps1");
        string resultFile = Path.Combine(work, "uninstall-result.txt");
        if (File.Exists(resultFile)) File.Delete(resultFile);

        await File.WriteAllTextAsync(script,
$$"""
# Removes the Virtual Display Driver's device nodes. Run by Holo-Displays at
# the user's request. Safe to read before approving.
Write-Host 'Removing Virtual Display Driver device nodes...'
$removed = 0
Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match 'MttVDD' } |
    ForEach-Object {
        Write-Host ('  removing ' + $_.InstanceId)
        & pnputil.exe /remove-device $_.InstanceId | Out-Host
        $removed++
    }
Set-Content -Path '{{resultFile}}' -Value "OK removed $removed device(s)" -Encoding utf8
Write-Host "Done. Removed $removed device(s)."
Start-Sleep -Seconds 2
""").ConfigureAwait(false);

        log("Windows will now ask for administrator rights…");
        var (ok, message) = await RunElevatedAsync(script, resultFile).ConfigureAwait(false);
        log(ok ? $"uninstall: {message}" : $"uninstall failed: {message}");
        return (ok, message);
    }

    /// <summary>
    /// Runs a script elevated, in a window the user can watch.
    ///
    /// CreateNoWindow is false and the window style is Normal deliberately. The
    /// user approved a driver install; they should be able to see it happen and
    /// read any error for themselves.
    /// </summary>
    private static async Task<(bool Ok, string Message)> RunElevatedAsync(string script, string resultFile)
    {
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
        };

        try
        {
            using var process = Process.Start(info);
            if (process == null) return (false, "could not start the installer");
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "administrator rights were declined");
        }

        if (!File.Exists(resultFile)) return (false, "the script produced no result");

        string result = (await File.ReadAllTextAsync(resultFile).ConfigureAwait(false)).Trim();
        return (result.StartsWith("OK", StringComparison.Ordinal), result);
    }

    private static string BuildInstallScript(string infPath, string resultFile) =>
$$"""
# ---------------------------------------------------------------------------
# Installs the Virtual Display Driver for Holo-Displays.
#
# Written by the Holo-Displays host app at your request. It is plain text on
# purpose: read it before approving if you like.
#
# What it does:
#   1. Stages the driver package with pnputil (a Windows tool).
#   2. Creates a Root\MttVDD device node and binds the driver to it.
#
# To undo: Device Manager -> Display adapters -> Virtual Display Driver ->
# Uninstall device, or the Uninstall driver button in the app.
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
$inf = '{{infPath}}'
$result = '{{resultFile}}'

function Finish($text) {
    Set-Content -Path $result -Value $text -Encoding utf8
    Write-Host ''
    Write-Host $text
    Write-Host 'This window closes in 5 seconds.'
    Start-Sleep -Seconds 5
    exit
}

Write-Host 'Holo-Displays: installing the Virtual Display Driver'
Write-Host "  architecture: $env:PROCESSOR_ARCHITECTURE"
Write-Host "  driver: $inf"
Write-Host ''

try {
    Write-Host 'Staging the driver package...'
    $add = & pnputil.exe /add-driver "$inf" /install 2>&1 | Out-String
    Write-Host $add

    if ($LASTEXITCODE -ne 0) {
        if ($add -match '0x800B0109|not trusted') {
            Finish 'FAIL Windows rejected the signature. On ARM64 only WHQL/Store-signed drivers are accepted, and this driver is signed with a commercial certificate. Installing it here needs Secure Boot turned off. See docs/SETUP.md.'
        }
        Finish "FAIL pnputil returned $LASTEXITCODE"
    }

    if (Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'MttVDD' }) {
        Finish 'OK the driver bound to a device node that already existed'
    }

    Write-Host 'Creating the virtual display device...'

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class VddNode {
    const int DICD_GENERATE_ID = 1, SPDRP_HARDWAREID = 1, DIF_REGISTERDEVICE = 0x19, INSTALLFLAG_FORCE = 1;
    [StructLayout(LayoutKind.Sequential)]
    struct DD { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g, IntPtr h);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr s, string n, ref Guid g, string d, IntPtr h, int f, ref DD dd);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s, ref DD dd, int p, byte[] b, int l);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiCallClassInstaller(int f, IntPtr s, ref DD dd);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("newdev.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr h, string hw, string inf, int f, out bool reboot);
    public static string Run(Guid cls, string hwid, string inf) {
        IntPtr set = SetupDiCreateDeviceInfoList(ref cls, IntPtr.Zero);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return "FAIL create-list " + Marshal.GetLastWin32Error();
        try {
            var d = new DD(); d.cbSize = Marshal.SizeOf(typeof(DD));
            if (!SetupDiCreateDeviceInfoW(set, "Display", ref cls, null, IntPtr.Zero, DICD_GENERATE_ID, ref d))
                return "FAIL create-devinfo " + Marshal.GetLastWin32Error();
            byte[] buf = System.Text.Encoding.Unicode.GetBytes(hwid + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref d, SPDRP_HARDWAREID, buf, buf.Length))
                return "FAIL set-hwid " + Marshal.GetLastWin32Error();
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref d))
                return "FAIL register " + Marshal.GetLastWin32Error();
        } finally { SetupDiDestroyDeviceInfoList(set); }
        bool reboot;
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hwid, inf, INSTALLFLAG_FORCE, out reboot))
            return "FAIL bind " + Marshal.GetLastWin32Error();
        return "OK";
    }
}
'@

    $r = [VddNode]::Run([Guid]'{{DisplayClassGuid}}', '{{HardwareId}}', $inf)

    if ($r -like 'OK*') { Finish 'OK driver installed' }
    if ($r -like '*265*') {
        Finish 'FAIL Windows refused to bind the driver (error 265). On ARM64 this means the signature was rejected; Secure Boot would have to be off.'
    }
    Finish "FAIL $r"
}
catch { Finish "FAIL $($_.Exception.Message)" }
""";
}
