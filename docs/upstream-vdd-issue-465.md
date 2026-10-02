# Draft comment for VirtualDrivers/Virtual-Display-Driver#465

Written to post at
<https://github.com/VirtualDrivers/Virtual-Display-Driver/issues/465>.
Kept in the repo as provenance for why ARM64 does not work here, and what was
reported upstream. Everything below was measured first-hand on this machine.

---

Some first-hand data that may narrow this down, plus a separate bug ARM64 users
hit before they ever reach the signature problem.

**Machine:** Snapdragon X Plus, Windows 11 build 26200, Secure Boot **on**,
BitLocker **on**, Memory Integrity (HVCI) **on**. VDD 25.7.26 via winget.

## The signature is the whole problem, and here is a controlled comparison

`pnputil /add-driver` on `SignedDrivers\ARM64\VDD\MttVDD.inf` fails, and
`setupapi.dev.log` is unambiguous:

```
!!!  sig:  Driver package failed signature validation. Error = 0x800B0109
     sto:  {DRIVERSTORE IMPORT VALIDATE: exit(0x800b0109)}
!!!  sto:  Failed to import driver package into Driver Store. Error = 0x800B0109
!!!  ndv:  Driver package import failed for device.
     ndv:  Installing NULL driver.
!!!  ndv:  Failed to install device instance 'ROOT\DISPLAY\0000'. Error = 0x00000109
```

`0x800B0109` is `CERT_E_UNTRUSTEDROOT`. The subsequent `0x109` (265) on the
device bind is just that failure propagating, which is worth knowing because
265 on its own looks like a SetupAPI problem rather than a signing one.

The catalog is validly signed, just not with a certificate ARM64 accepts:

```
VDD  ARM64  mttvdd.cat
  Status : Valid
  Subject: CN=SignPath Foundation, O=SignPath Foundation, L=Lewes, S=Delaware, C=US
  Issuer : CN=GlobalSign GCC R45 CodeSigning CA 2020, O=GlobalSign nv-sa, C=BE
```

To check whether this is really about the certificate rather than about ARM64,
IddCx, or `Root\`-enumerated devices, I installed a **different** indirect
display driver on the same machine with nothing disabled: spacedesk's ARM64
build (v2.2.33 — their own download path is `/downloadarm64`, and the x64 ones
are `/downloadidd64`, so it is IDD-based too).

```
spacedesk  spacedeskdisplay.cat
  Status : Valid
  Subject: CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation
  Issuer : CN=Microsoft Windows Third Party Component CA 2014, O=Microsoft Corporation
```

Its INF declares `ntarm64.10.0...22000`. It **installed cleanly**, landed in the
driver store as a Display-class driver, and the service started — with Secure
Boot, BitLocker and HVCI all left on.

So: a Microsoft-signed, ARM64, Display-class indirect display driver installs on
this hardware without any security being weakened. The platform is fine, IddCx
is fine, root enumeration is fine. **The only difference that matters is who
signed the catalog.** That turns the WHQL theory earlier in this thread from
plausible into measured, and it rules out the alternatives.

It also means every current workaround — disabling core isolation and enabling
test signing — is treating a symptom. Those are also not available to a lot of
people: on a BitLocker machine, changing Secure Boot forces a recovery-key
prompt, and on managed or corporate hardware it is simply off the table.

## Separate bug: VDD Control misdetects ARM64 as x86

Independent of signing, and cheap to fix. `VDD Control.exe` fails before it gets
anywhere near the certificate:

```
[INFO] Detected system architecture: x86
[INFO] Expected driver path: SignedDrivers\x86\VDD\
[ERROR] Driver installation failed with exit code: 2
[ERROR] devcon.exe failed.
```

Both `VDD Control.exe` and the bundled `Dependencies\devcon.exe` are **x86_64**
binaries. Under emulation on ARM64 they query the system architecture, get back
`x86`, and reach for `SignedDrivers\x86\VDD` — which is actually the amd64
driver (its INF is `[Standard.NTamd64]`, despite the folder name). The ARM64
driver sitting one directory over is never tried.

Two consequences: ARM64 users get a confusing "x86" failure that hides the real
problem, and the bundled devcon cannot help on ARM64 regardless.

Suggestions, in case useful:

- Detect the **native** architecture rather than the process one —
  `IsWow64Process2` or `RuntimeInformation.OSArchitecture` rather than
  `PROCESSOR_ARCHITECTURE`, which is emulation-local.
- Consider dropping devcon. `pnputil` is in-box and native on every
  architecture; the root device node can be created with SetupAPI
  (`SetupDiCreateDeviceInfo` → `SPDRP_HARDWAREID` → `DIF_REGISTERDEVICE` →
  `UpdateDriverForPlugAndPlayDevices`), which is what devcon does anyway. That
  path works natively on ARM64 and gets as far as the signature check.
- Renaming `SignedDrivers\x86` to `x64` would prevent a related confusion.

## On getting the driver Microsoft-signed

Mentioning this because a small project could reasonably assume it is out of
reach, and the numbers are better than they look:

- **SignPath Foundation cannot get you there.** It issues **OV**-level
  certificates, and Partner Center attestation requires **EV**. That is
  structural, not a policy that can be appealed — which is presumably why the
  current signature is what it is.
- **Partner Center registration, and attestation submissions, are free.** The
  EV certificate is the only cost.
- **It is not an annual subscription.** The returned package carries
  *Microsoft's* signature, timestamped, so it keeps installing after the EV
  certificate lapses. A certificate is only needed at submission time, i.e.
  once per driver revision rather than once per year.
- EV certificates are around **$279/year** through resellers with a registered
  business. For maintainers without a company, SSL.com sells a **Sole Proprietor
  EV** that validates an individual and explicitly covers kernel-mode driver
  signing and Partner Center submission.

**One thing I could not confirm and would check before spending anything:**
whether ARM64 is covered by *attestation* signing or requires full HLK/WHQL
certification. The spacedesk catalog above is signed by the Hardware
Compatibility Publisher, which is the signer for both, so the signature alone
does not distinguish them. The attestation docs enumerate x86/x64 in places
without mentioning ARM64. Worth a direct question to Microsoft before buying a
certificate on the strength of it.

Happy to run tests on this hardware if that helps — I have a Snapdragon X Plus
on 26200 with Secure Boot and HVCI on, which seems to be the configuration that
reproduces this.
