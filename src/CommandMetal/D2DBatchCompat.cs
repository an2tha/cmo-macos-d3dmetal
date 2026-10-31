using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Keep the emulated D3D11 state attached for one complete Direct2D drawing batch.
public static class D2DBatchCompat
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void BeginFn(IntPtr target);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EndFn(IntPtr target, IntPtr tag1, IntPtr tag2);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateFn(IntPtr factory, IntPtr surface, ref Properties properties, out IntPtr target);
    [StructLayout(LayoutKind.Sequential)]
    struct Properties
    {
        public uint Type, Format, Alpha; public float DpiX, DpiY; public uint Usage, MinLevel;
    }
    sealed class Hooks
    {
        public BeginFn Begin; public EndFn End, Flush;
    }
    static readonly Dictionary<IntPtr, Hooks> Tables = new Dictionary<IntPtr, Hooks>();
    static readonly BeginFn BeginRoot = Begin;
    static readonly EndFn EndRoot = End, FlushRoot = Flush;
    public static string LastError;
    [ThreadStatic] static HashSet<IntPtr> ActiveTargets;
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint old);
    static T Fn<T>(IntPtr p, int slot) where T : class
    {
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), slot * IntPtr.Size), typeof(T));
    }
    static void Patch(IntPtr table, int slot, Delegate callback)
    {
        IntPtr address = IntPtr.Add(table, slot * IntPtr.Size);
        uint old, ignored;
        if (!VirtualProtect(address, (UIntPtr)IntPtr.Size, 0x40, out old))
            throw new System.ComponentModel.Win32Exception();
        try
        {
            Marshal.WriteIntPtr(address, Marshal.GetFunctionPointerForDelegate(callback));
        }
        finally { VirtualProtect(address, (UIntPtr)IntPtr.Size, old, out ignored); }
    }
    static Hooks Get(IntPtr p)
    {
        lock (Tables)
            return Tables[Marshal.ReadIntPtr(p)];
    }
    public static void Install(IntPtr swap)
    {
        IntPtr surface = IntPtr.Zero, f = IntPtr.Zero, target = IntPtr.Zero;
        try
        {
            Guid sid = NativeIds.Surface;
            Marshal.ThrowExceptionForHR(Fn<SurfaceCompat.GetBufferFn>(swap, 9)(swap, 0, ref sid, out surface));
            f = NativeGraphics.CreateFactory();
            {
                var props = new Properties();
                props.Alpha = 1;
                props.DpiX = 96;
                props.DpiY = 96;
                Marshal.ThrowExceptionForHR(Fn<CreateFn>(f, 15)(f, surface, ref props, out target));
                D2DPerf.Install(target);
                IntPtr table = Marshal.ReadIntPtr(target);
                lock (Tables)
                {
                    if (Tables.ContainsKey(table))
                        return;
                    var hooks = new Hooks();
                    hooks.Begin = Fn<BeginFn>(target, 48);
                    hooks.End = Fn<EndFn>(target, 49);
                    hooks.Flush = Fn<EndFn>(target, 42);
                    Tables.Add(table, hooks);
                    Patch(table, 48, BeginRoot);
                    Patch(table, 49, EndRoot);
                    Patch(table, 42, FlushRoot);
                }
            }
        }
        finally { if (target != IntPtr.Zero) Marshal.Release(target); if (f != IntPtr.Zero) Marshal.Release(f); if (surface != IntPtr.Zero) Marshal.Release(surface); }
    }
    static void Error(Exception e)
    {
        try
        {
            LastError = e.ToString();
            Console.Error.WriteLine("D2DBatchCompat: " + LastError);
        }
        catch { }
    }
    static void Begin(IntPtr target)
    {
        try
        {
            Get(target).Begin(target);
            if (ActiveTargets == null)
                ActiveTargets = new HashSet<IntPtr>();
            if (ActiveTargets.Add(target))
                ContextStateCompat.BeginBatch(target);
        }
        catch (Exception e) { Error(e); }
    }
    static int End(IntPtr target, IntPtr tag1, IntPtr tag2)
    {
        int result = unchecked((int)0x80004005);
        try
        {
            result = Get(target).End(target, tag1, tag2);
        }
        catch (Exception e) { Error(e); }
        finally { try { if (ActiveTargets != null && ActiveTargets.Remove(target)) ContextStateCompat.EndBatch(target); } catch (Exception e) { Error(e); } }
        return result;
    }
    static int Flush(IntPtr target, IntPtr tag1, IntPtr tag2)
    {
        int result = unchecked((int)0x80004005);
        try
        {
            result = Get(target).Flush(target, tag1, tag2);
        }
        catch (Exception e) { Error(e); }
        finally { try { if (ActiveTargets != null && ActiveTargets.Contains(target)) { ContextStateCompat.EndBatch(target); ContextStateCompat.BeginBatch(target); } } catch (Exception e) { Error(e); } }
        return result;
    }
}
