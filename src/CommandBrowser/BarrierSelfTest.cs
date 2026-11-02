using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class BarrierSelfTest
{
    [DllImport("kernel32.dll")] private static extern void FlushProcessWriteBuffers();

    // Manual CrossOver test only. This does not load the game or its graphics.
    internal static int Run()
    {
        try
        {
            BarrierCompat.Install(Console.WriteLine);
            Exception failure = null;
            using (ManualResetEvent start = new ManualResetEvent(false))
            {
                Thread[] workers = new Thread[4];
                for (int index = 0; index < workers.Length; index++)
                {
                    workers[index] = new Thread(() => {
                        try
                        {
                            start.WaitOne();
                            for (int count = 0; count < 10000; count++) FlushProcessWriteBuffers();
                        }
                        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
                    });
                    workers[index].IsBackground = true;
                    workers[index].Start();
                }
                Stopwatch timer = Stopwatch.StartNew();
                start.Set();
                foreach (Thread worker in workers)
                    if (!worker.Join(30000)) throw new TimeoutException("Native barrier test did not complete.");
                if (failure != null) throw new InvalidOperationException("A barrier worker failed.", failure);
                if (BarrierCompat.Calls < 40001 || BarrierCompat.MaximumActive != 1 || BarrierCompat.Faults != 0)
                    throw new InvalidOperationException("Native barrier counters failed validation.");
                Console.WriteLine("PASS:40000 original barriers from four threads; maxActive=" + BarrierCompat.MaximumActive
                    + "; faults=" + BarrierCompat.Faults + "; elapsed_ms=" + timer.ElapsedMilliseconds);
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
