using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class BrowserBootstrap
{
    private static readonly string LogName = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")
        + "-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-browser.log";

    private static void Log(string message)
    {
        try
        {
            string directory = Environment.GetEnvironmentVariable("CMO_LOG_DIR");
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommandMetal", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, LogName), DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    private static Assembly ResolveDependency(object sender, ResolveEventArgs args)
    {
        if (new AssemblyName(args.Name).Name != "0Harmony")
            return null;
        using (Stream stream = typeof(BrowserBootstrap).Assembly.GetManifestResourceStream("CommandBrowser.0Harmony.dll"))
        {
            if (stream == null)
                return null;
            using (MemoryStream bytes = new MemoryStream())
            {
                stream.CopyTo(bytes);
                return Assembly.Load(bytes.ToArray());
            }
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine("CommandBrowser " + typeof(BrowserBootstrap).Assembly.GetName().Version);
            return 0;
        }
        AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
        try
        {
            if (args.Length == 1 && args[0] == "--self-test")
                return BootstrapSelfTest.Run();
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            Directory.SetCurrentDirectory(directory);
            Log("Starting browser compatibility bootstrap before CommandMetal.exe.");
            // Keep Harmony-dependent methods out of this entry-point's JIT resolution.
            InstallGuard(directory);
            InstallLayout(directory);
            Assembly renderer = Assembly.LoadFrom(Path.Combine(directory, "CommandMetal.exe"));
            MethodInfo entry = renderer.EntryPoint;
            object result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : new object[] { args });
            return result is int ? (int)result : Environment.ExitCode;
        }
        catch (Exception error)
        {
            Log("Browser bootstrap failed: " + error);
            try
            {
                Console.Error.WriteLine(error);
            }
            catch { }
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallLayout(string directory)
    {
        if (Environment.GetEnvironmentVariable("CMO_PANEL_LAYOUT") == "0")
        {
            Log("Sidebar layout fix disabled by environment.");
            return;
        }
        PanelLayout.Install(directory, Log);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallGuard(string directory)
    {
        if (Environment.GetEnvironmentVariable("CMO_WEBVIEW_COMPAT") == "0")
        {
            Log("WebView compatibility guard disabled by environment.");
            return;
        }
        WebViewGuard.Install(Path.Combine(directory, "Microsoft.Web.WebView2.Core.dll"), Log);
    }
}
