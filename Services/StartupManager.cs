using Microsoft.Win32;

namespace Qlyptra.Services;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Qlyptra";

    public static bool IsEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(RunKey, false); return key?.GetValue(ValueName) is string; }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к приложению.");
            key.SetValue(ValueName, $"\"{exe}\" --startup");
        }
        else key.DeleteValue(ValueName, false);
    }
}
