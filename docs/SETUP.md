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

### 1.1a Normally, let the app do it

On **x64 Windows**, you do not need any of the manual steps below. Start the
host and press **Install driver…** in its header. It will:

- show you exactly what it is about to do, before anything happens
- use a copy of the driver already on the machine, or fetch it from the
  [Virtual Display Driver releases](https://github.com/VirtualDrivers/Virtual-Display-Driver)
- ask for administrator rights with a normal Windows UAC prompt
- run the install in a **visible** console window you can read
- log every step into the app's own log pane

The script it runs is written to disk first, in plain text, and the log says
where — read it before approving if you want to. **Uninstall driver** reverses
it, as does Device Manager.

The button only appears when installing could actually succeed. On this ARM64
laptop it does not appear, and the app says why instead (§1.3a).

The rest of section 1 is the manual path, and the ARM64 situation.

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

### 1.3a The ARM64 wall, and what it actually costs to get past it

**On this laptop the driver cannot be installed at all as things stand.** The
install fails with:

```
Driver package failed signature validation. Error = 0x800B0109
A certificate chain processed, but terminated in a root certificate
which is not trusted by the trust provider.
```

Windows on ARM64 only accepts drivers signed with a **Windows, WHQL, ELAM or
Store** certificate. It does not accept commercial code-signing certificates at
all — x64 is far more relaxed about this. VDD is signed by SignPath Foundation
via GlobalSign, which is a commercial code-signing certificate, so ARM64 refuses
it. This is an open upstream bug:
[VirtualDrivers/Virtual-Display-Driver#465](https://github.com/VirtualDrivers/Virtual-Display-Driver/issues/465).

Nothing in this project can fix that. Bundling the driver into the app does not
help; the signature is the problem, not the packaging. It would take the VDD
project getting the driver attestation-signed through Microsoft's Partner
Center.

**But this is a VDD problem, not an ARM64 problem.** Properly signed ARM64
virtual display drivers install here perfectly well with Secure Boot, BitLocker
and Memory Integrity all left on. Verified by inspecting
[spacedesk](https://www.spacedesk.net/)'s ARM64 driver (v2.2.33), whose display
catalog is signed:

```
CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation
Issuer: Microsoft Windows Third Party Component CA 2014
```

and whose INF declares `ntarm64.10.0...22000`. That is exactly the signature
ARM64 requires. So the fix is a correctly signed driver, **not** a weakened
machine — see §1.3b before touching Secure Boot.

### 1.3a-2 There is a second, separate Snapdragon blocker

Before spending anything on signing, know that signing may not be enough.

[VDD issue #483](https://github.com/VirtualDrivers/Virtual-Display-Driver/issues/483)
(opened 2026-04-14, still open, **zero comments**) reports that on **Snapdragon X
with the Adreno GPU** — this exact hardware — the driver installs and creates a
monitor, but the display is **never attached to the desktop**:

- it appears in `EnumDisplayDevices`
- it does **not** appear in `EnumDisplayMonitors`, DXGI `EnumOutputs`, or
  anything built on them

That is fatal for this project independently of the certificate. The host finds
displays through Godot's `DisplayServer` and captures with `ScreenGetImage`,
both of which need a display that is actually a desktop output. A monitor
Windows will not attach is one nothing can capture and nothing can be dragged
onto.

So this laptop has **two** unsolved problems stacked:

1. The driver will not install (certificate, §1.3a).
2. On Snapdragon X it reportedly would not work even once installed (#483).

Nobody has answered #483 in six months. **Do not buy a certificate expecting it
to fix this machine** — fixing problem 1 may simply expose problem 2.

### 1.3a-3 Why no free signed driver exists anywhere

Not an accident, and not for want of looking. The open-source indirect display
drivers and how they sign, from parsec-vdd's own comparison table:

| Project | IddCx | Signed |
| --- | --- | --- |
| IddSampleDriver | 1.2 | no |
| RustDeskIddDriver | 1.2 | no |
| virtual-display-rs | 1.5 | no |
| parsec-vdd | 1.5 | yes — **SignPath Foundation** |
| Virtual-Display-Driver (HDR) | 1.10 | yes — **SignPath Foundation** |
| usbmmidd_v2 (Amyuni) | n/a | yes — commercial, x64 only |

Both signed open-source drivers use **SignPath Foundation**, because that is the
free code-signing programme for open source. SignPath issues **OV**-level
certificates, and OV is exactly what Windows on ARM64 refuses.

So the structural position is: free OSS signing gives OV; ARM64 demands
WHQL/Store; therefore **no free, open-source, ARM64-installable virtual display
driver exists**, and none can while that remains the funding model.

### 1.3b Getting virtual monitors here without weakening anything

In rough order of effort:

**1. Use a Microsoft-signed virtual display driver instead — tested, and it
does not work on a single machine.**

[spacedesk](https://www.spacedesk.net/download/)'s ARM64 server (v2.2.33) was
installed on this machine to settle the question. It **installed cleanly**, with
Secure Boot, BitLocker and Memory Integrity untouched: `spacedeskdisplay.inf`
landed in the driver store as a Display-class driver and `spacedeskService`
started. That is the proof that the signature, not the architecture, is what
blocks VDD.

But **no monitor appears**, and it cannot be made to on one machine.

spacedesk materialises a display only when one of its viewer clients connects.
The obvious trick — run its free Windows viewer on the same laptop, pointed at
itself — **does not work, by design**. Tested on 2026-10-02:

- `127.0.0.1` fails. The TCP port accepts loopback, but discovery runs over UDP
  and those sockets bind only to the real interfaces, never to `127.0.0.1`, so
  the viewer searches forever.
- The machine's own LAN address fails too, with an explicit refusal:
  **`Invalid entry: Local host`**. The viewer detects the address belongs to
  this host and rejects it outright.

So a spacedesk display needs a **second device** — phone, tablet or another PC —
running the viewer and staying connected. The monitor exists only while that
device is attached. Using one as a permanent dummy viewer to conjure a monitor,
which this host then captures and re-encodes for the glasses, is not a design
worth building on.

Even with a viewer connected it would be the wrong shape for this project: you
would run spacedesk's capture-encode-network stack purely to bring a monitor
into existence, then this host would capture that same monitor and encode and
stream it *again* to the glasses. Two encoders for one picture.

Three further problems: it installs seven drivers (audio, HID, USB, bus,
capture, mouse, display) when only one is wanted; its licence restricts
redistribution, so it cannot be bundled the way MIT-licensed VDD can; and it is
free for **non-commercial use only**.

Useful as proof. Not a foundation.

**2. Attestation-sign VDD yourself.** VDD is MIT licensed, so this is allowed.
Build it, make a CAB, sign the CAB with an EV certificate, submit it to the
[Partner Center hardware dashboard](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation),
and Microsoft returns an attestation-signed driver that installs on ARM64 with
Secure Boot on. This is the clean, permanent fix and it would help everyone
stuck on issue #465.

**What this actually costs.** Less than "$300/year" suggests, because the
recurring framing is wrong:

| Item | Cost |
| --- | --- |
| Partner Center / Hardware Developer Program registration | **free** |
| Microsoft Entra ID directory (created during registration) | **free** |
| Microsoft's attestation signing itself | **free**, unlimited submissions |
| EV code signing certificate | the only real cost |

For the certificate, the cheapest routes found in 2026:

- **With a registered business:** Sectigo EV via resellers, around
  **$279/year**, or roughly $840 for three years.
- **As an individual, with no company:** SSL.com's
  [Sole Proprietor EV](https://www.ssl.com/products/software-integrity/code-signing/ev-sole-proprietor/)
  validates a person rather than a business entity and explicitly covers
  Windows kernel-mode driver signing and Partner Center submission.
  **$359 for one year**, down to **$201/year on a five-year term**. Cloud
  signing via eSigner means no hardware token to buy — though confirm with them
  that your chosen option meets the EV kernel-mode requirement, as their own
  page warns a YubiKey may not.

> **Read §1.3a-2 before spending any of this.** Microsoft's own answer on a
> [signed ARM64 driver that still would not install](https://learn.microsoft.com/en-us/answers/questions/2225246/signed-arm64-native-printer-driver-will-not-instal)
> never offers attestation as a fix — it says ARM64 "intentionally enforces
> stricter Code Integrity policies than Windows x64", and the route reported to
> work was **full WHQL with an HLK catalog**, which is lab certification, not a
> $300 certificate. The figures below are therefore the **x64** story. For ARM64
> the cost is unknown and larger, and #483 suggests a Snapdragon display would
> not attach to the desktop even then.

**The certificate is not an annual subscription for this purpose.** Microsoft's
attestation signature is theirs, not yours, and it is timestamped — the signed
driver keeps working after your certificate expires. You only need a live
certificate at the moment you *submit*. So signing VDD once is a **one-off
~$279–359**, not a yearly bill, unless you want to sign new driver versions
later.

Two things that do **not** work, both checked:

- [Azure Trusted Signing](https://learn.microsoft.com/en-au/answers/questions/5866910/hardware-program-verification-using-azures-trusted)
  is cheaper but Microsoft state plainly it supports neither EV certificates nor
  driver signing, so it cannot be used for hardware-program attestation.
- **SignPath Foundation**, which signs open source for free and is already what
  VDD uses, issues **OV-level** certificates. Attestation requires EV, so the
  free-for-OSS route does not reach.

**The genuinely free option is to not pay for it yourself.** VDD is open source
and [issue #465](https://github.com/VirtualDrivers/Virtual-Display-Driver/issues/465)
is open precisely because its ARM64 build cannot install. If the project ever
attestation-signs its driver, every ARM64 user is unblocked at no cost to
anyone downstream. Adding your findings to that issue costs nothing and is the
highest-leverage thing available here.

**3. Run the host on an x64 Windows PC.** VDD installs there without ceremony,
and the glasses connect over the LAN to whichever machine runs the host.

### 1.3b-note Writing our own driver does not help

The obvious thought is to skip VDD and write a virtual display driver for this
project. It does not get us anywhere, for a reason that has nothing to do with
code quality:

**Windows on ARM64 refuses to load any driver that Microsoft has not signed.**
That rule is about the signature on the driver package, not about who wrote it
or how good it is. A driver written here would start out *unsigned*, which is
strictly worse than VDD's position — VDD at least has a real commercial
certificate. Ours would need the same EV certificate and the same Partner
Center attestation run, at the same ~$300/year, before it could install on this
laptop.

Nor can the driver be avoided altogether. Windows only reports a monitor that a
display adapter driver created; there is no user-mode API to add one. A
borderless window pretending to be a screen is not a monitor — the cursor will
not travel into it, windows will not maximise to it, and the taskbar will not
follow. That is mirroring-shaped, and this project is explicitly about extended
displays.

So the cost of a from-scratch driver is several weeks of UMDF/IddCx work to
arrive at exactly the same wall, having reimplemented something MIT-licensed
that already works. If the EV certificate is ever bought, spend it
attestation-signing **VDD**, not a rewrite.

Only if none of those suit you is §1.3c worth reading.

### 1.3c Turning off Secure Boot — the last resort

Having read §1.3b, if you still want this: **it means stopping Windows
enforcing the rule**, which is three changes, in this order. This is almost
certainly the wrong trade now that §1.3b exists.

1. **Memory Integrity off** — Windows Security → Device security → Core
   isolation → Memory integrity → Off. Reboot.
2. **Secure Boot off** — this is a **UEFI firmware setting and cannot be changed
   from inside Windows**. Settings → System → Recovery → Advanced startup →
   Restart now → Troubleshoot → Advanced options → UEFI Firmware Settings →
   Restart. Find Secure Boot in the firmware menu (usually under Security or
   Boot) and disable it.
3. **Test signing on** — once Secure Boot is off, in an admin terminal:
   `bcdedit /set testsigning on`, then reboot. It has no effect while Secure
   Boot is on.

**What that costs, permanently, until you undo it:**

| | |
| --- | --- |
| BitLocker | Changing Secure Boot invalidates the TPM measurement, so Windows will demand your **48-digit recovery key** at the next boot. Get it from [aka.ms/myrecoverykey](https://aka.ms/myrecoverykey) **before** you start. Your C: drive is fully encrypted. |
| Desktop | A permanent "Test Mode" watermark in the corner |
| Security | Memory Integrity and Secure Boot are two of the stronger protections on a modern Windows machine. Off is meaningfully weaker. |
| Media | Some DRM-protected video stops playing |
| Certainty | **It still might not work.** Issue #465 is open and ARM64 users report mixed results even after all this. |

To undo: `bcdedit /set testsigning off`, re-enable Secure Boot in firmware, turn
Memory Integrity back on. The virtual monitors stop working at that point.

Prefer any of the routes in §1.3b to this. You can also develop against the
built-in display in the meantime: start the host with `HOLO_STREAM_PRIMARY=1`
and everything except the resolution-change path works, including the whole
lens.

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

### 2.4 You also need the .NET 8 SDK, for the editor

`dotnet build` works fine with only the .NET 10 SDK — the `net8.0` reference
packs restore from NuGet. But **Godot's editor tooling does not**:

```
ERROR: .NET Sdk not found. The required version is '10.0.12'.
ERROR: Could not load file or assembly 'Microsoft.Build, Version=15.1.0.0'
```

Godot 4.7.2 looks for an SDK matching its *runtime* version (10.0.12), sees
10.0.401, and gives up. The build hammer then does nothing, and pressing Play
fails with it, because Godot will not run C# it could not build.

The .NET 8 SDK (8.0.425, arm64) has been installed **per-user** at
`C:\Users\jared\.dotnet`, which needed no admin:

```powershell
curl -L https://dot.net/v1/dotnet-install.ps1 -o dotnet-install.ps1
.\dotnet-install.ps1 -Channel 8.0 -Architecture arm64 -InstallDir "$env:USERPROFILE\.dotnet"
```

A per-user install is invisible to Godot unless `DOTNET_ROOT` points at it, and
setting that machine-wide would change which .NET *every* app on your account
uses. So it is scoped to one process instead — see §2.5.

If you would rather have it work everywhere with no launcher, install the .NET 8
SDK into `C:\Program Files\dotnet` (needs admin) and Godot finds it unaided.

### 2.5 Opening the host project

Use **[host/open_in_godot.bat](../host/open_in_godot.bat)**. It sets
`DOTNET_ROOT` to the .NET 8 SDK for that one Godot process and opens the
project. Nothing else on the machine is affected.

Opening Godot directly works too, but the build hammer will fail as in §2.4.

Either way, build once before pressing Play — Godot needs the assembly to exist
before it can load C# nodes. From a terminal, `dotnet build` in `host/` always
works regardless of the editor.

If the editor reports no .NET SDK, you are either in the wrong executable or you
launched Godot without the batch file.

### 2.6 Measured capture performance

Run `Godot --path host -- --bench` (or the `--bench` user arg) to time capture
and encode. On this laptop, one 1920x1280 display:

| | |
| --- | --- |
| capture (`ScreenGetImage`) | 35.5 ms |
| unchanged-frame hash | 2.1 ms |
| encode (`SaveJpgToBuffer`, q 0.7) | 13.8 ms |
| frame size | 169 KB |
| ceiling | **19.5 fps**, single threaded |

An earlier version of this table said 20.7 fps, which was optimistic: the hash
sat between the two timers and was not being counted at all. It is measured now.

So Phase 1 clears the 15 fps target for **one** display with room to spare. Two
virtual monitors both at `full` would need ~97 ms a pass and land near 10 fps,
under target — which is exactly when the Phase 2 work in `SPEC.md` (DXGI Desktop
Duplication on worker threads) earns its place.

Bandwidth is worth noticing too: 147 KB at 15 fps is about 18 Mbps per display.
The gaze tiers in M6 exist precisely so that only the panel you are looking at
pays that.

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
