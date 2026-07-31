using System.IO;
using System.Text;

namespace SpaceTree.App.Services;

/// <summary>Appends unhandled exceptions to a rolling log next to the settings file.</summary>
public static class CrashLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string LogPath => Path.Combine(SettingsService.Directory, "errors.log");

    public static void Write(Exception? exception)
    {
        if (exception is null)
            return;

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsService.Directory);

                // Truncate rather than grow without bound; the newest failure is
                // the one worth keeping.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    File.Delete(LogPath);

                var sb = new StringBuilder();
                sb.AppendLine(new string('-', 72));
                sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  SpaceTree");
                sb.AppendLine(exception.ToString());
                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never itself throw.
        }
    }
}
