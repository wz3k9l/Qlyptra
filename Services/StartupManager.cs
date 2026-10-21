using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Qlyptra.Services;

/// <summary>
/// Registers Qlyptra as a logon task with the highest available privileges.
/// The UAC prompt is shown only when the task is changed, not at each logon.
/// </summary>
public static class StartupManager
{
    private const string TaskName = "QlyptraStartupElevated";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "Qlyptra";

    public static bool IsEnabled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return false;
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                throw new InvalidOperationException("Не удалось определить путь к Qlyptra.exe. Запустите опубликованную версию приложения.");

            // --startup keeps the window hidden in the tray after logon.
            var arguments = $"/Create /F /SC ONLOGON /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --startup\" /IT /RL HIGHEST";
            RunElevated(arguments);
        }
        else if (IsEnabled())
        {
            RunElevated($"/Delete /TN \"{TaskName}\" /F");
        }

        RemoveLegacyStartupEntry();
    }

    /// <summary>Removes the old non-elevated Run-key entry left by earlier versions.</summary>
    public static void RemoveLegacyStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    private static void RunElevated(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null) throw new InvalidOperationException("Не удалось запустить Планировщик заданий Windows.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Планировщик заданий Windows вернул код ошибки {process.ExitCode}. Проверьте права текущей учётной записи.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Настройка автозапуска отменена в окне контроля учётных записей Windows (UAC).", ex);
        }
    }
}
