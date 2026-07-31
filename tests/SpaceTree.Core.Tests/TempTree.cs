using System.Text;

namespace SpaceTree.Core.Tests;

/// <summary>
/// Builds a throwaway directory tree on disk and removes it afterwards.
/// The scanner talks to the real filesystem, so the tests do too — a mocked
/// filesystem would not exercise the interop that the engine is built on.
/// </summary>
public sealed class TempTree : IDisposable
{
    public TempTree(string? name = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "SpaceTreeTests", (name ?? "t") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Dir(string relative)
    {
        string full = Path.Combine(Root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Creates a file of exactly <paramref name="size"/> bytes.</summary>
    public string File(string relative, int size)
    {
        string full = Path.Combine(Root, relative);
        string? dir = Path.GetDirectoryName(full);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
        if (size > 0)
        {
            var buffer = new byte[Math.Min(size, 64 * 1024)];
            Array.Fill(buffer, (byte)0xAB);
            int remaining = size;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, buffer.Length);
                stream.Write(buffer, 0, chunk);
                remaining -= chunk;
            }
        }
        return full;
    }

    /// <summary>Creates a deeply nested path that exceeds the legacy 260-character limit.</summary>
    public string DeepPath(int totalLength, string segment = "verylongdirectorysegmentname")
    {
        var sb = new StringBuilder(Root);
        while (sb.Length + segment.Length + 1 < totalLength)
        {
            sb.Append('\\').Append(segment);
        }
        string path = sb.ToString();
        Directory.CreateDirectory(@"\\?\" + path);
        return path;
    }

    /// <summary>Back-dates a file or directory. Directories need the Directory API, not the File one.</summary>
    public void SetLastWrite(string path, DateTime utc)
    {
        if (Directory.Exists(path))
            Directory.SetLastWriteTimeUtc(path, utc);
        else
            System.IO.File.SetLastWriteTimeUtc(path, utc);
    }

    public void Dispose()
    {
        try
        {
            DeleteRecursive(@"\\?\" + Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a locked handle must not fail an otherwise passing test.
        }
    }

    private static void DeleteRecursive(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (string file in Directory.EnumerateFiles(path))
            System.IO.File.SetAttributes(file, FileAttributes.Normal);

        foreach (string dir in Directory.EnumerateDirectories(path))
            DeleteRecursive(dir);

        Directory.Delete(path, true);
    }
}
