using System.IO;
using System.Text;

namespace DesktopMediaController.Services;

/// <summary>
/// Last-resort crash log. A widget that fails silently is undebuggable — and because the controller
/// has no visible error surface (no console, no main window chrome), a startup failure would
/// otherwise look like "nothing happened at all".
/// </summary>
internal static class CrashLog
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopMediaController",
        "crash.log");

    internal static void Write(string context, Exception? exception)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ")
                .Append(context).Append(": ")
                .Append(exception?.GetType().Name ?? "Exception").Append(": ")
                .AppendLine(exception?.Message ?? "(no message)");
            if (exception?.StackTrace is { } stack) sb.AppendLine(stack);

            File.AppendAllText(FilePath, sb.ToString());
        }
        catch (Exception)
        {
            // A logger that can itself throw is worse than no logger at all.
        }
    }
}
