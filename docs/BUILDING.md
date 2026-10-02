# Building a standalone host

The host normally runs from the Godot editor. Exporting gives a `.exe` that
runs on a machine with no Godot and no .NET installed — the .NET runtime is
bundled alongside it.

Two presets, in `host/export_presets.cfg`:

| Preset | Output | For |
| --- | --- | --- |
| `Windows ARM64` | `host/export/arm64/` | Snapdragon laptops |
| `Windows x86_64` | `host/export/x86_64/` | everything else |

`host/export/` is gitignored. Builds are ~175 MB each, mostly the bundled .NET
runtime (187 files).

## One-time setup

**Export templates.** They must be the **`.mono`** set, matching your Godot
version exactly. Plain templates will not export a C# project.

```powershell
curl -L -o mono_templates.tpz `
  https://github.com/godotengine/godot/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_export_templates.tpz
```

The `.tpz` is a zip with a misleading extension, which `Expand-Archive`
refuses. Extract with `unzip` or rename it to `.zip` first. The contents go in:

```
%APPDATA%\Godot\export_templates\4.7.2.stable.mono\
```

Everything is flattened out of `templates/` into that folder. The full archive
is 1.2 GB because it carries Android, iOS and Linux too; for Windows only you
need `windows_*`, `version.txt` and `icudt_godot.dat`, which is about 630 MB.

**A solution file.** Godot refuses to export a C# project without one:

```
ERROR: Export .NET Project: This project contains C# files but no solution
file was found at ... HoloDisplaysHost.sln
```

It is committed, so this should not come up. If it ever needs regenerating,
note that the .NET 10 SDK produces the newer `.slnx` by default and Godot wants
the classic format:

```bash
dotnet new sln --name HoloDisplaysHost --format sln
dotnet sln HoloDisplaysHost.sln add HoloDisplaysHost.csproj
```

## Exporting

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"      # the .NET 8 SDK, see SETUP.md 2.4
$env:MSBUILDDISABLENODEREUSE = "1"

& "<godot-mono>.exe" --headless --path host --export-release "Windows ARM64"
& "<godot-mono>.exe" --headless --path host --export-release "Windows x86_64"
```

**Godot does not exit when a headless export finishes.** The files are written
and complete, then the process sits there. Give it a timeout and kill it; that
is not a failed export. Check the output rather than the exit code:

```powershell
Get-ChildItem host\export\arm64\data_HoloDisplaysHost_windows_arm64 | Measure-Object
# expect ~187 files, and no *.tmp left in host\export\arm64
```

A leftover `.pck*.tmp` does mean it was interrupted mid-write — delete the
output folder and run it again.

If you drive this from PowerShell's `Start-Process`, quote the preset name
inside the argument array or it gets split on the space and you get
`Invalid export preset name: Windows`.

## Verified

Both builds were exported and run on 2026-10-02:

- ARM64 — runs natively, binds `0.0.0.0:8880`, passes `tools/protocol_test.mjs`.
- x86_64 — correct PE machine type, runs (emulated on the ARM64 machine it was
  built on), binds and passes the same test.

Worth noting from that: the x64 build running under emulation still correctly
reports the ARM64 driver situation, because the VDD check reads
`RuntimeInformation.OSArchitecture` rather than `ProcessArchitecture`. Reading
the process architecture is exactly the mistake that breaks VDD Control
(`docs/SETUP.md` §1.2), so it is worth keeping that way.

## Sharing a build

Ship the whole architecture folder, not just the `.exe` — the `.pck` and the
`data_HoloDisplaysHost_windows_*` folder beside it are both required.

Recipients need no Godot and no .NET. They do still need VDD for virtual
monitors, which the app offers to install on x64 (see the README).
