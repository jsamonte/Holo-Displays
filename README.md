# Holo-Displays

Virtual monitors for Snap Spectacles.

Windows thinks extra monitors are plugged in. Each one appears on the glasses as
its own floating panel that you can drag, drop and resize. Resizing a panel
changes that monitor's real resolution in Windows, like swapping in a bigger
monitor. Only the panels you are looking at (or near) get streamed, to save
bandwidth and decode work.

This is **extended displays, not screen mirroring**. The laptop's own keyboard
and trackpad stay the input; the cursor moves between virtual monitors normally.

## Parts

| Part | Where | What it is |
| --- | --- | --- |
| Virtual Display Driver (VDD) | installed system-wide | Open source IddCx driver that adds virtual monitors to Windows 10/11 |
| Host app | [host/](host/) | Godot 4.7 .NET (C#), Windows only. Captures each virtual monitor, streams it over WebSocket, changes monitor resolution on request |
| Lens | [Spectacles/Holo-Display/](Spectacles/Holo-Display/) | Lens Studio 5.15 project for Spectacles, TypeScript. One panel per virtual monitor |
| Test client | [tools/](tools/) | Pretends to be the glasses, so the host can be tested without wearing them |
| Docs | [docs/](docs/) | [PROTOCOL.md](docs/PROTOCOL.md), [SETUP.md](docs/SETUP.md) |

### Layout note

`SPEC.md` calls for the lens at `/lens`. In this repo it lives at
`Spectacles/Holo-Display/` instead, because the Lens Studio project was already
created there. Lens scripts go in `Spectacles/Holo-Display/Assets/Scripts/`.
Everything else follows the spec.

## Start here

1. [docs/SETUP.md](docs/SETUP.md) — install and configure VDD, Godot .NET, Lens Studio.
2. [docs/PROTOCOL.md](docs/PROTOCOL.md) — the WebSocket protocol between host and lens.
3. [docs/LENS_SETUP.md](docs/LENS_SETUP.md) — wiring the lens scene, step by step.
4. [SPEC.md](SPEC.md) — the full build spec and milestone list.

## This machine

Built and tested on a **Snapdragon X Plus (ARM64)** Windows 11 laptop. That
matters more than usual:

- Godot must be the **`mono_windows_arm64`** build, not `mono_win64`. The x86_64
  build runs under emulation and then cannot see the ARM64 .NET SDK.
- VDD on ARM64 + Windows 11 24H2 or later may require test signing.
- VDD Control's own Install button **misdetects this CPU as x86** and fails. The
  driver installs fine by hand.

All three are covered in [docs/SETUP.md](docs/SETUP.md).

## Sharing this with someone else

The goal is that handing over **the Lens Studio project and the Godot project is
enough**. That constrains the design, so it is worth stating plainly.

What it rules out, and what the host therefore does not do:

- **No external helper tools.** Everything the host needs is C# inside `host/` —
  Win32 via P/Invoke, and the driver's named pipe. No bundled devcon, no Python,
  no native DLLs to copy alongside.
- **No hand-edited config as a setup step.** The host sets the virtual monitor
  count itself over `\\.\pipe\MTTVirtualDisplayPipe`, unelevated. See
  [docs/SETUP.md §1.6](docs/SETUP.md).
- `tools/` is a development aid, not part of what gets shared.

**The one thing that cannot ship inside the project is VDD itself**, because it
is a display driver. So the host provisions it instead: on startup it pings the
driver's pipe, and if nothing answers it offers an **Install driver…** button.

That button is deliberately not quiet about what it does. It shows the whole
plan first, uses a copy already on the machine or fetches one from the
[upstream project](https://github.com/VirtualDrivers/Virtual-Display-Driver)
(MIT), raises a normal UAC prompt, and runs the install in a **visible** console
window — writing the script to disk in plain text first, so it can be read
before approving. Every step lands in the app's log, and **Uninstall driver**
reverses it. An app that installs a display driver behind a hidden prompt is
shaped exactly like malware, and both the user and their antivirus deserve to
see the work.

**ARM64 is the exception.** Windows on ARM only accepts WHQL/Store-signed
drivers, and VDD carries a commercial certificate, so it is refused
(`0x800B0109`) however it is packaged. On an ARM64 machine the button does not
appear and the app explains why instead. See
[docs/SETUP.md §1.3a](docs/SETUP.md) for what getting past that would cost, and
why running the host on an x64 PC is usually the better answer.

Two more one-time things on the recipient's side: the **Godot .NET** editor
matching their CPU architecture, and **Experimental APIs** enabled in Lens Studio
for `ws://`.

## Status

| Milestone | State |
| --- | --- |
| M0 Scaffold | done |
| M1 Host capture | done |
| M2 Host server + test client | done |
| M3 Host resolution changes | code done, untested (needs VDD) |
| M4 Lens single panel | scripts done + typechecked, scene wiring pending |
| M5 Lens multi panel + drag/resize | scripts done + typechecked, scene wiring pending |
| M6 Lens gaze tiers | scripts done + typechecked, scene wiring pending |
| M7 Lens resize to resolution | scripts done + typechecked, scene wiring pending |
| M8 Performance | measured: 19.5 fps ceiling, one display. Phase 2 only needed for two at `full` |
