using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Wine performs the Windows-to-Unix ABI transition. No AppKit calls or managed
// callbacks run in that transition. The native library queues work to AppKit.
internal static class NativeHost
{
    private const string NativeHash = "ab32585d767ae631154235861a2e0e8ea61bd09267a2dd341a439e3e8ed071fc";
    private const string MacHash = "a46e1c63a5096210626d64ef6c62ddea7632f95dbcf437b1a3bf006d43dcf532";
    internal const int MaximumFrame = 8 * 1024 * 1024;
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> Pending = new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
    private static IntPtr Table, Code;
    private static int Serial;
    private static System.Threading.Timer Poller;
    private static int Polling;
    internal static event Action<Dictionary<string, object>> Event;
    internal static Action<string> Logger;
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationThread(IntPtr thread, int kind, byte[] info, int length, out int returned);
    [DllImport("ntdll.dll", EntryPoint="__wine_unix_call")] private static extern int UnixCall(IntPtr table, uint index, IntPtr args);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protect);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);
    [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    internal static string Encode(object value) { return new JavaScriptSerializer { MaxJsonLength = MaximumFrame }.Serialize(value); }
    internal static string WindowsPath(string unix) { return "Z:" + unix.Replace('/', '\\'); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static void CheckHash(string path, string expected)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            Require(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() == expected,
                "Native UI is unavailable for this unreviewed file: " + Path.GetFileName(path));
    }

    internal static void Install(string nativePath, Action<string> log)
    {
        if (Table != IntPtr.Zero) return;
        Logger = log;
        Require(IntPtr.Size == 8 && !string.IsNullOrEmpty(nativePath) && nativePath.StartsWith("/"), "CMO_NATIVE_HOST must be an absolute macOS library path.");
        Require(File.Exists(WindowsPath(nativePath)), "Build the native UI library first: " + nativePath);
        string root = Environment.GetEnvironmentVariable("CX_ROOT") ?? "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver";
        CheckHash(WindowsPath(root + "/lib/wine/x86_64-unix/ntdll.so"), NativeHash);
        CheckHash(WindowsPath(root + "/lib/wine/x86_64-unix/winemac.so"), MacHash);
        byte[] basic = new byte[48]; int returned;
        Require(NtQueryInformationThread(new IntPtr(-2), 0, basic, basic.Length, out returned) >= 0, "Cannot identify Wine thread.");
        IntPtr teb = new IntPtr(BitConverter.ToInt64(basic, 8));
        IntPtr tables = Marshal.ReadIntPtr(teb, 0x370);
        IntPtr services = Marshal.ReadIntPtr(tables);
        Require(Marshal.ReadInt64(tables, 16) == 259, "Unexpected Wine service count.");
        IntPtr nativeBase = IntPtr.Add(BarrierCompat.UnwrapBarrier(Marshal.ReadIntPtr(services, 0x95 * 8)), -0x6bd20);
        Require(tables == IntPtr.Add(nativeBase, 0x84c00) && Marshal.ReadInt32(nativeBase) == unchecked((int)0xfeedfacf)
            && Marshal.ReadInt32(nativeBase, 4) == 0x01000007, "Unexpected Wine image identity.");
        IntPtr dlopen = IntPtr.Add(nativeBase, 0x70172), dlsym = IntPtr.Add(nativeBase, 0x70178);
        Require((ushort)Marshal.ReadInt16(dlopen) == 0x25ff && (ushort)Marshal.ReadInt16(dlsym) == 0x25ff, "Unexpected Darwin loader imports.");
        // SysV entry(void *args): two pointer-sized arguments, output at +16.
        // Called by Wine's own Unix dispatcher, with its normal native stack.
        var bytes = new List<byte>();
        foreach (IntPtr target in new[] { dlopen, dlsym })
        {
            while (bytes.Count % 64 != 0) bytes.Add(0x90);
            bytes.AddRange(new byte[] { 0x53, 0x48, 0x89, 0xfb, 0x48, 0x8b, 0x3b, 0x48, 0x8b, 0x73, 0x08, 0x48, 0xb8 });
            bytes.AddRange(BitConverter.GetBytes(target.ToInt64()));
            bytes.AddRange(new byte[] { 0xff, 0xd0, 0x48, 0x89, 0x43, 0x10, 0x31, 0xc0, 0x5b, 0xc3 });
        }
        Code = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
        Require(Code != IntPtr.Zero, "Cannot allocate native loader bridge.");
        Marshal.Copy(bytes.ToArray(), 0, Code, bytes.Count);
        uint previous;
        Require(VirtualProtect(Code, (UIntPtr)4096, 0x20, out previous) && FlushInstructionCache(new IntPtr(-1), Code, (UIntPtr)bytes.Count), "Cannot publish native loader bridge.");
        IntPtr loaderTable = Marshal.AllocHGlobal(16);
        Marshal.WriteIntPtr(loaderTable, Code); Marshal.WriteIntPtr(loaderTable, 8, IntPtr.Add(Code, 64));
        IntPtr path = Utf8(nativePath), symbol = Utf8("cmo_native_call"), args = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteIntPtr(args, path); Marshal.WriteIntPtr(args, 8, new IntPtr(2)); Marshal.WriteIntPtr(args, 16, IntPtr.Zero);
            Require(UnixCall(loaderTable, 0, args) == 0, "Wine native loader call failed.");
            IntPtr library = Marshal.ReadIntPtr(args, 16);
            Require(library != IntPtr.Zero, "Darwin could not load the native UI library. Verify its architecture and dependencies.");
            Marshal.WriteIntPtr(args, library); Marshal.WriteIntPtr(args, 8, symbol); Marshal.WriteIntPtr(args, 16, IntPtr.Zero);
            Require(UnixCall(loaderTable, 1, args) == 0, "Wine native symbol lookup failed.");
            IntPtr entry = Marshal.ReadIntPtr(args, 16);
            Require(entry != IntPtr.Zero, "Native UI library does not export cmo_native_call.");
            Table = Marshal.AllocHGlobal(8); Marshal.WriteIntPtr(Table, entry);
        }
        finally { Marshal.FreeHGlobal(path); Marshal.FreeHGlobal(symbol); Marshal.FreeHGlobal(args); Marshal.FreeHGlobal(loaderTable); }
        // Library and entry table remain loaded until process exit.
        Poller = new System.Threading.Timer(_ => Poll(), null, 0, 16);
        log("Native AppKit bridge loaded in the Wine process without modifying Wine's service table.");
    }

    private static IntPtr Utf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, pointer, bytes.Length); return pointer;
    }

    // ABI: uint64 operation, pointer bytes, uint64 capacity/length, uint64 used.
    private static long Call(long operation, IntPtr bytes, int size)
    {
        IntPtr args = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt64(args, operation); Marshal.WriteIntPtr(args, 8, bytes);
            Marshal.WriteInt64(args, 16, size); Marshal.WriteInt64(args, 24, 0);
            int status = UnixCall(Table, 0, args);
            if (status != 0) throw new IOException("Native UI call failed: 0x" + status.ToString("x8"));
            return Marshal.ReadInt64(args, 24);
        }
        finally { Marshal.FreeHGlobal(args); }
    }

    internal static Task<Dictionary<string, object>> Request(string operation, int browser, Dictionary<string, object> values = null)
    {
        var message = values == null ? new Dictionary<string, object>() : new Dictionary<string, object>(values);
        int id = Interlocked.Increment(ref Serial);
        message["id"] = id; message["op"] = operation; message["browser"] = browser;
        byte[] bytes = Encoding.UTF8.GetBytes(Encode(message));
        if (bytes.Length > MaximumFrame) throw new IOException("The native UI request is too large.");
        var completion = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = completion;
        var timeout = new System.Threading.Timer(_ => {
            TaskCompletionSource<Dictionary<string, object>> expired;
            if (Pending.TryRemove(id, out expired)) expired.TrySetException(new TimeoutException("Native UI request timed out: " + operation));
        }, null, 15000, Timeout.Infinite);
        completion.Task.ContinueWith(_ => timeout.Dispose(), TaskScheduler.Default);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, buffer, bytes.Length); Call(0, buffer, bytes.Length); }
        catch (Exception error) { TaskCompletionSource<Dictionary<string, object>> removed; Pending.TryRemove(id, out removed); completion.TrySetException(error); }
        finally { Marshal.FreeHGlobal(buffer); }
        return completion.Task;
    }

    private static void Poll()
    {
        if (Interlocked.Exchange(ref Polling, 1) != 0) return;
        try
        {
            for (int index = 0; index < 128; index++)
            {
                int size = checked((int)Call(1, IntPtr.Zero, 0));
                if (size == 0) break;
                if (size < 0 || size > MaximumFrame) throw new IOException("Native UI reply exceeded the size limit.");
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int used = checked((int)Call(1, buffer, size));
                    if (used > size) throw new IOException("Native UI reply changed during read.");
                    byte[] bytes = new byte[used]; Marshal.Copy(buffer, bytes, 0, used);
                    var message = new JavaScriptSerializer { MaxJsonLength = MaximumFrame }.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes));
                    object reply;
                    if (message.TryGetValue("reply", out reply))
                    {
                        TaskCompletionSource<Dictionary<string, object>> completion;
                        if (!Pending.TryRemove(Convert.ToInt32(reply), out completion)) continue;
                        object error;
                        if (message.TryGetValue("error", out error)) completion.TrySetException(new IOException(Convert.ToString(error)));
                        else completion.TrySetResult(message);
                    }
                    else Event?.Invoke(message);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch (Exception error) { try { Logger?.Invoke("Native UI receive error: " + error); } catch { } }
        finally { Volatile.Write(ref Polling, 0); }
    }
}
