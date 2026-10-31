using System;
using System.IO;
using System.Reflection;
using System.Runtime;

internal static class CommandMetal
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine("CommandMetal " + typeof(CommandMetal).Assembly.GetName().Version);
            return 0;
        }
        try
        {
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("CommandMetal requires a 64-bit process.");
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string game = Path.Combine(directory, "Command.exe");
            if (!File.Exists(game))
                throw new FileNotFoundException("Install CommandMetal beside your own Command.exe first.", game);
            Directory.SetCurrentDirectory(directory);
            Diagnostics.Start();
            Diagnostics.Write("Starting CommandMetal " + typeof(CommandMetal).Assembly.GetName().Version
                + "; serverGC=" + GCSettings.IsServerGC + "; latency=" + GCSettings.LatencyMode);
            using (MetalCompat.Install(false))
            {
                Diagnostics.Write("Adapters installed; invoking Command.exe.");
                Assembly assembly = Assembly.LoadFrom(game);
                MethodInfo entry = assembly.EntryPoint;
                if (entry == null)
                    throw new MissingMethodException("Command.exe has no managed entry point.");
                object result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : new object[] { args });
                Diagnostics.Write("Command.exe entry point returned.");
                return result is int ? (int)result : Environment.ExitCode;
            }
        }
        catch (Exception error)
        {
            Exception original = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
            Diagnostics.Write("Launcher failure: " + original);
            try
            {
                Console.Error.WriteLine(original);
            }
            catch { }
            return 1;
        }
        finally { Diagnostics.Stop(); }
    }
}
