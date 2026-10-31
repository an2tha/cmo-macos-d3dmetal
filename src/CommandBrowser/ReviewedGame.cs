using System;
using System.IO;
using System.Security.Cryptography;

internal static class ReviewedGame
{
    internal const string Sha256 = "53fc37bb0b634adbe46f4ddf3a87c99847d273cc6f1d980ba9796da6389d25fc";

    internal static bool IsKnown(string directory, Action<string> log)
    {
        try
        {
            string hash;
            using (SHA256 sha = SHA256.Create())
            using (Stream stream = File.OpenRead(Path.Combine(directory, "Command.exe")))
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (hash == Sha256)
                return true;
            SafeLog(log, "Game UI compatibility skipped: unreviewed Command.exe hash " + hash);
        }
        catch (Exception error) { SafeLog(log, "Game UI build verification failed: " + error.Message); }
        return false;
    }

    private static void SafeLog(Action<string> log, string message)
    {
        try
        {
            if (log != null)
                log(message);
        }
        catch { }
    }
}
