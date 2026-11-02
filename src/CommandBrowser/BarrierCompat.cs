using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

// Exact-version, process-local backport of native barrier serialization.
// The emitted SysV x64 function calls native pthread APIs and the original Wine
// service. There is no managed reverse callback in the CLR suspension path.
internal static class BarrierCompat
{
    private const string NativeHash = "ab32585d767ae631154235861a2e0e8ea61bd09267a2dd341a439e3e8ed071fc";
    private static readonly object Initialization = new object();
    private static IntPtr Data, Code, Copy;
    private static volatile bool Installed;
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationThread(IntPtr thread, int kind, byte[] info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protect);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint previous);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern void FlushProcessWriteBuffers();

    private static IntPtr Add(IntPtr address, int offset) { return IntPtr.Add(address, offset); }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Emitter
    {
        internal readonly List<byte> Bytes = new List<byte>();
        private readonly List<int> Failures = new List<int>();
        internal void Emit(params byte[] bytes) { Bytes.AddRange(bytes); }
        internal void Mov(byte opcode, IntPtr pointer)
        { Emit(0x48, opcode); Bytes.AddRange(BitConverter.GetBytes(pointer.ToInt64())); }
        internal void DataRegister(IntPtr pointer)
        { Emit(0x49, 0xbb); Bytes.AddRange(BitConverter.GetBytes(pointer.ToInt64())); }
        internal void Call(IntPtr function) { Mov(0xb8, function); Emit(0xff, 0xd0); }
        internal void FailIfNonzero()
        {
            Emit(0x85, 0xc0, 0x0f, 0x85);
            Failures.Add(Bytes.Count); Emit(0, 0, 0, 0);
        }
        internal void ResolveFailures()
        {
            int target = Bytes.Count;
            foreach (int offset in Failures)
            {
                byte[] relative = BitConverter.GetBytes(target - offset - 4);
                for (int index = 0; index < 4; index++) Bytes[offset + index] = relative[index];
            }
        }
    }

    private static byte[] Build(IntPtr data, IntPtr original, IntPtr mutexLock, IntPtr mutexUnlock, IntPtr nativeExit)
    {
        Emitter code = new Emitter();
        code.Emit(0x53);                           // push rbx, preserves SysV callee save and aligns stack
        code.DataRegister(data);
        code.Emit(0xf0, 0x49, 0xff, 0x43, 0x40);    // lock inc qword [r11+64], calls
        code.Mov(0xbf, data);                      // movabs rdi, mutex
        code.Call(mutexLock);
        code.FailIfNonzero();
        code.DataRegister(data);
        code.Emit(0x49, 0xff, 0x43, 0x48);          // inc active, protected by native mutex
        code.Emit(0x49, 0x8b, 0x43, 0x48);          // mov rax,[r11+72]
        code.Emit(0x49, 0x3b, 0x43, 0x50);          // cmp rax,[r11+80]
        code.Emit(0x76, 0x04);                      // jbe over max update
        code.Emit(0x49, 0x89, 0x43, 0x50);          // mov [r11+80],rax
        code.Call(original);                       // original NtFlushProcessWriteBuffers, unchanged
        code.Emit(0x89, 0xc3);                      // mov ebx,eax, retain NTSTATUS
        code.DataRegister(data);
        code.Emit(0x49, 0xff, 0x4b, 0x48);          // dec active
        code.Mov(0xbf, data);
        code.Call(mutexUnlock);
        code.FailIfNonzero();
        code.Emit(0x89, 0xd8, 0x5b, 0xc3);          // mov eax,ebx; pop rbx; ret
        code.ResolveFailures();
        code.DataRegister(data);
        code.Emit(0xf0, 0x49, 0xff, 0x43, 0x58);    // lock inc faults
        code.Emit(0xbf, 0x7d, 0, 0, 0);             // native _exit(125), cannot report barrier success after a lock error
        code.Call(nativeExit);
        code.Emit(0x0f, 0x0b);                      // unreachable if _exit obeys its contract
        return code.Bytes.ToArray();
    }

    internal static unsafe void Install(Action<string> log)
    {
        lock (Initialization) InstallCore(log);
    }

    private static unsafe void InstallCore(Action<string> log)
    {
        if (Installed) return;
        Require(IntPtr.Size == 8, "Barrier compatibility requires x64.");
        string root = Environment.GetEnvironmentVariable("CX_ROOT");
        if (string.IsNullOrEmpty(root)) root = "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver";
        string nativePath = "Z:" + root.Replace('/', '\\').TrimEnd('\\') + "\\lib\\wine\\x86_64-unix\\ntdll.so";
        using (SHA256 hash = SHA256.Create())
        using (Stream stream = File.OpenRead(nativePath))
            Require(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() == NativeHash,
                "Barrier compatibility withheld: unreviewed native Wine module.");
        byte[] basic = new byte[48]; int returned;
        Require(NtQueryInformationThread(new IntPtr(-2), 0, basic, basic.Length, out returned) >= 0,
            "Cannot identify current Wine thread.");
        IntPtr teb = new IntPtr(BitConverter.ToInt64(basic, 8));
        IntPtr tables = Marshal.ReadIntPtr(Add(teb, 0x370));
        IntPtr originalTable = Marshal.ReadIntPtr(tables);
        long limit = Marshal.ReadInt64(Add(tables, 16));
        Require(limit == 259, "Unexpected native service count.");
        IntPtr original = Marshal.ReadIntPtr(Add(originalTable, 0x95 * 8));
        IntPtr nativeBase = Add(original, -0x6bd20);
        Require(tables == Add(nativeBase, 0x84c00)
            && (nativeBase.ToInt64() & 0xfff) == 0
            && Marshal.ReadInt32(nativeBase) == unchecked((int)0xfeedfacf)
            && Marshal.ReadInt32(Add(nativeBase, 4)) == 0x01000007, "Unexpected native Wine image/table identity.");
        IntPtr mutexLock = Add(nativeBase, 0x70436), mutexUnlock = Add(nativeBase, 0x7043c), nativeExit = Add(nativeBase, 0x700e8);
        Require((ushort)Marshal.ReadInt16(mutexLock) == 0x25ff && (ushort)Marshal.ReadInt16(mutexUnlock) == 0x25ff
            && (ushort)Marshal.ReadInt16(nativeExit) == 0x25ff, "Unexpected native import stubs.");
        try
        {
            // Darwin x86_64 pthread_mutex_t is64 bytes. Public SDK's static
            // PTHREAD_MUTEX_INITIALIZER is signature0x32aaaba7 and zero opaque bytes.
            Data = Marshal.AllocHGlobal(128);
            Marshal.Copy(new byte[128], 0, Data, 128);
            Marshal.WriteInt64(Data, 0x32aaaba7);
            byte[] bytes = Build(Data, original, mutexLock, mutexUnlock, nativeExit);
            Code = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
            Require(Code != IntPtr.Zero, "Cannot allocate native barrier code.");
            Marshal.Copy(bytes, 0, Code, bytes.Length);
            uint previous;
            Require(VirtualProtect(Code, (UIntPtr)4096, 0x20, out previous)
                && FlushInstructionCache(new IntPtr(-1), Code, (UIntPtr)(uint)bytes.Length), "Cannot publish native barrier code.");
            Copy = Marshal.AllocHGlobal((int)limit * 8);
            for (int index = 0; index < limit; index++)
                Marshal.WriteIntPtr(Add(Copy, index * 8), Marshal.ReadIntPtr(Add(originalTable, index * 8)));
            Marshal.WriteIntPtr(Add(Copy, 0x95 * 8), Code);
            for (int index = 0; index < limit; index++)
                Require(index == 0x95 || Marshal.ReadIntPtr(Add(Copy, index * 8)) == Marshal.ReadIntPtr(Add(originalTable, index * 8)),
                    "Unexpected service-table difference.");
            Require((tables.ToInt64() & 7) == 0, "Unaligned service-table pointer.");
            Require(Interlocked.CompareExchange(ref *(long*)tables.ToPointer(), Copy.ToInt64(), originalTable.ToInt64()) == originalTable.ToInt64(),
                "Native service table changed during preparation.");
            Installed = true;
        }
        finally
        {
            if (!Installed)
            {
                if (Code != IntPtr.Zero) VirtualFree(Code, UIntPtr.Zero, 0x8000);
                if (Data != IntPtr.Zero) Marshal.FreeHGlobal(Data);
                if (Copy != IntPtr.Zero) Marshal.FreeHGlobal(Copy);
                Code = Data = Copy = IntPtr.Zero;
            }
        }
        // The native allocation and copied table live until process exit.
        FlushProcessWriteBuffers();
        Require(Calls > 0 && MaximumActive == 1 && Faults == 0,
            "Native barrier is installed but verification failed; abort startup. It was not rolled back.");
        try { log("Native barrier serialization installed; original Mach barrier retained; service0x95 only; calls=" + Calls); }
        catch { /* A diagnostic failure must not undo or misreport native publication. */ }
    }
    internal static long Calls { get { return Installed ? Marshal.ReadInt64(Add(Data, 64)) : 0; } }
    internal static long Active { get { return Installed ? Marshal.ReadInt64(Add(Data, 72)) : 0; } }
    internal static long MaximumActive { get { return Installed ? Marshal.ReadInt64(Add(Data, 80)) : 0; } }
    internal static long Faults { get { return Installed ? Marshal.ReadInt64(Add(Data, 88)) : 0; } }
}
