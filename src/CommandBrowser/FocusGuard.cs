using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using HarmonyLib;

// Opt-in containment for the captured MainForm.WndProc -> Activate native wait.
// This does not replace the game's protected window procedure or change input.
internal static class FocusGuard
{
    private const string Owner = "cmo.mainform.reactive-activation";
    private const int WindowProcedureToken = 0x060014c0;
    private const int BridgeToken = 0x0601373e;
    private const int MaximumFrames = 64;
    private static readonly object Lifecycle = new object();
    private static readonly MethodInfo ActivateMethod = typeof(Form).GetMethod("Activate", BindingFlags.Public | BindingFlags.Instance);
    private static volatile CallerPolicy Policy;
    private static volatile CallerPolicy TestPolicy;
    private static Harmony Patcher;
    private static Action<string> Logger;
    private static string ExpectedPath;
    private static string PendingError;
    private static Form ReportingForm;
    private static System.Threading.Timer Reporter;
    private static volatile bool Stopped;
    private static long Skips;
    private static long Reported;
    private static int Reporting;
    private static int TestEntries, TestOriginals, TestSkips;

    internal static void Install(string gameDirectory, Action<string> log)
    {
        lock (Lifecycle)
        {
            if (Patcher != null)
                return;
            Logger = log;
            try
            {
                if (!ReviewedGame.IsKnown(gameDirectory, log))
                    return;
                ExpectedPath = Path.GetFullPath(Path.Combine(gameDirectory, "Command.exe"));
                Stopped = false;
                AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    TryResolvePolicy(assembly);
                Patcher = new Harmony(Owner);
                Patcher.Patch(ActivateMethod,
                    prefix: new HarmonyMethod(typeof(FocusGuard).GetMethod("BeforeActivate", BindingFlags.Static | BindingFlags.NonPublic)));
                Patcher.Patch(typeof(Form).GetMethod("OnShown", BindingFlags.Instance | BindingFlags.NonPublic),
                    postfix: new HarmonyMethod(typeof(FocusGuard).GetMethod("AfterShown", BindingFlags.Static | BindingFlags.NonPublic)));
                Log("Opt-in activation guard installed; waiting for the reviewed MainForm.");
            }
            catch (Exception error)
            {
                // Do not leave a partial installation active after setup failure.
                Policy = null;
                Stopped = true;
                AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
                try
                {
                    if (Patcher != null)
                        Patcher.UnpatchAll(Owner);
                }
                catch { }
                Patcher = null;
                Log("Activation guard unavailable; original behavior retained: " + error);
            }
        }
    }

    private static void AssemblyLoaded(object sender, AssemblyLoadEventArgs args)
    {
        TryResolvePolicy(args.LoadedAssembly);
    }

    // Resolve metadata only, without loading Command.exe ahead of its own startup.
    private static void TryResolvePolicy(Assembly assembly)
    {
        try
        {
            if (Stopped || Policy != null || assembly.IsDynamic ||
                !string.Equals(assembly.GetName().Name, "Command", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(assembly.Location), ExpectedPath, StringComparison.OrdinalIgnoreCase))
                return;
            Type main = assembly.GetType("Command.MainForm", false);
            if (main == null || !typeof(Form).IsAssignableFrom(main))
                throw new InvalidOperationException("The reviewed MainForm type is missing.");
            MethodInfo windowProcedure = main.GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, new[] { typeof(Message).MakeByRefType() }, null);
            Type bridge = assembly.GetType("AuDMEk5fYGprYSkbhrAo", false);
            MethodInfo forward = bridge == null ? null : bridge.GetMethod("lfW55dwdZtB", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(object), bridge }, null);
            if (windowProcedure == null || windowProcedure.ReturnType != typeof(void) || windowProcedure.MetadataToken != WindowProcedureToken ||
                windowProcedure.Module.Assembly != assembly || forward == null || forward.ReturnType != typeof(void) ||
                forward.Module.Assembly != assembly || forward.MetadataToken != BridgeToken)
                throw new InvalidOperationException("The reviewed activation caller metadata differs.");
            CallerPolicy resolved = new CallerPolicy(main, windowProcedure, forward);
            lock (Lifecycle)
            {
                if (!Stopped && Policy == null)
                    Policy = resolved;
            }
        }
        catch
        {
            Interlocked.Exchange(ref PendingError, "Reviewed activation caller metadata could not be resolved; original calls retained.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BeforeActivate(Form __instance)
    {
        try
        {
            CallerPolicy policy = Policy;
            if (policy == null || __instance == null || __instance.GetType() != policy.MainType)
                return true;
            // The capture itself is not depth-limited; inspection stops after 64 frames.
            if (!Allow(policy, new StackTrace(1, false).GetFrames()))
            {
                Interlocked.Increment(ref Skips);
                return false;
            }
        }
        catch
        {
            // No logging, native focus calls, handles, or UI dispatch in this prefix.
            Interlocked.Exchange(ref PendingError, "Activation stack inspection failed; original call retained.");
        }
        return true;
    }

    private static bool Allow(CallerPolicy policy, StackFrame[] frames)
    {
        if (frames == null)
            return true;
        for (int i = 0; i < Math.Min(frames.Length, MaximumFrames); i++)
        {
            MethodBase method = frames[i].GetMethod();
            Type caller = method == null ? null : method.DeclaringType;
            if (caller == null || caller.Assembly != policy.MainType.Assembly)
                continue;
            // The isolated fixture shares this assembly with its test prefix.
            // Production's reviewed Command.exe never contains this guard type.
            if (caller == typeof(FocusGuard))
                continue;
            if (SameMethod(method, policy.Bridge))
                continue;
            // A dialog helper before an outer MainForm.WndProc must retain activation.
            return !SameMethod(method, policy.WindowProcedure);
        }
        return true;
    }

    private static bool SameMethod(MethodBase first, MethodBase second)
    {
        return first.Module == second.Module && first.MetadataToken == second.MetadataToken;
    }

    private static void AfterShown(Form __instance)
    {
        try
        {
            CallerPolicy policy = Policy;
            // AssemblyLoad can run before a protected assembly finishes startup.
            // Retry metadata resolution only for an actual MainForm reaching OnShown.
            if (policy == null && !Stopped && __instance.GetType().FullName == "Command.MainForm")
            {
                TryResolvePolicy(__instance.GetType().Assembly);
                policy = Policy;
            }
            if (policy == null || __instance.GetType() != policy.MainType || !__instance.Visible || !__instance.IsHandleCreated)
                return;
            lock (Lifecycle)
            {
                if (Stopped || ReportingForm != null)
                    return;
                ReportingForm = __instance;
                ReportingForm.Disposed += MainDisposed;
                Reporter = new System.Threading.Timer(Report, null, 5000, 5000);
            }
        }
        catch
        {
            Interlocked.Exchange(ref PendingError, "Activation counter reporting could not start.");
        }
    }

    private static void Report(object state)
    {
        // Only counter/error snapshots leave the UI thread. Never access Form here.
        if (Interlocked.CompareExchange(ref Reporting, 1, 0) != 0)
            return;
        try
        {
            long count = Interlocked.Read(ref Skips);
            if (Interlocked.Exchange(ref Reported, count) != count)
                Log("Guarded MainForm message-handler activation calls=" + count);
            string error = Interlocked.Exchange(ref PendingError, null);
            if (error != null)
                Log(error);
        }
        catch { }
        finally { Volatile.Write(ref Reporting, 0); }
    }

    private static void MainDisposed(object sender, EventArgs args)
    {
        try
        {
            lock (Lifecycle)
            {
                Stopped = true;
                Policy = null;
                AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
                if (ReportingForm != null)
                    ReportingForm.Disposed -= MainDisposed;
                ReportingForm = null;
                if (Reporter != null)
                    Reporter.Dispose();
                Reporter = null;
            }
        }
        catch { }
    }

    private static void Log(string message)
    {
        try
        {
            if (Logger != null)
                Logger(message);
        }
        catch { }
    }

    private sealed class CallerPolicy
    {
        internal readonly Type MainType;
        internal readonly MethodInfo WindowProcedure, Bridge;

        internal CallerPolicy(Type main, MethodInfo windowProcedure, MethodInfo bridge)
        {
            MainType = main;
            WindowProcedure = windowProcedure;
            Bridge = bridge;
        }
    }

    // The fixture policy is used only by this temporary self-test prefix. It cannot
    // publish a production Policy or bypass the installation hash/path checks.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BeforeTestActivate(Form __instance)
    {
        Interlocked.Increment(ref TestEntries);
        CallerPolicy policy = TestPolicy;
        if (policy != null && __instance.GetType() == policy.MainType && !Allow(policy, new StackTrace(1, false).GetFrames()))
        {
            Interlocked.Increment(ref TestSkips);
            return false;
        }
        return true;
    }

    private static void AfterTestActivate(bool __runOriginal)
    {
        if (__runOriginal)
            Interlocked.Increment(ref TestOriginals);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void SelfTest()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The focus guard framework self-test requires an STA thread.");
        const string id = "cmo.mainform.reactive-activation.self-test";
        Harmony patcher = new Harmony(id);
        lock (Lifecycle)
        {
            try
            {
                TestEntries = TestOriginals = TestSkips = 0;
                TestPolicy = new CallerPolicy(typeof(ActivationFixture),
                    typeof(ActivationFixture).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
                    typeof(ActivationBridge).GetMethod("Forward", BindingFlags.Static | BindingFlags.NonPublic));
                patcher.Patch(ActivateMethod,
                    prefix: new HarmonyMethod(typeof(FocusGuard).GetMethod("BeforeTestActivate", BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(FocusGuard).GetMethod("AfterTestActivate", BindingFlags.Static | BindingFlags.NonPublic)));
                // Hidden, handle-free fixtures exercise framework dispatch without focus changes.
                using (ActivationFixture main = new ActivationFixture())
                using (Form other = new Form())
                {
                    main.ObservedActivation();
                    AssertTest(1, 1, 0, "observed bridge caller was not suppressed");
                    main.LegitimateActivation();
                    AssertTest(2, 1, 1, "legitimate main activation was suppressed");
                    main.NestedDialogActivation();
                    AssertTest(3, 1, 2, "nested dialog activation was suppressed by an outer WndProc");
                    main.OtherFormActivation(other);
                    AssertTest(4, 1, 3, "another form's activation was suppressed");
                    if (main.IsHandleCreated || other.IsHandleCreated)
                        throw new InvalidOperationException("The activation fixtures unexpectedly created native handles.");
                }
            }
            finally
            {
                TestPolicy = null;
                patcher.Unpatch(ActivateMethod, HarmonyPatchType.All, id);
            }
            Patches patches = Harmony.GetPatchInfo(ActivateMethod);
            if (patches != null && patches.Owners.Contains(id))
                throw new InvalidOperationException("The focus self-test patch was not removed.");
        }
    }

    private static void AssertTest(int entries, int skips, int originals, string failure)
    {
        if (TestEntries != entries || TestSkips != skips || TestOriginals != originals)
            throw new InvalidOperationException("Focus guard self-test: " + failure + ".");
    }

    private sealed class ActivationFixture : Form
    {
        private int Mode;
        private Form Other;

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void ObservedActivation()
        {
            Mode = 0;
            Dispatch();
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void LegitimateActivation()
        {
            Activate();
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void NestedDialogActivation()
        {
            Mode = 1;
            Dispatch();
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void OtherFormActivation(Form other)
        {
            Mode = 2;
            Other = other;
            Dispatch();
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private void Dispatch()
        {
            Message message = Message.Create(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
            WndProc(ref message);
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        protected override void WndProc(ref Message message)
        {
            if (Mode == 1)
                DialogFixture.ActivateMain(this);
            else
                ActivationBridge.Forward(Mode == 2 ? Other : this);
        }
    }

    private static class ActivationBridge
    {
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static void Forward(Form form)
        {
            form.Activate();
        }
    }

    private static class DialogFixture
    {
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static void ActivateMain(Form form)
        {
            form.Activate();
        }
    }
}
