using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;

internal static class WebViewGuard
{
    private const string ExpectedHash = "4045f33c0cc195950aa92908bab5f904f41f04f54cb2219c11ddddf0268e1a4d";
    private static readonly Guid ExpectedMvid = new Guid("293b524b-c2df-42fc-b7b5-f77c1008e0b7");
    private const int NoInterface = unchecked((int)0x80004002);
    private static readonly Guid Settings8 = new Guid("9e6b0e8f-86ad-4e81-8147-a9b5edb68650");
    private static FieldInfo RawField;
    private static Action<string> Logger;
    private static Harmony Patcher;
    internal static long Skipped;

    internal static bool Install(string sdkPath, Action<string> log)
    {
        Logger = log;
        try
        {
            string hash;
            using (SHA256 sha = SHA256.Create())
            using (Stream stream = File.OpenRead(sdkPath))
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (hash != ExpectedHash)
            {
                Write("WebView guard not applied: SDK hash is outside the validated target: " + hash);
                return false;
            }
            Assembly sdk = Assembly.LoadFrom(sdkPath);
            if (sdk.ManifestModule.ModuleVersionId != ExpectedMvid || string.IsNullOrEmpty(sdk.Location))
                throw new InvalidOperationException("The loaded WebView2 SDK differs from the validated module.");
            using (SHA256 sha = SHA256.Create())
            using (Stream stream = File.OpenRead(sdk.Location))
                if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != ExpectedHash)
                    throw new InvalidOperationException("The loaded SDK file has an unexpected hash.");
            Type settings = sdk.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Settings", true);
            RawField = settings.GetField("_rawNative", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo setter = settings.GetProperty("IsReputationCheckingRequired").GetSetMethod();
            if (RawField == null || RawField.FieldType != typeof(object) || setter == null)
                throw new MissingMemberException("Unexpected WebView2 Settings layout.");
            Patcher = new Harmony("cmo.webview.settings8-compat");
            Patcher.Patch(setter, prefix: new HarmonyMethod(typeof(WebViewGuard).GetMethod("BeforeSetter", BindingFlags.Static | BindingFlags.NonPublic)));
            Write("WebView Settings8 guard installed for " + sdk.FullName);
            return true;
        }
        catch (Exception error)
        {
            Write("WebView guard installation failed; original SDK behavior retained: " + error);
            return false;
        }
    }

    private static bool BeforeSetter(object __instance, bool __0)
    {
        IntPtr unknown = IntPtr.Zero, supported = IntPtr.Zero;
        int result = 0;
        try
        {
            object raw = RawField.GetValue(__instance);
            if (raw == null || !Marshal.IsComObject(raw))
                return true;
            unknown = Marshal.GetIUnknownForObject(raw);
            Guid iid = Settings8;
            result = Marshal.QueryInterface(unknown, ref iid, out supported);
            if (result != NoInterface)
                return true;
            Interlocked.Increment(ref Skipped);
            Write("Settings8 E_NOINTERFACE: skipped optional reputation setter (requested=" + __0 + "); browser defaults retained.");
            return false;
        }
        catch (Exception error)
        {
            Write("WebView capability check failed; forwarding original setter: " + error);
            return true;
        }
        finally
        {
            // The raw RCW is borrowed. Release only references acquired above.
            if (supported != IntPtr.Zero && result >= 0)
                ReleaseOwned(supported);
            if (unknown != IntPtr.Zero)
                ReleaseOwned(unknown);
        }
    }

    private static void ReleaseOwned(IntPtr pointer)
    {
        try
        {
            Marshal.Release(pointer);
        }
        catch (Exception error) { Write("WebView capability reference release failed: " + error); }
    }

    private static void Write(string message)
    {
        try
        {
            if (Logger != null)
                Logger(message);
        }
        catch { }
    }
}
