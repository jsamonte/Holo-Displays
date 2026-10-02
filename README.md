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
3. [SPEC.md](SPEC.md) — the full build spec and milestone list.

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

**The one thing that cannot ship inside the project is VDD itself.** It is a
signed display driver and has to be installed on the target machine. So the host
treats it as a dependency it detects and explains rather than assumes: on startup
it pings the pipe, and if nothing answers it says so and points at the install
steps instead of failing with an empty monitor list.

Two more one-time things on the recipient's side: the **Godot .NET** editor
matching their CPU architecture, and **Experimental APIs** enabled in Lens Studio
for `ws://`.

## Status

| Milestone | State |
| --- | --- |
| M0 Scaffold | done |
| M1 Host capture | not started |
| M2 Host server + test client | not started |
| M3 Host resolution changes | not started |
| M4 Lens single panel | not started |
| M5 Lens multi panel + drag/resize | not started |
| M6 Lens gaze tiers | not started |
| M7 Lens resize to resolution | not started |
| M8 Performance | if needed |
