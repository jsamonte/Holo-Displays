using System;
using System.Runtime.InteropServices;
using Godot;

namespace HoloDisplays;

/// <summary>
/// M0 smoke test. Confirms the C# toolchain is alive and that Godot is the
/// native arm64 .NET build, then lists what DisplayServer can see.
///
/// M1 replaces this with real monitor discovery and capture.
/// </summary>
public partial class Main : Control
{
    private RichTextLabel _log = null!;

    public override void _Ready()
    {
        _log = GetNode<RichTextLabel>("%Log");
        Report();
    }

    private void Report()
    {
        var engine = (string)Engine.GetVersionInfo()["string"];
        var arch = RuntimeInformation.ProcessArchitecture;
        var emulated = arch != Architecture.Arm64 && RuntimeInformation.OSArchitecture == Architecture.Arm64;

        Line($"Godot        {engine}");
        Line($".NET         {System.Environment.Version}  ({RuntimeInformation.FrameworkDescription})");
        Line($"Process arch {arch}        OS arch {RuntimeInformation.OSArchitecture}");

        if (emulated)
        {
            Line("");
            Line("[color=#ff6b6b]This is the x86_64 Godot build running emulated on an ARM64 machine.[/color]");
            Line("[color=#ff6b6b]Open the project with Godot_v4.7.2-stable_mono_windows_arm64.exe instead.[/color]");
            Line("[color=#ff6b6b]See docs/SETUP.md section 2.[/color]");
        }
        else
        {
            Line("");
            Line("[color=#51cf66]Native build, C# running. Toolchain is good.[/color]");
        }

        Line("");
        Line($"[b]Screens DisplayServer reports: {DisplayServer.GetScreenCount()}[/b]");
        Line($"Primary screen index: {DisplayServer.GetPrimaryScreen()}");
        Line("");

        for (var i = 0; i < DisplayServer.GetScreenCount(); i++)
        {
            var size = DisplayServer.ScreenGetSize(i);
            var pos = DisplayServer.ScreenGetPosition(i);
            var dpi = DisplayServer.ScreenGetDpi(i);
            var hz = DisplayServer.ScreenGetRefreshRate(i);
            var primary = i == DisplayServer.GetPrimaryScreen() ? "  [PRIMARY]" : "";

            Line($"  screen {i}:  {size.X}x{size.Y}  at ({pos.X},{pos.Y})  {dpi} dpi  {hz:0.#} Hz{primary}");
        }

        Line("");
        if (DisplayServer.GetScreenCount() < 3)
        {
            Line("[color=#ffd43b]Expecting 3 screens once VDD is installed with count=2.[/color]");
            Line("[color=#ffd43b]Follow docs/SETUP.md section 1, then reopen this.[/color]");
        }
        else
        {
            Line("[color=#51cf66]Three or more screens. VDD looks configured. M0 done.[/color]");
        }

        Line("");
        Line("[color=#868e96]Note: DisplayServer cannot tell a virtual monitor from a real one.[/color]");
        Line("[color=#868e96]M1 adds the Win32 calls that identify VDD displays by device name.[/color]");
    }

    /// <summary>
    /// Writes to the on-screen log and to stdout. The stdout copy is what makes
    /// this checkable with --headless, so bbcode tags get stripped out of it.
    /// </summary>
    private void Line(string text)
    {
        _log?.AppendText(text + "\n");
        GD.Print(System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-z]+(=[^\]]*)?\]", ""));
    }
}
