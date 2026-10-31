using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

internal static class BootstrapSelfTest
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Target()
    {
        return 1;
    }

    private static bool Prefix(ref int __result)
    {
        __result = 2;
        return false;
    }

    // Exercises the embedded dependency and detour without loading any game SDK.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run()
    {
        const string id = "cmo.browser.bootstrap-self-test";
        MethodInfo target = typeof(BootstrapSelfTest).GetMethod("Target", BindingFlags.Static | BindingFlags.NonPublic);
        Harmony patcher = new Harmony(id);
        try
        {
            if (Target() != 1)
                throw new InvalidOperationException("Unexpected baseline result.");
            patcher.Patch(target, prefix: new HarmonyMethod(typeof(BootstrapSelfTest).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)));
            if (Target() != 2)
                throw new InvalidOperationException("The embedded Harmony prefix was not applied.");
            patcher.Unpatch(target, HarmonyPatchType.All, id);
            if (Target() != 1)
                throw new InvalidOperationException("The original method was not restored.");
            Console.WriteLine("PASS: embedded Harmony resolved, prefix applied, original restored.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { patcher.Unpatch(target, HarmonyPatchType.All, id); }
    }
}
