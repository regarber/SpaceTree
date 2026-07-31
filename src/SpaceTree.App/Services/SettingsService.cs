using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpaceTree.App.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under %APPDATA%\SpaceTree.
///
/// Saving is write-to-temp-then-replace so that a crash or a power cut during a
/// save cannot leave a truncated settings file behind — the previous file stays
/// intact until the new one is complete.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();

    public AppSettings Current { get; private set; } = new();

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceTree");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;

            string json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Json);
            if (loaded is not null)
                Current = Sanitize(loaded);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file must never stop the app from starting.
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));

                if (File.Exists(FilePath))
                    File.Replace(temp, FilePath, null);
                else
                    File.Move(temp, FilePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Read-only profile or roaming hiccup: losing preferences is not
                // worth interrupting the user over.
            }
            catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException)
            {
                // A value the serialiser refuses (NaN, a cycle) is a bug here,
                // not something the user did. Record it, but never let saving a
                // preference take down the operation that triggered the save.
                CrashLog.Write(e);
            }
        }
    }

    /// <summary>Clamps persisted values that a hand-edited file could put out of range.</summary>
    private static AppSettings Sanitize(AppSettings s)
    {
        s.WindowWidth = Math.Clamp(double.IsFinite(s.WindowWidth) ? s.WindowWidth : 1280, 640, 20000);
        s.WindowHeight = Math.Clamp(double.IsFinite(s.WindowHeight) ? s.WindowHeight : 800, 400, 20000);
        s.TreePaneWidth = Math.Clamp(double.IsFinite(s.TreePaneWidth) ? s.TreePaneWidth : 0.62, 0.15, 0.9);
        s.ThreadCount = Math.Clamp(s.ThreadCount, 1, 64);
        s.MinimumSize = Math.Max(0, s.MinimumSize);
        s.Columns ??= new();
        s.RecentPaths ??= new();
        return s;
    }
}
