namespace Qlyptra.Services;

public static class TempFileCleaner
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qlyptra");
    public static string CacheFolder => Path.Combine(Root, "Cache");
    public static string OutboxFolder => Path.Combine(Root, "Outbox");

    public static void EnsureFolders()
    {
        Directory.CreateDirectory(CacheFolder);
        Directory.CreateDirectory(OutboxFolder);
    }

    public static void CleanOldFiles()
    {
        try
        {
            EnsureFolders();
            foreach (var folder in new[] { CacheFolder, OutboxFolder })
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(24))
                            File.Delete(file);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }
}
