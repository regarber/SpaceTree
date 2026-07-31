using System.Diagnostics;
using System.Security.Principal;

namespace SpaceTree.App.Services;

/// <summary>Detects and acquires administrator rights.</summary>
public static class ElevationService
{
    private static readonly Lazy<bool> Elevated = new(Detect);

    /// <summary>True when the process is already running with a full administrator token.</summary>
    public static bool IsElevated => Elevated.Value;

    private static bool Detect()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Relaunches the app elevated, forwarding the current scan root so the user
    /// lands back where they were. Returns false if the UAC prompt was declined,
    /// in which case the caller should simply carry on unelevated.
    /// </summary>
    public static bool RestartElevated(string? scanPath)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            return false;

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };

        if (!string.IsNullOrWhiteSpace(scanPath))
            info.ArgumentList.Add(scanPath);

        try
        {
            Process.Start(info);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ERROR_CANCELLED: the user dismissed the consent prompt.
            return false;
        }
    }
}
