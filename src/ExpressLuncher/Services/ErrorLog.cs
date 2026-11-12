using System.IO;
using ExpressLuncher.Core;

namespace ExpressLuncher.Services;

/// <summary>Journal des erreurs inattendues (%AppData%\ExpressLuncher\error.log), sans jamais lever.</summary>
public static class ErrorLog
{
    private const long MaxBytes = 512 * 1024;

    public static void Write(Exception exception)
    {
        try
        {
            var file = Path.Combine(AppInfo.DataDirectory, "error.log");
            if (File.Exists(file) && new FileInfo(file).Length > MaxBytes) File.Delete(file);
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignoré
        }
    }
}
