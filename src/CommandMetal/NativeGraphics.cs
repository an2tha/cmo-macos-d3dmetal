using System;
using System.Runtime.InteropServices;

// Interface identities from the Windows SDK, not from a game's managed wrapper.
internal static class NativeIds
{
    internal static readonly Guid Device = new Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    internal static readonly Guid Device1 = new Guid("a04bfb29-08ef-43d6-a49c-a9bdbdcbe686");
    internal static readonly Guid DeviceChild = new Guid("1841e5c8-16b0-489b-bcc8-44cfb0d5deae");
    internal static readonly Guid ContextState = new Guid("5c1e0d8a-7c23-48f9-8c59-a92958ceff11");
    internal static readonly Guid Surface = new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    internal static readonly Guid Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    internal static readonly Guid Factory1 = new Guid("bb12d362-daee-4b9a-aa1d-14ba401cfa1f");
}

internal static class NativeGraphics
{
    [DllImport("d2d1.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int D2D1CreateFactory(uint kind, ref Guid iid, IntPtr options, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentFn(IntPtr swapChain, uint interval, uint flags);

    internal static T Method<T>(IntPtr instance, int slot) where T : class
    {
        IntPtr address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), checked(slot * IntPtr.Size));
        return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
    }

    // Single-threaded factory, matching the original adapter's factory choice.
    internal static IntPtr CreateFactory()
    {
        Guid iid = NativeIds.Factory1;
        IntPtr factory;
        Marshal.ThrowExceptionForHR(D2D1CreateFactory(0, ref iid, IntPtr.Zero, out factory));
        if (factory == IntPtr.Zero)
            throw new InvalidOperationException("Direct2D returned a null factory.");
        return factory;
    }

    internal static void Present(IntPtr swapChain)
    {
        Marshal.ThrowExceptionForHR(Method<PresentFn>(swapChain, 8)(swapChain, 0, 0));
    }
}
