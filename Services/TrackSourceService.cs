using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Qlyptra.Models;

namespace Qlyptra.Services;

public sealed class TrackSourceService
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    static TrackSourceService()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130 Safari/537.36 Qlyptra/1.0");
        Client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    }

    public async Task<TrackInfo> ReadYandexTrackAsync(string input, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Вставьте полную ссылку вида https://music.yandex.ru/track/…");

        var host = uri.Host.ToLowerInvariant();
        if (!(host == "music.yandex.ru" || host.EndsWith(".music.yandex.ru") || host == "music.yandex.com" || host.EndsWith(".music.yandex.com")))
            throw new InvalidOperationException("Это не ссылка на Яндекс Музыку. Для аудио по прямой ссылке используй URL файла .mp3.");

        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var info = ExtractMetadata(html, uri.ToString());
        if (string.IsNullOrWhiteSpace(info.Title))
            info.Title = "Трек Яндекс Музыки";

        if (!string.IsNullOrWhiteSpace(info.CoverUrl))
        {
            try
            {
                using var imageResponse = await Client.GetAsync(info.CoverUrl, cancellationToken);
                if (imageResponse.IsSuccessStatusCode)
                {
                    var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length is > 100 and < 12_000_000)
                        info.CoverBytes = bytes;
                }
            }
            catch { }
        }
        return info;
    }

    public async Task<string> DownloadDirectMp3Async(string input, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Для скачивания нужен прямой HTTP(S)-адрес аудиофайла.");
        if (uri.Host.Equals("music.yandex.ru", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("music.yandex.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ссылка ведёт на страницу Яндекс Музыки, а не на MP3-файл. Прямое извлечение потока из этой страницы не поддерживается.");

        TempFileCleaner.EnsureFolders();
        var tempPath = Path.Combine(TempFileCleaner.CacheFolder, $"source_{Guid.NewGuid():N}.mp3");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 Qlyptra/1.0");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var pathExtension = Path.GetExtension(uri.AbsolutePath);
            if (!pathExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) && !contentType.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Ссылка не подтверждена как MP3 (тип содержимого: {contentType}). Нужен прямой URL файла .mp3.");
            if (response.Content.Headers.ContentLength is > 150_000_000)
                throw new InvalidOperationException("Файл больше лимита 150 МБ.");

            await using (var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(tempPath))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    total += read;
                    if (total > 150_000_000) throw new InvalidOperationException("Файл больше лимита 150 МБ.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            var fileInfo = new FileInfo(tempPath);
            if (fileInfo.Length < 4096)
                throw new InvalidOperationException("Скачанный файл слишком мал и, вероятно, не является аудио.");
            await using var check = File.OpenRead(tempPath);
            var header = new byte[3];
            _ = await check.ReadAsync(header, cancellationToken);
            var hasId3 = header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3';
            var looksLikeMpeg = header[0] == 0xFF && (header[1] & 0xE0) == 0xE0;
            if (!hasId3 && !looksLikeMpeg)
                throw new InvalidOperationException("Сервер вернул не MP3-файл (похоже на страницу HTML или другой формат).");
            return tempPath;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static TrackInfo ExtractMetadata(string html, string url)
    {
        var info = new TrackInfo { OriginalUrl = url };
        var tags = Regex.Matches(html, "<meta\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in tags)
        {
            var tag = match.Value;
            var key = Attribute(tag, "property") ?? Attribute(tag, "name") ?? "";
            var value = Attribute(tag, "content") ?? "";
            if (key.Equals("og:title", StringComparison.OrdinalIgnoreCase) || key.Equals("twitter:title", StringComparison.OrdinalIgnoreCase))
                info.Title = CleanTitle(value);
            if (key.Equals("og:description", StringComparison.OrdinalIgnoreCase) || key.Equals("description", StringComparison.OrdinalIgnoreCase))
                if (string.IsNullOrWhiteSpace(info.Artist)) info.Artist = ExtractArtist(value);
            if (key.Equals("og:image", StringComparison.OrdinalIgnoreCase) || key.Equals("twitter:image", StringComparison.OrdinalIgnoreCase))
                info.CoverUrl = value;
        }

        // Structured JSON-LD is preferred where the page includes it.
        foreach (Match script in Regex.Matches(html, "<script[^>]*type=[\"']application/ld\\+json[\"'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            try
            {
                using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(script.Groups[1].Value));
                SearchJson(doc.RootElement, info);
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(info.CoverUrl) && info.CoverUrl.StartsWith("//")) info.CoverUrl = "https:" + info.CoverUrl;
        info.Title = CleanTitle(WebUtility.HtmlDecode(info.Title));
        info.Artist = WebUtility.HtmlDecode(info.Artist).Trim();
        return info;
    }

    private static void SearchJson(JsonElement element, TrackInfo info)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (p.NameEquals("name") && p.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(info.Title))
                    info.Title = p.Value.GetString() ?? "";
                if ((p.NameEquals("byArtist") || p.NameEquals("author")) && string.IsNullOrWhiteSpace(info.Artist))
                    info.Artist = ReadArtist(p.Value);
                if ((p.NameEquals("image") || p.NameEquals("thumbnailUrl")) && string.IsNullOrWhiteSpace(info.CoverUrl))
                    info.CoverUrl = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) SearchJson(p.Value, info);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) SearchJson(child, info);
    }

    private static string ReadArtist(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind == JsonValueKind.Array) return string.Join(", ", value.EnumerateArray().Select(ReadArtist).Where(s => !string.IsNullOrWhiteSpace(s)));
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) return name.GetString() ?? "";
        }
        return "";
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, "\\b" + Regex.Escape(name) + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return null;
        return WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
    }

    private static string ExtractArtist(string description)
    {
        var value = WebUtility.HtmlDecode(description);
        var patterns = new[]
        {
            @"(?:исполнитель|артист)\s*:\s*(.+?)(?:\.|;|\||$)",
            @"by\s+(.+?)(?:\s*[|•·]|$)"
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return "";
    }

    private static string CleanTitle(string title)
    {
        var value = (title ?? "").Trim();
        value = Regex.Replace(value, @"\s*[—|–-]\s*(Яндекс Музыка|Yandex Music).*$", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"^Слушать\s+музыку\s*[:—-]?\s*", "", RegexOptions.IgnoreCase);
        return value.Trim();
    }
}
