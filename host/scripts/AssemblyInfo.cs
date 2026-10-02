// This host is Windows-only by design: it captures Windows monitors, drives
// ChangeDisplaySettingsEx, and talks to a Windows display driver over a named
// pipe. Declaring that here is what makes the Win32 calls below legal without
// CA1416 warnings at every call site.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
