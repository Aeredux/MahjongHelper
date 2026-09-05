using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MahjongHelper.Mahjong;

/// <summary>
/// KAN-11 capture writer. JSON sidecars live under %APPDATA%/MahjongHelper/captures/.
/// </summary>
public static class SnapCapture
{
    public const int KeepLastFiles = 10;

    public static readonly string CapturesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MahjongHelper",
        "captures");

    public static readonly string[] RequestFilePaths =
    {
        Path.Combine(CapturesDirectory, "request_snap"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MahjongHelper",
            "request_snap"),
    };

    public static string WriteJson(object payload)
    {
        Directory.CreateDirectory(CapturesDirectory);
        var name = $"snap-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json";
        var path = Path.Combine(CapturesDirectory, name);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        Prune();
        return path;
    }

    public static bool TryConsumeRequest()
    {
        foreach (var path in RequestFilePaths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                File.Delete(path);
                return true;
            }
            catch
            {
                // Keep looking; a locked request file should not block the other path.
            }
        }

        return false;
    }

    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(CapturesDirectory))
                return;

            var files = Directory.GetFiles(CapturesDirectory)
                .Where(p =>
                {
                    var name = Path.GetFileName(p);
                    return !string.Equals(name, "request_snap", StringComparison.OrdinalIgnoreCase);
                })
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            foreach (var extra in files.Skip(KeepLastFiles))
            {
                try { extra.Delete(); }
                catch { }
            }
        }
        catch { }
    }
}
