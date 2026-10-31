// Opt-in, process-local Direct2D call profiler. Native drawing arguments and operations are forwarded unchanged.
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class D2DPerf
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CMO_D2D_PROFILE") == "1";
    public static string LastError;
    public static int InstalledTargets;
    static readonly object InstallGate = new object();
    static readonly ConcurrentDictionary<IntPtr, Originals> Tables = new ConcurrentDictionary<IntPtr, Originals>();
    static readonly string[] Names = { "DrawGlyphRun", "DrawText", "DrawTextLayout", "DrawGeometry", "FillGeometry", "DrawBitmap", "FillRectangle" };
    static readonly int[] Slots = { 29, 27, 28, 22, 23, 26, 17 };
    static readonly Counter[] Counters = CreateCounters();
    static readonly long[] PreviousCounts = new long[7], PreviousInclusive = new long[7], PreviousExclusive = new long[7];
    static readonly string CsvPath = Diagnostics.PathFor("d2d-profile.csv");
    static readonly int ProcessId = Process.GetCurrentProcess().Id;
    static readonly GlyphFn GlyphRoot = Glyph;
    static readonly TextFn TextRoot = Text;
    static readonly LayoutFn LayoutRoot = Layout;
    static readonly DrawGeometryFn DrawGeometryRoot = DrawGeometry;
    static readonly FillGeometryFn FillGeometryRoot = FillGeometry;
    static readonly BitmapFn BitmapRoot = Bitmap;
    static readonly RectangleFn RectangleRoot = Rectangle;
    static Timer Writer;
    static long PreviousTick;
    static int Writing;
    [ThreadStatic] static Frame[] Frames;
    [ThreadStatic] static int Depth;

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public float X, Y;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GlyphFn(IntPtr self, Point origin, IntPtr run, IntPtr brush, uint measuring);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void TextFn(IntPtr self, IntPtr text, uint length, IntPtr format, IntPtr rectangle, IntPtr brush, uint options, uint measuring);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void LayoutFn(IntPtr self, Point origin, IntPtr layout, IntPtr brush, uint options);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DrawGeometryFn(IntPtr self, IntPtr geometry, IntPtr brush, float width, IntPtr style);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FillGeometryFn(IntPtr self, IntPtr geometry, IntPtr brush, IntPtr opacityBrush);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void BitmapFn(IntPtr self, IntPtr bitmap, IntPtr destination, float opacity, uint interpolation, IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RectangleFn(IntPtr self, IntPtr rectangle, IntPtr brush);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtect(IntPtr pointer, UIntPtr bytes, uint protection, out uint previous);

    sealed class Counter
    {
        public long Count, Inclusive, Exclusive, MaxInclusive;
    }
    struct Frame
    {
        public long Started, ChildTicks;
    }
    sealed class Originals
    {
        public GlyphFn Glyph; public TextFn Text; public LayoutFn Layout;
        public DrawGeometryFn DrawGeometry; public FillGeometryFn FillGeometry; public BitmapFn Bitmap; public RectangleFn Rectangle;
        public readonly IntPtr[] Pointers = new IntPtr[7];
    }
    static Counter[] CreateCounters()
    {
        Counter[] counters = new Counter[7];
        for (int i = 0; i < counters.Length; ++i)
            counters[i] = new Counter();
        return counters;
    }
    static T Read<T>(IntPtr table, int slot) where T : class
    {
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T));
    }
    static void Write(IntPtr address, IntPtr value)
    {
        uint previous, ignored;
        if (!VirtualProtect(address, (UIntPtr)IntPtr.Size, 0x40, out previous))
            throw new System.ComponentModel.Win32Exception();
        try
        {
            Marshal.WriteIntPtr(address, value);
        }
        finally { VirtualProtect(address, (UIntPtr)IntPtr.Size, previous, out ignored); }
    }
    public static void Install(IntPtr renderTarget)
    {
        if (!Enabled || renderTarget == IntPtr.Zero)
            return;
        lock (InstallGate)
        {
            IntPtr table = Marshal.ReadIntPtr(renderTarget);
            if (Tables.ContainsKey(table))
                return;
            Originals original = new Originals();
            for (int i = 0; i < Slots.Length; ++i)
                original.Pointers[i] = Marshal.ReadIntPtr(table, Slots[i] * IntPtr.Size);
            original.Glyph = Read<GlyphFn>(table, 29);
            original.Text = Read<TextFn>(table, 27);
            original.Layout = Read<LayoutFn>(table, 28);
            original.DrawGeometry = Read<DrawGeometryFn>(table, 22);
            original.FillGeometry = Read<FillGeometryFn>(table, 23);
            original.Bitmap = Read<BitmapFn>(table, 26);
            original.Rectangle = Read<RectangleFn>(table, 17);
            if (!Tables.TryAdd(table, original))
                return;
            Delegate[] callbacks = { GlyphRoot, TextRoot, LayoutRoot, DrawGeometryRoot, FillGeometryRoot, BitmapRoot, RectangleRoot };
            int written = 0;
            try
            {
                for (; written < Slots.Length; ++written)
                    Write(IntPtr.Add(table, Slots[written] * IntPtr.Size), Marshal.GetFunctionPointerForDelegate(callbacks[written]));
                Interlocked.Increment(ref InstalledTargets);
                if (Writer == null)
                {
                    PreviousTick = Stopwatch.GetTimestamp();
                    Writer = new Timer(WriteSnapshot, null, 5000, 5000);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                // Keep the original delegates rooted even during rollback for any callback already in flight.
                for (int i = written - 1; i >= 0; --i)
                    Write(IntPtr.Add(table, Slots[i] * IntPtr.Size), original.Pointers[i]);
                throw;
            }
        }
    }
    static Originals Find(IntPtr self)
    {
        Originals original;
        if (!Tables.TryGetValue(Marshal.ReadIntPtr(self), out original))
            throw new InvalidOperationException("Unknown profiled Direct2D vtable.");
        return original;
    }
    static int Enter()
    {
        if (Frames == null)
            Frames = new Frame[16];
        int depth = Depth;
        if (depth == Frames.Length)
            Array.Resize(ref Frames, Frames.Length * 2);
        Frames[depth].ChildTicks = 0;
        Frames[depth].Started = Stopwatch.GetTimestamp();
        Depth = depth + 1;
        return depth;
    }
    static void Leave(int operation, int depth)
    {
        long elapsed = Stopwatch.GetTimestamp() - Frames[depth].Started;
        long exclusive = elapsed - Frames[depth].ChildTicks;
        Depth = depth;
        if (depth != 0)
            Frames[depth - 1].ChildTicks += elapsed;
        Counter counter = Counters[operation];
        Interlocked.Increment(ref counter.Count);
        Interlocked.Add(ref counter.Inclusive, elapsed);
        Interlocked.Add(ref counter.Exclusive, Math.Max(0, exclusive));
        long maximum;
        do
        {
            maximum = Interlocked.Read(ref counter.MaxInclusive);
            if (elapsed <= maximum)
                break;
        }
        while (Interlocked.CompareExchange(ref counter.MaxInclusive, elapsed, maximum) != maximum);
    }
    static void Glyph(IntPtr self, Point origin, IntPtr run, IntPtr brush, uint measuring)
    {
        Originals original = Find(self);
        int depth = Enter();
        try
        {
            original.Glyph(self, origin, run, brush, measuring);
        }
        finally { Leave(0, depth); }
    }
    static void Text(IntPtr self, IntPtr text, uint length, IntPtr format, IntPtr rectangle, IntPtr brush, uint options, uint measuring)
    {
        Originals original = Find(self);
        int depth = Enter();
        try
        {
            original.Text(self, text, length, format, rectangle, brush, options, measuring);
        }
        finally { Leave(1, depth); }
    }
    static void Layout(IntPtr self, Point origin, IntPtr layout, IntPtr brush, uint options)
    {
        Originals original = Find(self);
        int depth = Enter();
        try
        {
            original.Layout(self, origin, layout, brush, options);
        }
        finally { Leave(2, depth); }
    }
    static void DrawGeometry(IntPtr self, IntPtr geometry, IntPtr brush, float width, IntPtr style)
    {
        Originals original = Find(self);
        int depth = Enter();
        try
        {
            original.DrawGeometry(self, geometry, brush, width, style);
        }
        finally { Leave(3, depth); }
    }
    static void FillGeometry(IntPtr self, IntPtr geometry, IntPtr brush, IntPtr opacityBrush)
    {
        Originals original = Find(self);
        int depth = Enter();
        try
        {
            original.FillGeometry(self, geometry, brush, opacityBrush);
        }
        finally { Leave(4, depth); }
    }
    static void Bitmap(IntPtr self, IntPtr bitmap, IntPtr destination, float opacity, uint interpolation, IntPtr source)
    {
        Originals original = Find(self);
        int depth = Enter();
        try
        {
            original.Bitmap(self, bitmap, destination, opacity, interpolation, source);
        }
        finally { Leave(5, depth); }
    }
    static void Rectangle(IntPtr self, IntPtr rectangle, IntPtr brush)
    {
        Originals original = Find(self);
        int depth = Enter();
        try
        {
            original.Rectangle(self, rectangle, brush);
        }
        finally { Leave(6, depth); }
    }

    // Explicit flush makes short isolated probes observable without waiting for the timer.
    public static void Flush()
    {
        if (Enabled)
            WriteSnapshot(null);
    }
    static string Number(double number)
    {
        return number.ToString("F3", CultureInfo.InvariantCulture);
    }
    static void WriteSnapshot(object unused)
    {
        if (Interlocked.CompareExchange(ref Writing, 1, 0) != 0)
            return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (PreviousTick == 0)
                PreviousTick = now;
            double seconds = Math.Max(0, (now - PreviousTick) / (double)Stopwatch.Frequency);
            StringBuilder text = new StringBuilder();
            string utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            for (int i = 0; i < Counters.Length; ++i)
            {
                Counter counter = Counters[i];
                long count = Interlocked.Read(ref counter.Count), inclusive = Interlocked.Read(ref counter.Inclusive), exclusive = Interlocked.Read(ref counter.Exclusive);
                long calls = count - PreviousCounts[i], inc = inclusive - PreviousInclusive[i], exc = exclusive - PreviousExclusive[i];
                double scale = 1000.0 / Stopwatch.Frequency;
                text.Append(utc).Append(',').Append(ProcessId).Append(',').Append(Number(seconds)).Append(',').Append(Names[i]).Append(',').Append(calls).Append(',')
                    .Append(Number(inc * scale)).Append(',').Append(Number(exc * scale)).Append(',')
                    .Append(Number(calls == 0 ? 0 : inc * scale * 1000 / calls)).Append(',').Append(Number(calls == 0 ? 0 : exc * scale * 1000 / calls)).Append(',')
                    .Append(Number(Interlocked.Read(ref counter.MaxInclusive) * scale * 1000)).Append(',').Append(count).AppendLine();
                PreviousCounts[i] = count;
                PreviousInclusive[i] = inclusive;
                PreviousExclusive[i] = exclusive;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(CsvPath));
            if (!File.Exists(CsvPath) || new FileInfo(CsvPath).Length == 0)
                File.AppendAllText(CsvPath, "utc,pid,interval_seconds,operation,interval_count,inclusive_ms,exclusive_ms,mean_inclusive_us,mean_exclusive_us,max_inclusive_us,total_count" + Environment.NewLine);
            File.AppendAllText(CsvPath, text.ToString());
            PreviousTick = now;
        }
        catch (Exception ex) { LastError = ex.ToString(); }
        finally { Interlocked.Exchange(ref Writing, 0); }
    }
}
