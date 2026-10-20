using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Qlyptra.Services;

public sealed record YandexDownloadedTrack(string Mp3Path, string Title, string Artist);

/// <summary>
/// Uses the open-source llistochek/yandex-music-downloader CLI to obtain the track
/// authorized by the user's Yandex Music token, then transcodes it to high-quality MP3.
/// </summary>
public sealed class YandexDownloadService
{
    private static readonly string[] AudioExtensions = [".flac", ".m4a", ".mp3", ".aac", ".ogg", ".opus", ".wav"];

    public async Task<YandexDownloadedTrack> DownloadTrackAsMp3Async(
        string trackUrl, string token, string title, string artist, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Сначала добавьте токен Яндекс Музыки в настройках Qlyptra.");
        if (!Uri.TryCreate(trackUrl, UriKind.Absolute, out var uri) ||
            !(uri.Host.Equals("music.yandex.ru", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".music.yandex.ru", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("music.yandex.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".music.yandex.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Для скачивания через Яндекс загрузчик нужна ссылка music.yandex.ru или music.yandex.com.");

        var baseDir = AppPaths.BinDirectory;
        var pythonExe = Path.Combine(baseDir, "ymd-env", "Scripts", "python.exe");
        var workerScript = Path.Combine(baseDir, "yandex_downloader_worker.py");
        var ffmpegExe = Path.Combine(baseDir, "ffmpeg.exe");
        if (!File.Exists(pythonExe) || !File.Exists(workerScript))
        {
            // fallback: старая раскладка рядом с exe
            baseDir = AppPaths.AppDirectory;
            pythonExe = Path.Combine(baseDir, "ymd-env", "Scripts", "python.exe");
            workerScript = Path.Combine(baseDir, "yandex_downloader_worker.py");
            ffmpegExe = Path.Combine(baseDir, "ffmpeg.exe");
        }
        if (!File.Exists(pythonExe) || !File.Exists(workerScript))
            throw new FileNotFoundException("Не найден встроенный загрузчик Яндекс Музыки (папка bin\\ymd-env). Пересоберите проект через build.bat.", pythonExe);
        if (!File.Exists(ffmpegExe))
            throw new FileNotFoundException("Не найден ffmpeg.exe. Пересоберите проект через build.bat.", ffmpegExe);

        TempFileCleaner.EnsureFolders();
        var workDir = Path.Combine(TempFileCleaner.CacheFolder, "ymd_" + Guid.NewGuid().ToString("N"));
        var downloadDir = Path.Combine(workDir, "download");
        Directory.CreateDirectory(downloadDir);
        try
        {
            var start = NewProcess(pythonExe);
            start.ArgumentList.Add(workerScript);
            start.RedirectStandardInput = true;
            start.Environment["PYTHONIOENCODING"] = "utf-8";

            using var downloader = new Process { StartInfo = start };
            if (!downloader.Start()) throw new InvalidOperationException("Не удалось запустить загрузчик Яндекс Музыки.");
            var stdoutTask = downloader.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = downloader.StandardError.ReadToEndAsync(cancellationToken);
            var requestJson = JsonSerializer.Serialize(new { token, url = trackUrl, output_dir = downloadDir });
            await downloader.StandardInput.WriteAsync(requestJson.AsMemory(), cancellationToken);
            await downloader.StandardInput.FlushAsync(cancellationToken);
            downloader.StandardInput.Close();
            await downloader.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (downloader.ExitCode != 0)
            {
                var details = string.Join(Environment.NewLine, new[] { stderr.Trim(), stdout.Trim() }.Where(s => s.Length > 0));
                details = details.Length > 700 ? details[^700..] : details;
                throw new InvalidOperationException("Загрузчик Яндекс Музыки завершился с ошибкой. Проверь токен, доступ к треку и интернет." +
                    (details.Length > 0 ? Environment.NewLine + details : ""));
            }

            var source = Directory.EnumerateFiles(downloadDir, "*", SearchOption.AllDirectories)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (source is null) throw new InvalidOperationException("Загрузчик завершился без ошибки, но аудиофайл не найден в его выходной папке.");

            var outputPath = Path.Combine(TempFileCleaner.CacheFolder, "yandex_" + Guid.NewGuid().ToString("N") + ".mp3");
            try
            {
                var ffmpeg = NewProcess(ffmpegExe);
                AddArgument(ffmpeg, "-hide_banner", null);
                AddArgument(ffmpeg, "-loglevel", "error");
                AddArgument(ffmpeg, "-y", null);
                AddArgument(ffmpeg, "-i", source);
                AddArgument(ffmpeg, "-map", "0:a:0");
                AddArgument(ffmpeg, "-map", "0:v?");
                AddArgument(ffmpeg, "-c:a", "libmp3lame");
                AddArgument(ffmpeg, "-q:a", "0");
                AddArgument(ffmpeg, "-c:v", "copy");
                AddArgument(ffmpeg, "-id3v2_version", "3");
                if (!string.IsNullOrWhiteSpace(title) && !title.Equals("Трек Яндекс Музыки", StringComparison.OrdinalIgnoreCase))
                    AddArgument(ffmpeg, "-metadata", "title=" + SafeMetadata(title));
                if (!string.IsNullOrWhiteSpace(artist)) AddArgument(ffmpeg, "-metadata", "artist=" + SafeMetadata(artist));
                AddArgument(ffmpeg, outputPath, null);

                using var converter = new Process { StartInfo = ffmpeg };
                if (!converter.Start()) throw new InvalidOperationException("Не удалось запустить FFmpeg.");
                var errorTask = converter.StandardError.ReadToEndAsync(cancellationToken);
                await converter.WaitForExitAsync(cancellationToken);
                var conversionError = await errorTask;
                if (converter.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length < 4096)
                    throw new InvalidOperationException("Не удалось преобразовать трек в MP3." +
                        (string.IsNullOrWhiteSpace(conversionError) ? "" : Environment.NewLine + Limit(conversionError, 500)));
                var tags = ReadMp3Tags(outputPath);
                var detectedTitle = string.IsNullOrWhiteSpace(tags.Title) ? Path.GetFileNameWithoutExtension(source) : tags.Title;
                var detectedArtist = string.IsNullOrWhiteSpace(tags.Artist) ? artist : tags.Artist;
                return new YandexDownloadedTrack(outputPath, detectedTitle, detectedArtist);
            }
            catch
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                throw;
            }
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
        }
    }

    private static (string Title, string Artist) ReadMp3Tags(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[10];
            if (stream.Read(header, 0, 10) != 10 || header[0] != 'I' || header[1] != 'D' || header[2] != '3') return ("", "");
            var version = header[3];
            if (version is not (3 or 4) || (header[5] & 0x80) != 0) return ("", "");
            var tagSize = DecodeSynchsafe(header, 6);
            if (tagSize <= 0 || tagSize > 16 * 1024 * 1024 || tagSize > stream.Length - 10) return ("", "");
            var tag = new byte[tagSize];
            if (stream.Read(tag, 0, tag.Length) != tag.Length) return ("", "");
            var offset = 0;
            if ((header[5] & 0x40) != 0)
            {
                if (tag.Length < 4) return ("", "");
                offset = version == 3
                    ? ((tag[0] << 24) | (tag[1] << 16) | (tag[2] << 8) | tag[3]) + 4
                    : DecodeSynchsafe(tag, 0);
                if (offset < 0 || offset > tag.Length) return ("", "");
            }
            string title = "", artist = "";
            while (offset + 10 <= tag.Length && tag[offset] != 0)
            {
                var id = System.Text.Encoding.ASCII.GetString(tag, offset, 4);
                var size = version == 3
                    ? (tag[offset + 4] << 24) | (tag[offset + 5] << 16) | (tag[offset + 6] << 8) | tag[offset + 7]
                    : DecodeSynchsafe(tag, offset + 4);
                if (size <= 0 || offset + 10L + size > tag.Length) break;
                if ((id is "TIT2" or "TPE1") && size > 1)
                {
                    var body = tag.AsSpan(offset + 10, size);
                    var textEncoding = body[0];
                    var bytes = body[1..].ToArray();
                    string value = textEncoding switch
                    {
                        0 => System.Text.Encoding.Latin1.GetString(bytes),
                        1 => System.Text.Encoding.Unicode.GetString(bytes),
                        2 => System.Text.Encoding.BigEndianUnicode.GetString(bytes),
                        3 => System.Text.Encoding.UTF8.GetString(bytes),
                        _ => ""
                    };
                    value = value.Trim('\0', '\uFEFF', ' ', '\r', '\n');
                    if (id == "TIT2" && title.Length == 0) title = value;
                    if (id == "TPE1" && artist.Length == 0) artist = value;
                }
                offset += 10 + size;
            }
            return (title, artist);
        }
        catch { return ("", ""); }
    }

    private static int DecodeSynchsafe(byte[] bytes, int offset) =>
        ((bytes[offset] & 0x7F) << 21) | ((bytes[offset + 1] & 0x7F) << 14) |
        ((bytes[offset + 2] & 0x7F) << 7) | (bytes[offset + 3] & 0x7F);

    private static ProcessStartInfo NewProcess(string executable) => new(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = AppContext.BaseDirectory
    };

    private static void AddArgument(ProcessStartInfo start, string name, string? value)
    {
        start.ArgumentList.Add(name);
        if (value is not null) start.ArgumentList.Add(value);
    }

    private static string SafeMetadata(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();
    private static string Limit(string value, int max) => value.Length > max ? value[..max] + "…" : value;
}
