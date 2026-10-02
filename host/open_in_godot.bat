@echo off
REM ---------------------------------------------------------------------------
REM Opens the host project in Godot .NET with a working C# toolchain.
REM
REM Why this exists:
REM   Godot 4.7.2's editor-side C# tooling cannot find an SDK when only the
REM   .NET 10 SDK is installed. It asks for an SDK matching its runtime version
REM   (10.0.12), sees 10.0.401, gives up with ".NET Sdk not found", and then
REM   fails to load Microsoft.Build. The build hammer does nothing, so pressing
REM   Play fails too.
REM
REM   Pointing DOTNET_ROOT at a .NET 8 SDK fixes it. This is done here, for this
REM   one process, rather than as a machine-wide environment variable, so
REM   nothing else on the system changes which .NET it uses.
REM
REM If you would rather not use this launcher, install the .NET 8 SDK into
REM C:\Program Files\dotnet (needs admin) and Godot will find it on its own.
REM ---------------------------------------------------------------------------

set "GODOT=C:\Users\jared\Downloads\Installers\Godot_v4.7.2-stable_mono_windows_arm64\Godot_v4.7.2-stable_mono_windows_arm64\Godot_v4.7.2-stable_mono_windows_arm64.exe"
set "DOTNET8=C:\Users\jared\.dotnet"

if not exist "%GODOT%" (
  echo.
  echo Godot not found at:
  echo   %GODOT%
  echo.
  echo This must be the mono_windows_arm64 build. The mono_win64 build is
  echo x86_64, runs emulated on this machine, and cannot see the ARM64 .NET SDK.
  echo See docs\SETUP.md section 2.
  pause
  exit /b 1
)

if not exist "%DOTNET8%\sdk" (
  echo.
  echo No .NET 8 SDK at %DOTNET8%.
  echo Install it without admin:
  echo   curl -L https://dot.net/v1/dotnet-install.ps1 -o dotnet-install.ps1
  echo   .\dotnet-install.ps1 -Channel 8.0 -Architecture arm64 -InstallDir "%DOTNET8%"
  pause
  exit /b 1
)

set "DOTNET_ROOT=%DOTNET8%"
set "PATH=%DOTNET8%;%PATH%"

REM MSBuild node reuse leaves daemons behind that can hang later builds.
set "MSBUILDDISABLENODEREUSE=1"

start "" "%GODOT%" --path "%~dp0"
