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
            if (args.Length == 1 && args[0] == "--barrier-self-test")
                return BarrierSelfTest.Run();
            if (args.Length == 1 && args[0] == "--native-ui-self-test")
                return NativeUiSelfTest.Run();
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            Directory.SetCurrentDirectory(directory);
            Log("Starting browser compatibility bootstrap before CommandMetal.exe.");
            if (Environment.GetEnvironmentVariable("CMO_SERIALIZE_BARRIERS") == "1")
                BarrierCompat.Install(Log);
            // Keep Harmony-dependent methods out of this entry-point's JIT resolution.
            InstallBrowser(directory);
            InstallUiFixes(directory);
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
    private static void InstallUiFixes(string directory)
    {
        if (Environment.GetEnvironmentVariable("CMO_FOCUS_COMPAT") == "1")
            FocusGuard.Install(directory, Log);
        if (Environment.GetEnvironmentVariable("CMO_PANEL_LAYOUT") != "1")
        {
            Log("Experimental sidebar layout requires CMO_PANEL_LAYOUT=1; disabled by default.");
            return;
        }
        PanelLayout.Install(directory, Log);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallBrowser(string directory)
    {
        string mode = Environment.GetEnvironmentVariable("CMO_NATIVE_UI") ?? "0";
        if (mode == "0") { InstallGuard(directory); return; }
        if (mode != "browser" && mode != "all")
            throw new ArgumentException("CMO_NATIVE_UI must be 0, browser, or all.");
        if (!ReviewedGame.IsKnown(directory, Log))
            throw new InvalidOperationException("Native UI requires the reviewed game build.");
        NativeHost.Install(Environment.GetEnvironmentVariable("CMO_NATIVE_HOST"), Log);
        NativeBrowser.Install(directory, Log);
        if (mode == "all") NativeGui.Install();
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
