using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using Qlyptra.Services;

namespace Qlyptra;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogFatal("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                "Ошибка Qlyptra:\n\n" + args.Exception.Message + "\n\nПодробности в файле:\n" + LogPath(),
                "Qlyptra", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            LogFatal("UnhandledException", ex);
            try
            {
                MessageBox.Show(
                    "Критическая ошибка Qlyptra:\n\n" + (ex?.Message ?? args.ExceptionObject) + "\n\n" + LogPath(),
                    "Qlyptra", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogFatal("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        try
        {
            StartupManager.RemoveLegacyStartupEntry();
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            base.OnStartup(e);
            try { TempFileCleaner.CleanOldFiles(); } catch { }
        }
        catch (Exception ex)
        {
            LogFatal("OnStartup", ex);
            MessageBox.Show("Не удалось запустить Qlyptra:\n\n" + ex, "Qlyptra", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string LogPath()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qlyptra");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "startup.log");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "startup.log");
        }
    }

    internal static void LogFatal(string where, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(DateTime.Now.ToString("s"));
            sb.AppendLine(where);
            sb.AppendLine(ex?.ToString() ?? "(null)");
            sb.AppendLine(new string('-', 40));
            File.AppendAllText(LogPath(), sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }
}
