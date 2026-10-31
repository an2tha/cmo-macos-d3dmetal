using System;
using System.Runtime.InteropServices;

public sealed class MetalCompat : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Rational
    {
        public uint N, D;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Mode
    {
        public uint Width, Height; public Rational Refresh; public uint Format, Scanline, Scaling;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Sample
    {
        public uint Count, Quality;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct SwapDesc
    {
        public Mode Mode; public Sample Sample; public uint Usage, Count; public IntPtr Window; public int Windowed; public uint Effect, Flags;
    }
    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int D3D11CreateDeviceAndSwapChain(IntPtr adapter, uint type, IntPtr software, uint flags, uint[] levels, uint count, uint sdk, ref SwapDesc desc, out IntPtr swap, out IntPtr device, out uint level, out IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int show);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr window);
    IntPtr Window;
    IntPtr Device, Context;
    public IntPtr SwapChain;
    public static MetalCompat Install(bool showProbe)
    {
        var x = new MetalCompat();
        x.Window = CreateWindowExW(0, "STATIC", "D3DMetal compatibility drawing test", 0x00cf0000, 0, 0, 640, 480, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (x.Window == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        if (showProbe)
        {
            ShowWindow(x.Window, 5);
            UpdateWindow(x.Window);
        }
        SwapDesc desc = new SwapDesc();
        desc.Mode.Width = 600;
        desc.Mode.Height = 400;
        desc.Mode.Format = 87;
        desc.Sample.Count = 1;
        desc.Usage = 0x20;
        desc.Count = 2;
        desc.Window = x.Window;
        desc.Windowed = 1;
        uint level;
        int hr = D3D11CreateDeviceAndSwapChain(IntPtr.Zero, 1, IntPtr.Zero, 0x20, new uint[] { 0xb000 }, 1, 7, ref desc, out x.SwapChain, out x.Device, out level, out x.Context);
        if (hr < 0)
        {
            x.Dispose();
            Marshal.ThrowExceptionForHR(hr);
        }
        try
        {
            SurfaceCompat.Install(x.SwapChain);
            ContextStateCompat.Install(x.Device);
            if (Environment.GetEnvironmentVariable("CMO_D2D_BATCHING") != "0")
                D2DBatchCompat.Install(x.SwapChain);
            return x;
        }
        catch { x.Dispose(); throw; }
    }
    public void Present()
    {
        NativeGraphics.Present(SwapChain);
    }
    public void Dispose()
    {
        if (SwapChain != IntPtr.Zero)
        {
            Marshal.Release(SwapChain);
            SwapChain = IntPtr.Zero;
        }
        if (Context != IntPtr.Zero)
        {
            Marshal.Release(Context);
            Context = IntPtr.Zero;
        }
        if (Device != IntPtr.Zero)
        {
            Marshal.Release(Device);
            Device = IntPtr.Zero;
        }
        if (Window != IntPtr.Zero)
        {
            DestroyWindow(Window);
            Window = IntPtr.Zero;
        }
    }
}
