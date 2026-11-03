using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HarmonyLib;

// Only the reviewed SDK surface is redirected. Proprietary game methods and
// protected IL are not copied, patched, or redistributed.
internal static class NativeBrowser
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ConditionalWeakTable<object, Peer> Objects = new ConditionalWeakTable<object, Peer>();
    private static readonly ConditionalWeakTable<object, Dictionary<string, object>> Arguments = new ConditionalWeakTable<object, Dictionary<string, object>>();
    private static readonly ConcurrentDictionary<int, Peer> Peers = new ConcurrentDictionary<int, Peer>();
    private static Assembly Core;
    private static Type BrowserType;
    private static Harmony Patcher;
    private static int Serial;
    private static readonly bool Trace = Environment.GetEnvironmentVariable("CMO_NATIVE_TRACE") == "1";

    private sealed class Peer
    {
        internal int Id;
        internal Control Control;
        internal object Core, Settings;
        internal Task Initialization;
        internal bool Ready, Disposed;
        internal string Source = "about:blank";
        internal readonly Dictionary<string, Delegate> Events = new Dictionary<string, Delegate>();
        internal readonly Dictionary<string, object> SettingsValues = new Dictionary<string, object>();
        internal System.Windows.Forms.Timer Geometry;
        internal string LastGeometry;
    }

    internal static void Install(string directory, Action<string> log)
    {
        NativeHost.CheckHash(Path.Combine(directory, "Microsoft.Web.WebView2.Core.dll"), "4045f33c0cc195950aa92908bab5f904f41f04f54cb2219c11ddddf0268e1a4d");
        NativeHost.CheckHash(Path.Combine(directory, "Microsoft.Web.WebView2.WinForms.dll"), "b5a667fb7102341d5f4efe46ea772e4a53d1aa26a67573256c7dc42b6559cd80");
        Core = Assembly.LoadFrom(Path.Combine(directory, "Microsoft.Web.WebView2.Core.dll"));
        Assembly forms = Assembly.LoadFrom(Path.Combine(directory, "Microsoft.Web.WebView2.WinForms.dll"));
        NativeHost.CheckHash(Core.Location, "4045f33c0cc195950aa92908bab5f904f41f04f54cb2219c11ddddf0268e1a4d");
        NativeHost.CheckHash(forms.Location, "b5a667fb7102341d5f4efe46ea772e4a53d1aa26a67573256c7dc42b6559cd80");
        if (Core.ManifestModule.ModuleVersionId != new Guid("293b524b-c2df-42fc-b7b5-f77c1008e0b7") || forms.ManifestModule.ModuleVersionId != new Guid("24b73049-0cba-41af-9c44-c33ce23a4c33"))
            throw new InvalidOperationException("Loaded WebView2 modules differ from the reviewed SDK.");
        BrowserType = forms.GetType("Microsoft.Web.WebView2.WinForms.WebView2", true);
        Patcher = new Harmony("cmo.native.browser");
        try
        {
            PatchNames(BrowserType, "EnsureCoreWebView2Async", "get_CoreWebView2", "ExecuteScriptAsync", "NavigateToString", "get_Source", "set_Source", "Reload", "Stop", "GoBack", "GoForward", "get_ZoomFactor", "set_ZoomFactor");
            Type core = Core.GetType("Microsoft.Web.WebView2.Core.CoreWebView2", true);
            PatchNames(core, "get_Settings", "get_Source", "Navigate", "NavigateToString", "ExecuteScriptAsync", "SetVirtualHostNameToFolderMapping", "ClearVirtualHostNameToFolderMapping", "Reload", "Stop", "GoBack", "GoForward", "add_WebMessageReceived", "remove_WebMessageReceived", "add_NavigationStarting", "remove_NavigationStarting", "add_NavigationCompleted", "remove_NavigationCompleted");
            PatchNames(Core.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment", true), "CreateAsync");
            PatchNames(Core.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Settings", true),
                "get_AreDefaultContextMenusEnabled", "set_AreDefaultContextMenusEnabled", "get_IsReputationCheckingRequired", "set_IsReputationCheckingRequired", "get_IsScriptEnabled", "set_IsScriptEnabled", "get_AreDevToolsEnabled", "set_AreDevToolsEnabled", "get_AreBrowserAcceleratorKeysEnabled", "set_AreBrowserAcceleratorKeysEnabled");
            foreach (string name in new[] { "CoreWebView2WebMessageReceivedEventArgs", "CoreWebView2NavigationStartingEventArgs", "CoreWebView2NavigationCompletedEventArgs", "CoreWebView2InitializationCompletedEventArgs" })
                foreach (MethodInfo method in Core.GetType("Microsoft.Web.WebView2.Core." + name, true).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_") || method.Name == "TryGetWebMessageAsString") Patch(method);
            NativeHost.Event += Received;
        }
        catch { Patcher.UnpatchAll("cmo.native.browser"); throw; }
        log("Harmony native browser redirection installed. WebKit views will attach inside Wine windows.");
    }

    private static void PatchNames(Type type, params string[] names)
    {
        foreach (string name in names)
        {
            MethodInfo[] methods = type.GetMethods(All | BindingFlags.Static).Where(m => m.Name == name).ToArray();
            if (methods.Length == 0) throw new MissingMethodException(type.FullName, name);
            foreach (MethodInfo method in methods) Patch(method);
        }
    }

    private static void Patch(MethodInfo method)
    {
        string prefix = method.IsStatic ? "StaticResult" : method.ReturnType == typeof(void) ? "Void" : "Result";
        MethodInfo hook = typeof(NativeBrowser).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static);
        if (hook.IsGenericMethodDefinition) hook = hook.MakeGenericMethod(method.ReturnType);
        Patcher.Patch(method, prefix: new HarmonyMethod(hook));
    }
    private static bool Void(object __instance, MethodBase __originalMethod, object[] __args) { object result; return !Dispatch(__instance, __originalMethod, __args, out result); }
    private static bool Result<T>(object __instance, MethodBase __originalMethod, object[] __args, ref T __result)
    { object result; if (!Dispatch(__instance, __originalMethod, __args, out result)) return true; __result = result == null ? default(T) : (T)result; return false; }
    private static bool StaticResult<T>(MethodBase __originalMethod, object[] __args, ref T __result)
    { object result; if (!Dispatch(null, __originalMethod, __args, out result)) return true; __result = (T)result; return false; }

    private static object New(string type) { return FormatterServices.GetUninitializedObject(Core.GetType("Microsoft.Web.WebView2.Core." + type, true)); }
    private static object Completed(object value)
    { return typeof(Task).GetMethod("FromResult").MakeGenericMethod(value.GetType()).Invoke(null, new[] { value }); }

    private static Peer Get(Control control)
    {
        if (control.IsDisposed) throw new ObjectDisposedException("Browser");
        if (control.InvokeRequired) throw new InvalidOperationException("Browser calls require the game UI thread.");
        return Objects.GetValue(control, _ => {
            var peer = new Peer { Id = Interlocked.Increment(ref Serial), Control = control, Core = New("CoreWebView2"), Settings = New("CoreWebView2Settings") };
            Objects.Add(peer.Core, peer); Objects.Add(peer.Settings, peer); Peers[peer.Id] = peer;
            control.Disposed += (s, e) => Dispose(peer);
            return peer;
        });
    }

    private static bool Dispatch(object instance, MethodBase method, object[] args, out object result)
    {
        result = null; string name = method.Name;
        if (instance == null && name == "CreateAsync") { result = Completed(New("CoreWebView2Environment")); return true; }
        Dictionary<string, object> properties;
        if (instance != null && Arguments.TryGetValue(instance, out properties))
        {
            if (name.StartsWith("set_")) { properties[name.Substring(4)] = args[0]; return true; }
            string key = name == "TryGetWebMessageAsString" ? "String" : name.Substring(4);
            if (name == "TryGetWebMessageAsString" && !properties.ContainsKey(key)) throw new ArgumentException("The web message is not a string.");
            properties.TryGetValue(key, out result);
            Type returnType = ((MethodInfo)method).ReturnType;
            if (result != null && returnType.IsEnum) result = Enum.ToObject(returnType, result);
            return true;
        }
        Peer peer;
        if (instance is Control && BrowserType.IsInstanceOfType(instance)) peer = Get((Control)instance);
        else if (instance == null || !Objects.TryGetValue(instance, out peer)) return false;
        if (peer.Disposed) throw new ObjectDisposedException("Browser");
        if (peer.Control.InvokeRequired) throw new InvalidOperationException("Browser calls require the game UI thread.");
        if (name == "EnsureCoreWebView2Async") { result = Initialize(peer); return true; }
        if (name == "get_CoreWebView2") { result = peer.Ready ? peer.Core : null; return true; }
        if (name == "get_Settings") { result = peer.Settings; return true; }
        if (instance == peer.Settings)
        {
            string key = name.Substring(4);
            if (name.StartsWith("get_")) { object value; result = peer.SettingsValues.TryGetValue(key, out value) ? value : true; return true; }
            peer.SettingsValues[key] = args[0];
            Send(peer, "settings", new Dictionary<string, object> { { key, args[0] } }); return true;
        }
        if (name.StartsWith("add_") || name.StartsWith("remove_"))
        {
            string key = name.Substring(name.IndexOf('_') + 1); Delegate previous; peer.Events.TryGetValue(key, out previous);
            peer.Events[key] = name.StartsWith("add_") ? Delegate.Combine(previous, (Delegate)args[0]) : Delegate.Remove(previous, (Delegate)args[0]); return true;
        }
        switch (name)
        {
            case "get_Source": result = instance is Control ? (object)new Uri(peer.Source) : peer.Source; return true;
            case "set_Source": if (args[0] != null) { peer.Source = ((Uri)args[0]).AbsoluteUri; _ = NavigateWhenReady(peer, peer.Source); } return true;
            case "Navigate": Navigate(peer, (string)args[0]); return true;
            case "NavigateToString": peer.Source = "about:blank"; Send(peer, "html", Values("html", args[0])); return true;
            case "ExecuteScriptAsync": result = Evaluate(peer, (string)args[0]); return true;
            case "SetVirtualHostNameToFolderMapping": MapFolder(peer, (string)args[0], (string)args[1], Convert.ToInt32(args[2])); return true;
            case "ClearVirtualHostNameToFolderMapping": Send(peer, "unmap", Values("host", args[0])); return true;
            case "set_ZoomFactor": peer.SettingsValues["zoom"] = args[0]; if (peer.Ready) Send(peer, "zoom", Values("value", args[0])); return true;
            case "get_ZoomFactor": object zoom; result = peer.SettingsValues.TryGetValue("zoom", out zoom) ? zoom : 1.0; return true;
            case "Reload": case "Stop": case "GoBack": case "GoForward": Send(peer, name.ToLowerInvariant()); return true;
        }
        return false;
    }

    private static Dictionary<string, object> Values(string name, object value) { return new Dictionary<string, object> { { name, value } }; }
    private static void MapFolder(Peer peer, string host, string folder, int access)
    {
        var values = new Dictionary<string, object> { { "host", host }, { "folder", folder }, { "access", access } };
        if (Trace) NativeHost.Logger?.Invoke("Native browser " + peer.Id + " mapping " + NativeHost.Encode(values));
        try
        {
            if (folder == null) throw new ArgumentNullException("folderPath");
            // WebView2 resolves relative folders against the executable, not
            // the working directory. CMO also maps "Root" to the empty path.
            values["path"] = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder));
            Send(peer, "map", values);
        }
        catch (Exception error) { Report(peer, error); throw; }
    }
    private static void Navigate(Peer peer, string target)
    {
        Uri uri;
        bool drivePath = target != null && target.Length >= 3 && char.IsLetter(target[0]) && target[1] == ':' && (target[2] == '\\' || target[2] == '/');
        if (drivePath) uri = new Uri(Path.GetFullPath(target));
        else if (!Uri.TryCreate(target, UriKind.Absolute, out uri)) throw new ArgumentException("The browser address must be an absolute URL or Windows file path.");
        var values = Values("url", uri.AbsoluteUri);
        if (uri.IsFile) {
            if (uri.IsUnc) throw new NotSupportedException("UNC browser paths are not supported by the native host.");
            string path = Path.GetFullPath(uri.LocalPath);
            string directory = Path.GetDirectoryName(path);
            values["filePath"] = path;
            values["fileDirectory"] = directory;
            values["fileOrigin"] = new Uri(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri;
        }
        peer.Source = uri.AbsoluteUri;
        Send(peer, "navigate", values);
    }
    private static async Task NavigateWhenReady(Peer peer, string url)
    { try { await Initialize(peer); if (!peer.Disposed) Navigate(peer, url); } catch (Exception error) { Report(peer, error); } }
    private static Task Initialize(Peer peer)
    {
        if (peer.Initialization != null) return peer.Initialization;
        if (!peer.Control.IsHandleCreated) { IntPtr unused = peer.Control.Handle; }
        peer.Initialization = InitializeCore(peer); return peer.Initialization;
    }
    private static async Task InitializeCore(Peer peer)
    {
        try
        {
            // The game can request initialization while constructing a detached
            // control. Match WebView2's deferred initialization, without creating
            // the game form or waiting synchronously on either UI thread.
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                if (peer.Disposed) throw new ObjectDisposedException("Browser");
                Form owner = peer.Control.FindForm();
                if (owner != null && owner.IsHandleCreated)
                {
                    var geometry = NativeGui.Geometry(peer.Control);
                    geometry["winePrefix"] = Environment.GetEnvironmentVariable("CMO_WINE_PREFIX") ?? "";
                    try { await NativeHost.Request("create", peer.Id, geometry); break; }
                    catch (IOException error)
                    {
                        if (!error.Message.Contains("AppKit window is not available") || DateTime.UtcNow >= deadline) throw;
                    }
                }
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("The browser did not attach to a game window within 15 seconds.");
                await Task.Delay(50);
            }
            if (peer.Disposed) { Send(peer, "dispose"); return; }
            peer.Ready = true;
            NativeHost.Logger?.Invoke("Native browser " + peer.Id + " embedded in " + peer.Control.FindForm()?.GetType().FullName);
            peer.Geometry = new System.Windows.Forms.Timer { Interval = 100 };
            peer.Geometry.Tick += (s, e) => SyncGeometry(peer); peer.Geometry.Start();
            SyncGeometry(peer);
            object zoom; if (peer.SettingsValues.TryGetValue("zoom", out zoom)) Send(peer, "zoom", Values("value", zoom));
            Raise(peer, "CoreWebView2InitializationCompleted", Args("CoreWebView2InitializationCompletedEventArgs", new Dictionary<string, object> { { "IsSuccess", true } }));
        }
        catch (Exception error)
        {
            Raise(peer, "CoreWebView2InitializationCompleted", Args("CoreWebView2InitializationCompletedEventArgs", new Dictionary<string, object> { { "IsSuccess", false }, { "InitializationException", error } }));
            Report(peer, error); throw;
        }
    }
    private static void SyncGeometry(Peer peer)
    {
        if (peer.Disposed) return;
        var geometry = NativeGui.Geometry(peer.Control); string current = NativeHost.Encode(geometry);
        if (current == peer.LastGeometry) return;
        peer.LastGeometry = current; Send(peer, "geometry", geometry);
    }
    private static async Task<string> Evaluate(Peer peer, string script)
    {
        if (!peer.Ready) throw new InvalidOperationException("Initialize the browser before executing JavaScript.");
        var response = await NativeHost.Request("script", peer.Id, Values("script", script));
        return Convert.ToString(response["result"]);
    }
    private static async void Send(Peer peer, string op, Dictionary<string, object> values = null)
    {
        try {
            if (Trace && op != "geometry") {
                string detail = op == "html" ? " characters=" + Convert.ToString(values["html"]).Length : op == "navigate" ? " url=" + Convert.ToString(values["url"]) : "";
                NativeHost.Logger?.Invoke("Native browser " + peer.Id + " request " + op + detail);
            }
            await NativeHost.Request(op, peer.Id, values);
        } catch (Exception error) { Report(peer, error); }
    }
    private static void Report(Peer peer, Exception error)
    { try { NativeHost.Logger?.Invoke("Native browser " + peer.Id + ": " + error); } catch { } }
    private static object Args(string type, Dictionary<string, object> values) { object value = New(type); Arguments.Add(value, values); return value; }
    private static void Raise(Peer peer, string name, object args)
    {
        Delegate handler;
        if (peer.Events.TryGetValue(name, out handler)) handler?.DynamicInvoke(peer.Core, args);
        FieldInfo field = BrowserType.GetField(name, All);
        (field?.GetValue(peer.Control) as Delegate)?.DynamicInvoke(peer.Control, args);
    }
    private static void Received(Dictionary<string, object> message)
    {
        object id; Peer peer;
        if (!message.TryGetValue("browser", out id) || !Peers.TryGetValue(Convert.ToInt32(id), out peer) || peer.Disposed) return;
        try { peer.Control.BeginInvoke((Action)(() => ReceiveOnUI(peer, message))); } catch (InvalidOperationException) { }
    }
    private static void ReceiveOnUI(Peer peer, Dictionary<string, object> message)
    {
        if (peer.Disposed) return;
        try
        {
            string name = Convert.ToString(message["event"]);
            if (name == "navigationStarting")
            {
                if (Trace) NativeHost.Logger?.Invoke("Native browser " + peer.Id + " navigation starting: " + message["url"]);
                var values = new Dictionary<string, object> { { "Uri", message["url"] }, { "Cancel", false }, { "IsUserInitiated", message["user"] }, { "NavigationId", Convert.ToUInt64(message["navigation"]) }, { "IsRedirected", false } };
                try { Raise(peer, "NavigationStarting", Args("CoreWebView2NavigationStartingEventArgs", values)); }
                catch { values["Cancel"] = true; throw; }
                finally { Send(peer, "decision", new Dictionary<string, object> { { "navigation", message["navigation"] }, { "cancel", values["Cancel"] } }); }
            }
            else if (name == "navigationCompleted")
            {
                peer.Source = Convert.ToString(message["url"]);
                if (Trace) NativeHost.Logger?.Invoke("Native browser " + peer.Id + " navigation completed: success=" + message["success"] + " url=" + peer.Source);
                object error; if (message.TryGetValue("error", out error)) Report(peer, new IOException(Convert.ToString(error)));
                Raise(peer, "NavigationCompleted", Args("CoreWebView2NavigationCompletedEventArgs", new Dictionary<string, object> { { "IsSuccess", message["success"] }, { "WebErrorStatus", Convert.ToBoolean(message["success"]) ? 0 : 6 }, { "NavigationId", Convert.ToUInt64(message["navigation"]) }, { "HttpStatusCode", 0 } }));
            }
            else if (name == "message")
            {
                var values = new Dictionary<string, object> { { "WebMessageAsJson", message["json"] }, { "Source", message["url"] } };
                object value; if (message.TryGetValue("string", out value)) values["String"] = value;
                Raise(peer, "WebMessageReceived", Args("CoreWebView2WebMessageReceivedEventArgs", values));
            }
            else if (name == "error") Report(peer, new IOException(Convert.ToString(message["error"])));
        }
        catch (Exception error) { Report(peer, error); }
    }
    private static void Dispose(Peer peer)
    {
        if (peer.Disposed) return;
        peer.Disposed = true; peer.Geometry?.Dispose();
        Peer removed; Peers.TryRemove(peer.Id, out removed);
        Send(peer, "dispose"); peer.Events.Clear();
    }
}
