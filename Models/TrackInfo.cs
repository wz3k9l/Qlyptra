namespace Qlyptra.Models;

public sealed class TrackInfo
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string OriginalUrl { get; set; } = "";
    public string? CoverUrl { get; set; }
    public byte[]? CoverBytes { get; set; }
}
