# Setup

Written for this machine: a **Snapdragon X Plus (ARM64)** laptop running Windows
11 build 26200. The ARM64 part matters in two places and both are called out
below.

Checked on 2026-10-02:

| Thing | State |
| --- | --- |
| Lens Studio | 5.15.4 installed |
| .NET SDK | 10.0.401, **arm64** |
| Godot 4.7.2 .NET arm64 | installed, C# build verified |
| VDD package | 25.7.26 downloaded via winget, **driver not installed yet** |
| Virtual monitors | 0 — this is what step 1 fixes |

---

## 1. Virtual Display Driver

This is the only step that needs admin rights, and the only one that installs a
driver. Do it yourself.

### 1.1 Where it already is

`winget install --id=VirtualDrivers.Virtual-Display-Driver -e` has already run.
That **downloaded** the package but did not install the driver. The files are at:

```
C:\Users\jared\AppData\Local\Microsoft\WinGet\Packages\VirtualDrivers.Virtual-Display-Driver_Microsoft.Winget.Source_8wekyb3d8bbwe\
├── VDD Control.exe              <- the control app, run this
├── Dependencies\
│   ├── devcon.exe
│   └── vdd_settings.xml         <- the TEMPLATE, not the live config
└── SignedDrivers\
    ├── ARM64\VDD\               <- use this one on this laptop
    └── x86\VDD\
```

The ARM64 driver catalog is validly signed (SignPath Foundation, via GlobalSign).

### 1.2 Do not use VDD Control's Install button on this machine

It fails, and the log says why:

```
[INFO] Detected system architecture: x86
[INFO] Expected driver path: SignedDrivers\x86\VDD\
[ERROR] Driver installation failed with exit code: 2
[ERROR] devcon.exe failed.
```

Both `VDD Control.exe` and the `devcon.exe` it shells out to are **x86_64**
binaries. Under emulation they ask Windows what architecture it is and get back
"x86", so VDD Control reaches for `SignedDrivers\x86\VDD\` and devcon refuses to
install an x86 driver on an ARM64 system. The correct ARM64 driver is sitting in
the next folder along, untouched.

This is the same shape of bug as the Godot one in §2: an emulated process asking
an architecture question and believing the answer.

### 1.3 Install the ARM64 driver by hand

There is no ARM64 `devcon.exe` on this machine and the Windows Driver Kit is not
installed, so skip devcon entirely. Windows' built-in **Add Legacy Hardware**
wizard creates the same root-enumerated device node without it.

The ARM64 INF is correct for this — it declares `[Standard.NTARM64]` with the
`Root\MttVDD` hardware ID, which is exactly what the wizard needs.

1. Press **Win+X** → **Device Manager** (as administrator).
2. Select any node, then **Action** → **Add legacy hardware**.
3. **Next** → **Install the hardware that I manually select from a list (Advanced)**.
4. Choose **Display adapters** → **Next**.
5. Click **Have Disk...** → **Browse...** and point at:
   ```
   C:\Users\jared\AppData\Local\Microsoft\WinGet\Packages\VirtualDrivers.Virtual-Display-Driver_Microsoft.Winget.Source_8wekyb3d8bbwe\SignedDrivers\ARM64\VDD\MttVDD.inf
   ```
   Note **ARM64**, not x86.
6. Pick **Virtual Display Driver** → **Next** → **Next**.
7. Accept the Windows Security prompt.

Check it worked:

```powershell
Get-PnpDevice -Class Display | Where-Object FriendlyName -match 'Virtual Display'
```

Status should be **OK**. `C:\VirtualDisplayDriver\vdd_settings.xml` already
exists — VDD Control got that far before failing, so the config is in place.

### 1.4 If it lands with Code 52

The VDD project's own docs say:

> ARM64 Support in Windows 11 24H2 or later may require test signing be enabled.

This laptop is ARM64 on build 26200, so you may hit it. The symptom is the device
appearing in Device Manager with **Code 52** ("Windows cannot verify the digital
signature"). The driver catalog *is* validly signed (SignPath Foundation via
GlobalSign), but that is a code-signing certificate rather than a WHQL
attestation, and ARM64 enforces harder.

If that happens, enabling test signing is a real trade-off, not a formality:

```
bcdedit /set testsigning on      (admin, then reboot)
```

- It requires **Secure Boot off**, which on a BitLocker machine means you will be
  asked for your **BitLocker recovery key**. Have it before you start.
- It leaves a permanent "Test Mode" watermark on the desktop.
- Some DRM-protected video stops playing.

That is your call to make, not something to do casually. If you would rather not,
the alternative is to run the host against your built-in display only — which is
enough to build and test M1 through M3, just with one monitor instead of two.

### 1.5 Configure resolutions

The driver reads its live config from:

```
C:\VirtualDisplayDriver\vdd_settings.xml
```

Not the copy in `Dependencies\`. That one is only the template used at install
time.

**You do not need to hand-edit `<monitors><count>` .** The host app sets the
display count at runtime over the driver's control pipe — see §1.6. The count in
the XML is just the value the driver boots with. Leave it at 1.

The resolution list, though, has no runtime equivalent and does have to be right
here. The host can
only switch a monitor to a mode the driver advertises, so everything you want to
resize to has to be in here. The shipped list is 800x600, 1366x768, 1920x1080,
2560x1440, 3840x2160. Add the portrait modes, because a tall panel beside your
laptop is one of the nicer things this project can do:

```xml
<resolutions>
    <resolution>
        <width>1920</width>
        <height>1080</height>
        <refresh_rate>60</refresh_rate>
    </resolution>
    <resolution>
        <width>1080</width>
        <height>1920</height>
        <refresh_rate>60</refresh_rate>
    </resolution>
    <resolution>
        <width>2560</width>
        <height>1440</height>
        <refresh_rate>60</refresh_rate>
    </resolution>
    <resolution>
        <width>1440</width>
        <height>2560</height>
        <refresh_rate>60</refresh_rate>
    </resolution>
    <resolution>
        <width>1280</width>
        <height>800</height>
        <refresh_rate>60</refresh_rate>
    </resolution>
</resolutions>
```

Anything in `<global><g_refresh_rate>` is applied to every resolution on top of
the per-resolution rate, so you get 60/90/120/144/165/244 variants for free. For
streaming to glasses at 15 fps none of that matters much — leave it alone.

Then **reload the driver** — Device Manager → the Virtual Display Driver device →
Disable, then Enable. Or send `RELOAD_DRIVER` over the control pipe (§1.6).
Changes to the XML do not take effect until you do.

### 1.6 Controlling the driver at runtime

The driver runs a named pipe server. This is how the host app adds and removes
virtual monitors without touching XML, without admin, and without a reboot.

```
\\.\pipe\MTTVirtualDisplayPipe
```

Messages are **UTF-16** (wide strings) in **message mode**. The pipe is created
with the security descriptor `D:(A;;GA;;;WD)` — full access for *Everyone* — so
**any process can drive it unelevated**. That is what makes the Godot host able
to own display count rather than asking you to edit a file.

Commands, read from the driver source (`Driver.cpp`):

| Command | Effect |
| --- | --- |
| `SETDISPLAYCOUNT <n>` | Set the number of virtual monitors. Writes the XML *and* reloads the driver for you |
| `RELOAD_DRIVER` | Reload after an XML edit |
| `PING` | Liveness check. Use this to detect whether VDD is installed and running |
| `GETSETTINGS` | Read back current settings |
| `GETALLGPUS`, `GETASSIGNEDGPU`, `SETGPU` | Which GPU renders the virtual displays |
| `IDDCXVERSION` | Driver's IddCx version |
| `HDRPLUS`, `SDR10`, `HARDWARECURSOR`, `CUSTOMEDID`, `PREVENTSPOOF`, `CEAOVERRIDE` | Boolean toggles, `true`/`false` |
| `LOGGING`, `LOG_DEBUG` | Logging toggles |

`SETDISPLAYCOUNT` parses its argument with `swscanf_s(buffer + 15, L"%d", ...)`,
so `SETDISPLAYCOUNT 2` works.

**There is no command for resolutions.** The mode list only comes from the XML in
§1.5, which is why that part still has to be edited by hand once.

Note that `SETDISPLAYCOUNT` reloads the driver, so every monitor blinks out and
back. The host has to re-enumerate displays afterwards, and any resolution the
user had set returns to default.

### 1.7 M0 is done when

With the XML left at `<count>1</count>`, **Settings → System → Display** shows
**2 displays**: your built-in panel plus one virtual one.

Then prove the control pipe works, which is the mechanism the host will use for
the rest of the project. This needs no admin:

```powershell
$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'MTTVirtualDisplayPipe', 'InOut')
$p.Connect(3000)
$w = New-Object System.IO.StreamWriter($p, [System.Text.Encoding]::Unicode)
$w.Write('SETDISPLAYCOUNT 2'); $w.Flush()
$p.Dispose()
```

Display Settings should go to **3 displays** a second or two later, after the
driver reloads itself. Set it back with `SETDISPLAYCOUNT 1` if you like.

That is M0 done: the driver works, and the count is controllable from code
rather than from a file.

Drag the monitors into a sensible arrangement while you are in there — the host
reports each monitor's desktop position, and it is easier to reason about later
if the layout is not a pile.

### 1.8 Before GPU driver updates

Uninstall VDD before any major GPU or chipset driver update, then reinstall. The
project's docs warn about black screens and display-priority problems otherwise.

---

## 2. Godot .NET

### 2.1 The problem you hit, and why

You were opening:

```
Godot_v4.7.2-stable_mono_win64.exe        <- x86_64
```

On a Snapdragon laptop that runs **emulated**. Godot then shells out to find a
.NET SDK, and an emulated x64 process can only see x64 SDKs — which live in
`C:\Program Files\dotnet\x64\`. You have the **arm64** SDK in
`C:\Program Files\dotnet\`, and no x64 one at all. So Godot found zero SDKs and
refused to do anything with C#.

Godot's docs put it plainly: *"Be sure to install the 64-bit version of the
SDK(s) if you are using the 64-bit version of Godot."* Same rule, one
architecture over.

Two ways out. Installing the x64 .NET SDK alongside would work but leaves you
compiling and running everything under emulation. Using the native build is
faster and simpler, so that is what this project does.

### 2.2 Use this build

```
C:\Users\jared\Downloads\Installers\Godot_v4.7.2-stable_mono_windows_arm64\
    Godot_v4.7.2-stable_mono_windows_arm64\
        Godot_v4.7.2-stable_mono_windows_arm64.exe     <- open the host project with this
```

Already downloaded and verified. Note the name: **`mono_windows_arm64`**, not
`mono_win64`. They differ by one word and that word is the whole bug.

Worth making a Desktop shortcut to it and deleting the `mono_win64` folder so
you cannot grab the wrong one at 2am.

Do not confuse either with plain `Godot_v4.7.2-stable_win64.exe`, which is the
standard build and has no C# support at all.

### 2.3 dotnet on PATH

`C:\Program Files\dotnet` has been added to your **user** PATH (no admin needed).
It was missing, which would have broken command-line builds even once the
architecture was right.

Open a **new** terminal and check:

```powershell
dotnet --info
```

Expect `RID: win-arm64`, `Architecture: arm64`, SDK `10.0.401`.

### 2.4 Verified working

A scratch Godot C# project targeting `net8.0` was created, restored, built and
run headless on this machine. It printed its test string. So:

- .NET 10 SDK is fine. Godot 4.5+ needs "`.NET 8` or later", and although only
  the 10.0.12 reference pack is installed locally, `net8.0` targeting packs
  restore from NuGet on first build. **You do not need to install the .NET 8 SDK.**
- First build in a fresh project takes ~15 s while it restores. After that it is
  quick.

### 2.5 Opening the host project

From M1 onward the host project is at [host/](../host/).

1. Open `Godot_v4.7.2-stable_mono_windows_arm64.exe`.
2. **Import** → pick `C:\GitHub\Holo-Displays\host\project.godot`.
3. Build once with the hammer icon, top right, before pressing play. Godot needs
   the assembly to exist before it can load C# nodes.

If the editor ever reports no .NET SDK again, you are in the wrong executable.
Check the window title.

---

## 3. Lens Studio

### 3.1 The project

Lens Studio **5.15.4** is installed, and the project already exists at:

```
C:\GitHub\Holo-Displays\Spectacles\Holo-Display\Holo-Display.esproj
```

It was made from the **Spectacles** template and already has both packages this
project needs:

- `SpectaclesInteractionKit.lspkg`
- `SpectaclesUIKit.lspkg`

So nothing to create. Scripts go in `Assets/Scripts/` from M4 onward.

### 3.2 Turn on Experimental APIs

This is required and it has a consequence.

**Project Settings → enable Experimental APIs.**

Snap's docs: *"Using insecure connections (`ws`) requires enabling Experimental
APIs. While these Lenses are suitable for testing purposes, they cannot be
published."*

We stream over plain `ws://` on your LAN, so this is not optional. The lens will
run on your own Spectacles via Send to Device and will never be publishable.
That is the accepted trade for v1 — `wss://` with a self-signed cert is on the
nice-to-have list in `SPEC.md`.

### 3.3 Assets to add

In the Asset Browser, add both:

- **InternetModule** — creates the WebSocket, and makes resources from blobs
- **RemoteMediaModule** — turns those resources into image textures

From Lens Studio 5.9 the WebSocket APIs live on `InternetModule`, so an older
tutorial telling you to use a global `WebSocket` is out of date.

### 3.4 Port

The host listens on **8880**. You will type `ws://<laptop LAN IP>:8880` into the
panel controller's inspector fields at M4. The host UI shows the IP and port in
large text so you can read it without taking the glasses off.

Both devices must be on the same network, and it must not be a guest or
client-isolated one — AP isolation silently blocks this and looks exactly like a
firewall problem.

### 3.5 Windows Firewall

The first time the host opens its socket, Windows will prompt. Allow it on
**Private** networks. If you miss the prompt the glasses will connect to nothing
and time out; the fix is an inbound TCP allow rule for 8880.

---

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| Godot says no .NET SDK found | Running `mono_win64` instead of `mono_windows_arm64` (§2.1) |
| Godot has no C# option at all | Running the standard `win64` build, not a `.mono` one |
| `dotnet` not recognised | Terminal predates the PATH change — open a new one |
| VDD Control says `Detected system architecture: x86`, devcon exit code 2 | VDD Control is x86_64 and misdetects this ARM64 CPU. Install by hand (§1.3) |
| Virtual display in Device Manager with Code 52 | ARM64 signature enforcement (§1.4) |
| Edited the XML, nothing changed | Edited `Dependencies\vdd_settings.xml` instead of `C:\VirtualDisplayDriver\vdd_settings.xml`, or did not reload the driver (§1.5) |
| Pipe connect fails / `PING` times out | Driver not installed, or installed but disabled. The pipe only exists while the driver runs (§1.6) |
| Resize to a resolution fails | That mode is not in the driver's resolution list — add it and reload (§1.5) |
| Monitors blink and resolutions reset | Expected: `SETDISPLAYCOUNT` reloads the driver (§1.6) |
| Glasses never connect | Experimental APIs off, firewall, wrong IP, or AP isolation |
| Black screen after a GPU driver update | Uninstall and reinstall VDD (§1.8) |

## Sources

- [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
- [Godot C# basics](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_basics.html)
- [Spectacles WebSocket API](https://developers.snap.com/spectacles/about-spectacles-features/apis/web-socket)
