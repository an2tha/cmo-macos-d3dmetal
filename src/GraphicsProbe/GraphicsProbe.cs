using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

// A manual Windows/Wine graphics integration test. CI does not claim Metal coverage.
internal static class GraphicsProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Properties
    {
        public uint Type, Format, Alpha;
        public float DpiX, DpiY;
        public uint Usage, MinLevel;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TargetFn(IntPtr f, IntPtr s, ref Properties p, out IntPtr t);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BrushFn(IntPtr t, IntPtr color, IntPtr props, out IntPtr brush);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FillFn(IntPtr t, IntPtr rect, IntPtr brush);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DescFn(IntPtr texture, IntPtr desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void PointerFn(IntPtr self, out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TextureFn(IntPtr d, IntPtr desc, IntPtr data, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyFn(IntPtr c, IntPtr dst, IntPtr src);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapFn(IntPtr c, IntPtr resource, uint sub, uint type, uint flags, IntPtr mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapFn(IntPtr c, IntPtr resource, uint sub);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UIntSetFn(IntPtr c, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UIntGetFn(IntPtr c, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BeginFn(IntPtr t);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ClearFn(IntPtr t, IntPtr color);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EndFn(IntPtr t, IntPtr tag1, IntPtr tag2);
    private static volatile bool StressRunning;

    private static T Fn<T>(IntPtr p, int slot) where T : class
    {
        return NativeGraphics.Method<T>(p, slot);
    }
    private static void Release(IntPtr p)
    {
        if (p != IntPtr.Zero)
            Marshal.Release(p);
    }

    private static IntPtr Context(IntPtr swap)
    {
        IntPtr texture = IntPtr.Zero, device = IntPtr.Zero, context;
        try
        {
            Guid iid = NativeIds.Texture2D;
            Marshal.ThrowExceptionForHR(Fn<SurfaceCompat.GetBufferFn>(swap, 9)(swap, 0, ref iid, out texture));
            Fn<PointerFn>(texture, 3)(texture, out device);
            Fn<PointerFn>(device, 40)(device, out context);
            return context;
        }
        finally { Release(device); Release(texture); }
    }

    private static void Readback(IntPtr swap)
    {
        IntPtr texture = IntPtr.Zero, device = IntPtr.Zero, context = IntPtr.Zero, staging = IntPtr.Zero;
        IntPtr desc = Marshal.AllocHGlobal(44), mapped = Marshal.AllocHGlobal(16);
        bool isMapped = false;
        try
        {
            Guid iid = NativeIds.Texture2D;
            Marshal.ThrowExceptionForHR(Fn<SurfaceCompat.GetBufferFn>(swap, 9)(swap, 0, ref iid, out texture));
            Fn<DescFn>(texture, 10)(texture, desc);
            Marshal.WriteInt32(desc, 8, 1);
            Marshal.WriteInt32(desc, 12, 1);
            Marshal.WriteInt32(desc, 28, 3);
            Marshal.WriteInt32(desc, 32, 0);
            Marshal.WriteInt32(desc, 36, 0x20000);
            Marshal.WriteInt32(desc, 40, 0);
            Fn<PointerFn>(texture, 3)(texture, out device);
            Fn<PointerFn>(device, 40)(device, out context);
            Marshal.ThrowExceptionForHR(Fn<TextureFn>(device, 5)(device, desc, IntPtr.Zero, out staging));
            Fn<CopyFn>(context, 47)(context, staging, texture);
            Marshal.ThrowExceptionForHR(Fn<MapFn>(context, 14)(context, staging, 0, 1, 0, mapped));
            isMapped = true;
            IntPtr bits = Marshal.ReadIntPtr(mapped);
            int stride = Marshal.ReadInt32(mapped, 8);
            byte[] pixel = new byte[4];
            Marshal.Copy(IntPtr.Add(bits, 16 * stride + 16 * 4), pixel, 0, 4);
            Console.WriteLine("Rectangle BGRA: " + BitConverter.ToString(pixel));
            if (pixel[0] > 4 || pixel[1] > 4 || pixel[2] < 250 || pixel[3] < 250)
                throw new InvalidOperationException("Rectangle pixel readback failed.");
            Marshal.Copy(IntPtr.Add(bits, 100 * stride + 100 * 4), pixel, 0, 4);
            Console.WriteLine("Background BGRA: " + BitConverter.ToString(pixel));
            if (pixel[0] < 195 || pixel[1] < 65 || pixel[2] > 25 || pixel[3] < 250)
                throw new InvalidOperationException("Background pixel readback failed.");
        }
        finally
        {
            if (isMapped)
                Fn<UnmapFn>(context, 15)(context, staging, 0);
            Release(staging);
            Release(context);
            Release(device);
            Release(texture);
            Marshal.FreeHGlobal(desc);
            Marshal.FreeHGlobal(mapped);
        }
    }

    private static void AssertTopology(IntPtr context, string operation)
    {
        uint value;
        Fn<UIntGetFn>(context, 83)(context, out value);
        if (value != 3)
            throw new InvalidOperationException("State was not restored after " + operation + ": " + value);
    }

    private static void Draw(IntPtr target, IntPtr context, int draws, bool flush)
    {
        IntPtr color = Marshal.AllocHGlobal(16), rectangle = Marshal.AllocHGlobal(16), brush = IntPtr.Zero;
        try
        {
            Fn<UIntSetFn>(context, 24)(context, 3);
            Fn<BeginFn>(target, 48)(target);
            Marshal.Copy(new float[] { .05f, .3f, .8f, 1 }, 0, color, 4);
            Fn<ClearFn>(target, 47)(target, color);
            Marshal.Copy(new float[] { 1, 0, 0, 1 }, 0, color, 4);
            Marshal.ThrowExceptionForHR(Fn<BrushFn>(target, 8)(target, color, IntPtr.Zero, out brush));
            Marshal.Copy(new float[] { 8, 8, 64, 64 }, 0, rectangle, 4);
            FillFn fill = Fn<FillFn>(target, 17);
            for (int i = 0; i < draws; i++)
            {
                fill(target, rectangle, brush);
                if (flush && i == draws / 2)
                {
                    int result = Fn<EndFn>(target, 42)(target, IntPtr.Zero, IntPtr.Zero);
                    if (result != unchecked((int)0x80004001))
                        Marshal.ThrowExceptionForHR(result);
                    AssertTopology(context, "Flush");
                }
            }
            Marshal.ThrowExceptionForHR(Fn<EndFn>(target, 49)(target, IntPtr.Zero, IntPtr.Zero));
            AssertTopology(context, "EndDraw");
        }
        finally { Release(brush); Marshal.FreeHGlobal(rectangle); Marshal.FreeHGlobal(color); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Thread allocator = null;
        try
        {
            int frames = 1, draws = 1;
            bool flush = false;
            foreach (string arg in args)
            {
                if (arg.StartsWith("--frames="))
                    frames = int.Parse(arg.Substring(9));
                else if (arg.StartsWith("--draws="))
                    draws = int.Parse(arg.Substring(8));
                else if (arg == "--flush")
                    flush = true;
                else if (arg == "--gc-stress")
                    StressRunning = true;
                else
                    throw new ArgumentException("Unknown argument: " + arg);
            }
            if (frames < 1 || frames > 100000 || draws < 1 || draws > 100000)
                throw new ArgumentOutOfRangeException("Use 1..100000 frames and draws.");
            if (StressRunning)
            {
                allocator = new Thread(() =>
                {
                    byte[][] keep = new byte[64][];
                    int i = 0;
                    while (StressRunning)
                    {
                        keep[i++ % 64] = new byte[128 * 1024];
                        if (i % 256 == 0)
                            GC.Collect(2, GCCollectionMode.Forced, false);
                        Thread.Sleep(1);
                    }
                });
                allocator.IsBackground = true;
                allocator.Start();
            }
            using (MetalCompat compat = MetalCompat.Install(true))
            {
                IntPtr factory = IntPtr.Zero, surface = IntPtr.Zero, target = IntPtr.Zero, context = IntPtr.Zero;
                try
                {
                    factory = NativeGraphics.CreateFactory();
                    Guid iid = NativeIds.Surface;
                    Marshal.ThrowExceptionForHR(Fn<SurfaceCompat.GetBufferFn>(compat.SwapChain, 9)(compat.SwapChain, 0, ref iid, out surface));
                    Properties props = new Properties { Alpha = 1, DpiX = 96, DpiY = 96 };
                    Marshal.ThrowExceptionForHR(Fn<TargetFn>(factory, 15)(factory, surface, ref props, out target));
                    context = Context(compat.SwapChain);
                    Stopwatch timer = Stopwatch.StartNew();
                    for (int frame = 0; frame < frames; frame++)
                    {
                        Draw(target, context, draws, flush);
                        if (timer.ElapsedMilliseconds > 45000)
                            throw new TimeoutException("Drawing exceeded 45 seconds.");
                    }
                    Readback(compat.SwapChain);
                    if (ContextStateCompat.LastError != null || D2DBatchCompat.LastError != null)
                        throw new InvalidOperationException(ContextStateCompat.LastError ?? D2DBatchCompat.LastError);
                    if (ContextStateCompat.SwappedStates < 2)
                        throw new InvalidOperationException("The state adapter was not exercised.");
                    compat.Present();
                    Console.WriteLine("PASS: " + frames + " frames, " + draws + " draws/frame; state restoration and pixels checked.");
                }
                finally { Release(context); Release(target); Release(surface); Release(factory); }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { StressRunning = false; if (allocator != null) allocator.Join(2000); }
    }
}
