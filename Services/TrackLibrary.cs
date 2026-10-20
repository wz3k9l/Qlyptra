using System.Text.RegularExpressions;

namespace Qlyptra.Services;

/// <summary>
/// Сохранение треков рядом с программой: Tracks\{первый исполнитель}\{файл}.mp3
/// </summary>
public static class TrackLibrary
{
    public static string RootFolder => Path.Combine(AppPaths.AppDirectory, "Tracks");

    public static string GetPrimaryArtist(string? artists)
    {
        if (string.IsNullOrWhiteSpace(artists)) return "Unknown";
        var text = artists.Trim();
        // Яндекс обычно разделяет исполнителей запятой; берём только первого.
        var parts = text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var primary = parts.Length > 0 ? parts[0] : text;
        primary = primary.Trim();
        return string.IsNullOrWhiteSpace(primary) ? "Unknown" : primary;
    }

    public static string MakeSafeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "Unknown";
        if (name.Length > 80) name = name[..80].Trim();
        return name;
    }

    public static string BuildSavePath(string primaryArtist, string fullArtist, string title)
    {
        Directory.CreateDirectory(RootFolder);
        var folder = Path.Combine(RootFolder, MakeSafeFolderName(primaryArtist));
        Directory.CreateDirectory(folder);

        var baseName = string.IsNullOrWhiteSpace(fullArtist)
            ? title
            : $"{fullArtist} - {title}";
        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');
        baseName = baseName.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "track";
        if (baseName.Length > 120) baseName = baseName[..120].Trim();

        var path = Path.Combine(folder, baseName + ".mp3");
        if (!File.Exists(path)) return path;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(folder, $"{baseName} ({i}).mp3");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{baseName}_{Guid.NewGuid():N}.mp3");
    }
}
