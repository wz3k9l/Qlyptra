using System;
using System.IO;

namespace Qlyptra.Services;

/// <summary>
/// Папка с Qlyptra.exe и подпапка bin (ffmpeg, ymd-env, worker).
/// Для single-file нельзя полагаться только на AppContext.BaseDirectory.
/// </summary>
public static class AppPaths
{
    public static string AppDirectory
    {
        get
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    var dir = Path.GetDirectoryName(processPath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        return dir!;
                }
            }
            catch { }

            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    /// <summary>Служебные файлы: ffmpeg, python-env, worker, лицензии.</summary>
    public static string BinDirectory
    {
        get
        {
            var bin = Path.Combine(AppDirectory, "bin");
            if (Directory.Exists(bin)) return bin;
            // совместимость со старой раскладкой (всё рядом с exe)
            return AppDirectory;
        }
    }
}
