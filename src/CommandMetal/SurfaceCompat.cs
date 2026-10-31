using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Experimental, process-local DXGI surface adapter. No installed DLL is changed.
public static class SurfaceCompat
{
    const int ENoInterface = unchecked((int)0x80004002), EFail = unchecked((int)0x80004005);
    static readonly Guid SurfaceId = new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    static readonly Guid TextureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    static readonly Guid UnknownId = new Guid("00000000-0000-0000-c000-000000000046");
    static readonly Guid ObjectId = new Guid("aec22fb8-76f3-4639-9be0-28eb43a67a2e");
    static readonly Guid SubObjectId = new Guid("3d3e0379-f9de-4d58-bb6c-18d62992f1a6");
    static readonly Dictionary<IntPtr, Surface> Surfaces = new Dictionary<IntPtr, Surface>();
    static readonly Dictionary<IntPtr, GetBufferFn> Originals = new Dictionary<IntPtr, GetBufferFn>();
    static readonly List<Delegate> Roots = new List<Delegate>();
    static readonly GetBufferFn BufferHook = GetBuffer;
    static readonly IntPtr SurfaceTable = BuildTable();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetBufferFn(IntPtr self, uint index, ref Guid iid, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QueryFn(IntPtr self, ref Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint RefFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DataFn(IntPtr self, ref Guid guid, uint size, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DataInterfaceFn(IntPtr self, ref Guid guid, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDataFn(IntPtr self, ref Guid guid, ref uint size, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDeviceFn(IntPtr self, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void TextureDescFn(IntPtr self, out TextureDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SurfaceDescFn(IntPtr self, out SurfaceDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, out MappedRect mapped, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int UnmapFn(IntPtr self);
    [StructLayout(LayoutKind.Sequential)]
    struct TextureDesc
    {
        public uint Width, Height, Mips, Array, Format, Samples, Quality, Usage, Bind, Cpu, Misc;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct SurfaceDesc
    {
        public uint Width, Height, Format, Samples, Quality;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MappedRect
    {
        public int Pitch; public IntPtr Bits;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtect(IntPtr p, UIntPtr size, uint protection, out uint old);

    sealed class Surface
    {
        public IntPtr Pointer, Texture; public int Refs = 1;
    }
    static T Method<T>(IntPtr obj, int slot) where T : class
    {
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T));
    }
    static IntPtr Root(Delegate d)
    {
        Roots.Add(d);
        return Marshal.GetFunctionPointerForDelegate(d);
    }
    static IntPtr BuildTable()
    {
        Delegate[] methods = { new QueryFn(Query), new RefFn(AddRef), new RefFn(Release), new DataFn(SetData), new DataInterfaceFn(SetInterface), new GetDataFn(GetData), new QueryFn(GetParent), new QueryFn(GetDevice), new SurfaceDescFn(GetDesc), new MapFn(Map), new UnmapFn(Unmap) };
        IntPtr p = Marshal.AllocHGlobal(methods.Length * IntPtr.Size);
        for (int i = 0; i < methods.Length; i++)
            Marshal.WriteIntPtr(p, i * IntPtr.Size, Root(methods[i]));
        return p;
    }
    public static void Install(IntPtr swapChain)
    {
        IntPtr table = Marshal.ReadIntPtr(swapChain);
        lock (Originals)
        {
            if (Originals.ContainsKey(table))
                return;
            IntPtr slot = IntPtr.Add(table, 9 * IntPtr.Size);
            uint old;
            GetBufferFn original = Method<GetBufferFn>(swapChain, 9);
            if (!VirtualProtect(slot, (UIntPtr)IntPtr.Size, 0x40, out old))
                throw new InvalidOperationException("Cannot protect GetBuffer slot");
            Originals.Add(table, original);
            Marshal.WriteIntPtr(slot, Marshal.GetFunctionPointerForDelegate(BufferHook));
            uint ignored;
            VirtualProtect(slot, (UIntPtr)IntPtr.Size, old, out ignored);
        }
    }
    static int GetBuffer(IntPtr self, uint index, ref Guid iid, out IntPtr buffer)
    {
        buffer = IntPtr.Zero;
        try
        {
            GetBufferFn original;
            lock (Originals)
                original = Originals[Marshal.ReadIntPtr(self)];
            if (iid != SurfaceId)
                return original(self, index, ref iid, out buffer);
            Guid textureId = TextureId;
            IntPtr texture;
            int hr = original(self, index, ref textureId, out texture);
            if (hr < 0)
                return hr;
            var s = new Surface();
            s.Texture = texture;
            s.Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(s.Pointer, SurfaceTable);
            lock (Surfaces)
                Surfaces.Add(s.Pointer, s);
            buffer = s.Pointer;
            return 0;
        }
        catch { return EFail; }
    }
    static Surface Find(IntPtr p)
    {
        lock (Surfaces)
            return Surfaces[p];
    }
    static int Query(IntPtr self, ref Guid iid, out IntPtr value)
    {
        value = IntPtr.Zero;
        try
        {
            Surface s = Find(self);
            if (iid == UnknownId || iid == SurfaceId || iid == ObjectId || iid == SubObjectId)
            {
                AddRef(self);
                value = self;
                return 0;
            }
            return Marshal.QueryInterface(s.Texture, ref iid, out value);
        }
        catch { return ENoInterface; }
    }
    static uint AddRef(IntPtr self)
    {
        try
        {
            return (uint)Interlocked.Increment(ref Find(self).Refs);
        }
        catch { return 0; }
    }
    static uint Release(IntPtr self)
    {
        try
        {
            Surface s = Find(self);
            int n = Interlocked.Decrement(ref s.Refs);
            if (n == 0)
            {
                lock (Surfaces)
                    Surfaces.Remove(self);
                Marshal.Release(s.Texture);
                Marshal.FreeHGlobal(self);
            }
            return (uint)n;
        }
        catch { return 0; }
    }
    static int SetData(IntPtr self, ref Guid guid, uint size, IntPtr data)
    {
        try
        {
            Surface s = Find(self);
            return Method<DataFn>(s.Texture, 5)(s.Texture, ref guid, size, data);
        }
        catch { return EFail; }
    }
    static int SetInterface(IntPtr self, ref Guid guid, IntPtr data)
    {
        try
        {
            Surface s = Find(self);
            return Method<DataInterfaceFn>(s.Texture, 6)(s.Texture, ref guid, data);
        }
        catch { return EFail; }
    }
    static int GetData(IntPtr self, ref Guid guid, ref uint size, IntPtr data)
    {
        try
        {
            Surface s = Find(self);
            return Method<GetDataFn>(s.Texture, 4)(s.Texture, ref guid, ref size, data);
        }
        catch { return EFail; }
    }
    static int GetParent(IntPtr self, ref Guid guid, out IntPtr value)
    {
        value = IntPtr.Zero;
        return ENoInterface;
    }
    static int GetDevice(IntPtr self, ref Guid guid, out IntPtr value)
    {
        value = IntPtr.Zero;
        IntPtr device = IntPtr.Zero;
        try
        {
            Surface s = Find(self);
            Method<GetDeviceFn>(s.Texture, 3)(s.Texture, out device);
            return Marshal.QueryInterface(device, ref guid, out value);
        }
        catch { return EFail; }
        finally { if (device != IntPtr.Zero) Marshal.Release(device); }
    }
    static int GetDesc(IntPtr self, out SurfaceDesc desc)
    {
        desc = new SurfaceDesc();
        try
        {
            Surface s = Find(self);
            TextureDesc t;
            Method<TextureDescFn>(s.Texture, 10)(s.Texture, out t);
            desc.Width = t.Width;
            desc.Height = t.Height;
            desc.Format = t.Format;
            desc.Samples = t.Samples;
            desc.Quality = t.Quality;
            return 0;
        }
        catch { return EFail; }
    }
    // A swap-chain back buffer is not a CPU-mappable staging resource.
    static int Map(IntPtr self, out MappedRect mapped, uint flags)
    {
        mapped = new MappedRect();
        return unchecked((int)0x887a0001);
    }
    static int Unmap(IntPtr self)
    {
        return unchecked((int)0x887a0001);
    }
}
