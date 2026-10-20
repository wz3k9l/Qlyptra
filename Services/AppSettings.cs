using System.Text.Json.Serialization;

namespace Qlyptra.Services;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public bool AlwaysOnTop { get; set; } = false;
    public bool LaunchAtStartup { get; set; } = false;
    public string Hotkey { get; set; } = "Ctrl+Alt+M";
    public int DeleteDelaySeconds { get; set; } = 75;
    /// <summary>Множитель яркости тёмного фона (0.25–1.0).</summary>
    public double BackgroundBrightness { get; set; } = 1.0;

    [JsonIgnore]
    public string YandexToken { get; set; } = "";

    // Only the DPAPI-encrypted value is written to settings.json.
    public string YandexTokenProtected
    {
        get => SecretProtector.Protect(YandexToken);
        set => YandexToken = SecretProtector.Unprotect(value ?? "");
    }
}
