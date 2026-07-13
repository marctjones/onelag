namespace OneLag.Windows.Tests;

/// <summary>
/// A test that only means anything on Windows. It is skipped elsewhere rather than silently passing, so a
/// green run on a macOS dev machine never reads as evidence that the Windows probes work.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: this exercises real Win32 and PDH calls.";
        }
    }
}
