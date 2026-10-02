@echo off
setlocal enabledelayedexpansion
REM ---------------------------------------------------------------------------
REM Opens the host project in Godot .NET with a working C# toolchain.
REM
REM Why this exists:
REM   Godot 4.7's editor-side C# tooling cannot find an SDK when only the .NET
REM   10 SDK is installed. It asks for an SDK matching its runtime version,
REM   sees a different feature band, gives up with ".NET Sdk not found", and
REM   then fails to load Microsoft.Build. The build hammer does nothing, so
REM   pressing Play fails too. Pointing DOTNET_ROOT at a .NET 8 SDK fixes it.
REM
REM   That is done here for this one process, rather than as a machine-wide
REM   environment variable, so nothing else on the system changes which .NET it
REM   uses.
REM
REM Nothing below is specific to one machine: Godot and the .NET 8 SDK are
REM searched for in the usual places, and GODOT can be set beforehand to point
REM at a specific build.
REM ---------------------------------------------------------------------------

REM ---- find Godot .NET -------------------------------------------------------
REM Honour an existing GODOT if the caller set one.
if defined GODOT if exist "%GODOT%" goto :got_godot

set "GODOT="
for %%D in (
  "%USERPROFILE%\Downloads\Installers"
  "%USERPROFILE%\Downloads"
  "%LOCALAPPDATA%\Programs"
  "C:\Program Files"
) do (
  if not defined GODOT (
    for /f "delims=" %%F in ('dir /b /s "%%~D\Godot_v*_mono_*.exe" 2^>nul ^| findstr /v /i "console"') do (
      if not defined GODOT set "GODOT=%%F"
    )
  )
)

if not defined GODOT (
  echo.
  echo Could not find a Godot .NET build.
  echo.
  echo Download the one matching your CPU from https://godotengine.org/download/windows/
  echo   - 64-bit Intel/AMD : Godot_v4.7.x-stable_mono_win64
  echo   - Snapdragon/ARM   : Godot_v4.7.x-stable_mono_windows_arm64
  echo.
  echo The plain (non-mono^) builds have no C# support at all, and on an ARM
  echo machine the win64 build runs emulated and cannot see the ARM64 .NET SDK.
  echo See docs\SETUP.md section 2.
  echo.
  echo Or set GODOT to the executable before running this:
  echo   set "GODOT=C:\path\to\Godot_v4.7.2-stable_mono_windows_arm64.exe"
  echo.
  pause
  exit /b 1
)

:got_godot
echo Godot: %GODOT%

REM ---- find a .NET 8 SDK, if one is about ------------------------------------
set "DOTNET8="
for %%D in (
  "%USERPROFILE%\.dotnet"
  "%ProgramFiles%\dotnet"
  "%LOCALAPPDATA%\Microsoft\dotnet"
) do (
  if not defined DOTNET8 (
    if exist "%%~D\sdk\8.*" set "DOTNET8=%%~D"
  )
)

if defined DOTNET8 (
  echo .NET 8 SDK: %DOTNET8%
  set "DOTNET_ROOT=%DOTNET8%"
  set "PATH=%DOTNET8%;%PATH%"
) else (
  echo.
  echo No .NET 8 SDK found. Godot's build button may fail with
  echo   ".NET Sdk not found"
  echo even though 'dotnet build' works from a terminal.
  echo.
  echo Install one without admin rights:
  echo   curl -L https://dot.net/v1/dotnet-install.ps1 -o dotnet-install.ps1
  echo   .\dotnet-install.ps1 -Channel 8.0 -InstallDir "%%USERPROFILE%%\.dotnet"
  echo.
  echo Continuing anyway.
  echo.
)

REM MSBuild node reuse leaves daemons behind that can hang later builds.
set "MSBUILDDISABLENODEREUSE=1"

start "" "%GODOT%" --path "%~dp0"
endlocal
