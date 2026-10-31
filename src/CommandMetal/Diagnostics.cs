using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;

internal static class Diagnostics
{
    private static readonly object Gate = new object();
    private static readonly string Session = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)
        + "-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
    [ThreadStatic] private static bool Reporting;
    private static Timer Heartbeat;

    internal static string PathFor(string name)
    {
        string directory = Environment.GetEnvironmentVariable("CMO_LOG_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommandMetal", "logs");
        return Path.Combine(directory, Session + "-" + name);
    }

    internal static void Write(string message)
    {
        Append("runtime.log", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
    }

    private static void Append(string name, string text)
    {
        try
        {
            lock (Gate)
            {
                string path = PathFor(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, text);
            }
        }
        catch { /* Diagnostics must not replace a game or graphics exception. */ }
    }

    internal static void Start()
    {
        if (Environment.GetEnvironmentVariable("CMO_TRACE_EXCEPTIONS") == "1")
            AppDomain.CurrentDomain.FirstChanceException += OnException;
        if (Environment.GetEnvironmentVariable("CMO_DIAGNOSTICS") != "1")
            return;
        Append("stats.csv", "utc,gc0,gc1,gc2,managed_bytes,states,swaps,swap_entered,swap_completed,batches,skipped_swaps" + Environment.NewLine);
        Heartbeat = new Timer(Snapshot, null, 1000, 1000);
    }

    internal static void Stop()
    {
        if (Heartbeat != null)
            Heartbeat.Dispose();
        AppDomain.CurrentDomain.FirstChanceException -= OnException;
    }

    private static void Snapshot(object ignored)
    {
        try
        {
            Append("stats.csv", string.Join(",", new object[] {
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalMemory(false),
                ContextStateCompat.CreatedStates, ContextStateCompat.SwappedStates,
                Interlocked.Read(ref ContextStateCompat.SwapEntered), Interlocked.Read(ref ContextStateCompat.SwapCompleted),
                Interlocked.Read(ref ContextStateCompat.BatchEnter), Interlocked.Read(ref ContextStateCompat.BatchSwapsSkipped)
            }) + Environment.NewLine);
        }
        catch { }
    }

    private static void OnException(object sender, FirstChanceExceptionEventArgs args)
    {
        if (Reporting)
            return;
        Reporting = true;
        try
        {
            Exception error = args.Exception;
            if (error is System.Runtime.InteropServices.COMException || error is System.ComponentModel.Win32Exception
                || error is InvalidOperationException || error is TypeInitializationException)
                Write(error + Environment.NewLine + new StackTrace(true));
        }
        catch { }
        finally { Reporting = false; }
    }
}
