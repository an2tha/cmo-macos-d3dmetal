// Experimental process-local D3D11.1 graphics-state adapter for the CMO D3DMetal probe.
// This is a D2D-specific state adapter, not a complete D3D11 context-state implementation.
// It preserves the graphics slots changed by Wine Direct2D. Other bindings stay in place;
// automatic cross-stage resource unbinding remains a limitation for unusual resource aliases.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;

public static class ContextStateCompat
{
    const int InvalidArg = unchecked((int)0x80070057), NoInterface = unchecked((int)0x80004002);
    const int Fail = unchecked((int)0x80004005), NotFound = unchecked((int)0x887A0002);
    const int MoreData = unchecked((int)0x887A0003);
    static readonly object Gate = new object();
    static readonly Dictionary<IntPtr, State> States = new Dictionary<IntPtr, State>();
    static readonly Dictionary<IntPtr, State> Active = new Dictionary<IntPtr, State>();
    static readonly Dictionary<IntPtr, IntPtr> Hooks = new Dictionary<IntPtr, IntPtr>();
    static readonly HashSet<IntPtr> Hooking = new HashSet<IntPtr>();
    static readonly HashSet<IntPtr> BusyContexts = new HashSet<IntPtr>();
    static readonly Guid UnknownId = new Guid("00000000-0000-0000-c000-000000000046");
    static readonly Guid ChildId = NativeIds.DeviceChild;
    static readonly Guid StateId = NativeIds.ContextState;
    static readonly CreateStateFn CreateRoot = CreateState;
    static readonly SwapStateFn SwapRoot = SwapState;
    static readonly QueryFn QueryRoot = Query;
    static readonly RefFn AddRoot = Add;
    static readonly RefFn ReleaseRoot = Release;
    static readonly GetDeviceFn DeviceRoot = GetDevice;
    static readonly GetDataFn GetDataRoot = GetData;
    static readonly SetDataFn SetDataRoot = SetData;
    static readonly SetInterfaceFn SetInterfaceRoot = SetInterface;
    static readonly IntPtr StateVtable = BuildStateVtable();
    public static string LastError;
    public static int CreatedStates, SwappedStates;
    public static long SwapEntered, SwapCompleted, MaxSwapTicks;
    public static long TrackBatchEnter, BatchEnter, BatchSwapsSkipped;
    [ThreadStatic] static BatchScope CurrentBatch;
    sealed class BatchScope
    {
        public IntPtr Target, Context;
        public State Game, Drawing;
        public bool Disabled, LogicalDrawing;
        public BatchScope Parent;
    }
    public static void BeginBatch(IntPtr target)
    {
        Interlocked.Increment(ref TrackBatchEnter);
        Interlocked.Increment(ref BatchEnter);
        BatchScope parent = CurrentBatch;
        if (parent != null)
        {
            try
            {
                FlushBatch(parent, !parent.LogicalDrawing);
            }
            catch (Exception ex) { Error(ex); }
            parent.Disabled = true;
        }
        CurrentBatch = new BatchScope { Target = target, Parent = parent, Disabled = parent != null };
    }
    public static void EndBatch(IntPtr target)
    {
        BatchScope scope = CurrentBatch;
        if (scope == null)
            return;
        try
        {
            if (scope.Target != target)
            {
                // A mismatched scope cannot safely continue deferring state transitions.
                while (scope != null)
                {
                    FlushBatch(scope, true);
                    scope = scope.Parent;
                }
                CurrentBatch = null;
                return;
            }
            FlushBatch(scope, true);
        }
        catch (Exception ex) { Error(ex); }
        finally { if (CurrentBatch != null) CurrentBatch = scope == null ? null : scope.Parent; }
    }
    static void WriteHeldState(State state, IntPtr output)
    {
        if (output == IntPtr.Zero)
            return;
        lock (Gate)
            state.Hold();
        try
        {
            Marshal.WriteIntPtr(output, state.Pointer);
        }
        catch { DropState(state); throw; }
    }
    static void FlushBatch(BatchScope scope, bool restoreGame)
    {
        if (scope.Game == null)
            return;
        State game = scope.Game, drawing = scope.Drawing;
        IntPtr context = scope.Context;
        // Clear the scope before making native calls, so cleanup or reentry cannot flush it twice.
        scope.Game = scope.Drawing = null;
        scope.Context = IntPtr.Zero;
        scope.LogicalDrawing = false;
        BatchScope previous = CurrentBatch;
        CurrentBatch = null;
        try
        {
            if (restoreGame)
                SwapState(context, game.Pointer, IntPtr.Zero);
        }
        finally
        {
            CurrentBatch = previous;
            try
            {
                DropState(drawing);
            }
            finally { try { DropState(game); } finally { Unref(context); } }
        }
    }
    static bool TryBatchedSwap(BatchScope scope, IntPtr context, IntPtr incoming, IntPtr previous)
    {
        if (scope.Game == null)
            return false;
        if (context == scope.Context)
        {
            State physical;
            bool unchanged;
            lock (Gate)
                unchanged = Active.TryGetValue(context, out physical) && physical == scope.Drawing && !BusyContexts.Contains(context);
            if (!unchanged)
            {
                // An external thread changed this immediate context. Do not overwrite its newly active state.
                FlushBatch(scope, false);
                scope.Disabled = true;
                Error(new InvalidOperationException("A batched immediate context changed outside its drawing scope."));
                return false;
            }
            if (incoming == scope.Game.Pointer && scope.LogicalDrawing)
            {
                WriteHeldState(scope.Drawing, previous);
                scope.LogicalDrawing = false;
                Interlocked.Increment(ref BatchSwapsSkipped);
                return true;
            }
            if (incoming == scope.Drawing.Pointer && !scope.LogicalDrawing)
            {
                WriteHeldState(scope.Game, previous);
                scope.LogicalDrawing = true;
                Interlocked.Increment(ref BatchSwapsSkipped);
                return true;
            }
        }
        // Unknown state, phase or context: restore the real game state before using ordinary emulation.
        FlushBatch(scope, !scope.LogicalDrawing);
        scope.Disabled = true;
        return false;
    }
    static void RememberBatchSwap(BatchScope scope, IntPtr context, IntPtr incoming, IntPtr previous)
    {
        if (scope.Disabled || scope.Game != null || incoming == IntPtr.Zero || previous == IntPtr.Zero)
            return;
        IntPtr oldPointer = Marshal.ReadIntPtr(previous);
        if (oldPointer == IntPtr.Zero || oldPointer == incoming)
            return;
        State game = null, drawing = null;
        lock (Gate)
        {
            if (!States.TryGetValue(oldPointer, out game) || !States.TryGetValue(incoming, out drawing))
                return;
            game.Hold();
            drawing.Hold();
        }
        bool retained = false;
        try
        {
            Ref(context);
            retained = true;
            scope.Context = context;
            scope.Game = game;
            scope.Drawing = drawing;
            scope.LogicalDrawing = true;
        }
        finally { if (!retained) { DropState(drawing); DropState(game); } }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtect(IntPtr p, UIntPtr n, uint protection, out uint previous);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QueryFn(IntPtr self, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint RefFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDeviceFn(IntPtr self, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDataFn(IntPtr self, ref Guid key, ref uint size, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetDataFn(IntPtr self, ref Guid key, uint size, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetInterfaceFn(IntPtr self, ref Guid key, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint GetUIntFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateStateFn(IntPtr self, uint flags, IntPtr levels, uint count, uint sdk, ref Guid emulated, IntPtr chosen, IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SwapStateFn(IntPtr self, IntPtr incoming, IntPtr previous);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void VoidFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetPointerFn(IntPtr self, out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetPointerFn(IntPtr self, IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ArrayFn(IntPtr self, uint start, uint count, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ShaderGetFn(IntPtr self, out IntPtr shader, IntPtr instances, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ShaderSetFn(IntPtr self, IntPtr shader, IntPtr instances, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void VertexFn(IntPtr self, uint start, uint count, IntPtr values, IntPtr strides, IntPtr offsets);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void IndexGetFn(IntPtr self, out IntPtr buffer, out uint format, out uint offset);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void IndexSetFn(IntPtr self, IntPtr buffer, uint format, uint offset);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UIntGetFn(IntPtr self, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UIntSetFn(IntPtr self, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void TargetsGetFn(IntPtr self, uint count, IntPtr targets, out IntPtr depth);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void TargetsSetFn(IntPtr self, uint count, IntPtr targets, IntPtr depth);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void BlendGetFn(IntPtr self, out IntPtr state, IntPtr factor, out uint mask);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void BlendSetFn(IntPtr self, IntPtr state, IntPtr factor, uint mask);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DepthGetFn(IntPtr self, out IntPtr state, out uint reference);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DepthSetFn(IntPtr self, IntPtr state, uint reference);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SizedGetFn(IntPtr self, ref uint count, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SizedSetFn(IntPtr self, uint count, IntPtr values);

    static class FunctionCache<T> where T : class
    {
        [ThreadStatic] public static Dictionary<IntPtr, T> Entries;
    }
    static T Fn<T>(IntPtr self, int slot) where T : class
    {
        IntPtr pointer = Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size);
        Dictionary<IntPtr, T> cache = FunctionCache<T>.Entries;
        if (cache == null)
            FunctionCache<T>.Entries = cache = new Dictionary<IntPtr, T>();
        T function;
        if (!cache.TryGetValue(pointer, out function))
        {
            function = (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
            cache.Add(pointer, function);
        }
        return function;
    }
    static void Error(Exception ex)
    {
        try
        {
            LastError = ex.ToString();
            Console.Error.WriteLine("ContextStateCompat: " + LastError);
        }
        catch { }
    }
    static void Ref(IntPtr p)
    {
        if (p != IntPtr.Zero)
            Marshal.AddRef(p);
    }
    static void Unref(IntPtr p)
    {
        if (p != IntPtr.Zero)
            Marshal.Release(p);
    }
    static IntPtr QueryInterface(IntPtr p, Guid iid)
    {
        IntPtr q;
        int hr = Marshal.QueryInterface(p, ref iid, out q);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        return q;
    }
    static void WriteHook(IntPtr slot, IntPtr value)
    {
        uint old, ignored;
        if (!VirtualProtect(slot, (UIntPtr)IntPtr.Size, 0x40, out old))
            throw new System.ComponentModel.Win32Exception();
        try
        {
            Marshal.WriteIntPtr(slot, value);
        }
        finally { VirtualProtect(slot, (UIntPtr)IntPtr.Size, old, out ignored); }
    }
    static void Hook(IntPtr self, int slot, Delegate callback)
    {
        IntPtr address = IntPtr.Add(Marshal.ReadIntPtr(self), slot * IntPtr.Size);
        lock (Gate)
        {
            if (Hooks.ContainsKey(address))
                return;
            if (!Hooking.Add(address))
                throw new InvalidOperationException("Concurrent hook installation.");
        }
        try
        {
            IntPtr original = Marshal.ReadIntPtr(address);
            WriteHook(address, Marshal.GetFunctionPointerForDelegate(callback));
            lock (Gate)
                Hooks.Add(address, original);
        }
        finally { lock (Gate) Hooking.Remove(address); }
    }
    public static void Install(IntPtr device)
    {
        if (device == IntPtr.Zero)
            throw new ArgumentNullException("device");
        IntPtr d = IntPtr.Zero, c = IntPtr.Zero;
        try
        {
            d = QueryInterface(device, NativeIds.Device1);
            Fn<GetPointerFn>(d, 43)(d, out c);
            if (c == IntPtr.Zero)
                throw new InvalidOperationException("GetImmediateContext1 returned null.");
            Hook(d, 47, CreateRoot);
            Hook(c, 131, SwapRoot);
        }
        finally { Unref(c); Unref(d); }
    }
    // Call only after releasing the experiment's Direct2D objects and state handles.
    public static void Uninstall()
    {
        KeyValuePair<IntPtr, IntPtr>[] hooks;
        KeyValuePair<IntPtr, State>[] active;
        lock (Gate)
        {
            if (BusyContexts.Count != 0 || Hooking.Count != 0)
                throw new InvalidOperationException("Compatibility calls are still active.");
            hooks = new List<KeyValuePair<IntPtr, IntPtr>>(Hooks).ToArray();
            active = new List<KeyValuePair<IntPtr, State>>(Active).ToArray();
            Hooks.Clear();
            Active.Clear();
        }
        foreach (KeyValuePair<IntPtr, IntPtr> h in hooks)
            WriteHook(h.Key, h.Value);
        foreach (KeyValuePair<IntPtr, State> item in active)
        {
            DropState(item.Value);
            Unref(item.Key);
        }
    }
    static IntPtr BuildStateVtable()
    {
        Delegate[] entries = { QueryRoot, AddRoot, ReleaseRoot, DeviceRoot, GetDataRoot, SetDataRoot, SetInterfaceRoot };
        IntPtr table = Marshal.AllocHGlobal(entries.Length * IntPtr.Size);
        for (int i = 0; i < entries.Length; ++i)
            Marshal.WriteIntPtr(table, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(entries[i]));
        return table;
    }
    sealed class PrivateValue
    {
        // Managed references protect the one owned native interface reference while dictionary entries are replaced.
        public byte[] Bytes; public IntPtr Interface; public uint References = 1;
        public void DisposeDetached()
        {
            Unref(Interface);
            Interface = IntPtr.Zero;
        }
    }
    static void DropPrivate(PrivateValue value)
    {
        if (value == null)
            return;
        bool dispose;
        lock (Gate)
        {
            if (value.References == 0)
                throw new InvalidOperationException("Private-data reference underflow.");
            dispose = --value.References == 0;
        }
        if (dispose)
            value.DisposeDetached();
    }
    sealed class State
    {
        public IntPtr Pointer, Device; public uint References = 1; public Snapshot Saved;
        public readonly Dictionary<Guid, PrivateValue> Private = new Dictionary<Guid, PrivateValue>();
        public State(IntPtr ownedDevice, Snapshot saved)
        {
            Device = ownedDevice;
            Saved = saved;
            Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(Pointer, StateVtable);
        }
        // Gate must be held. This changes managed bookkeeping only.
        public void Hold()
        {
            checked
            {
                References++;
            }
        }
        public void DisposeDetached()
        {
            IntPtr pointer = Pointer, device = Device;
            Pointer = Device = IntPtr.Zero;
            Snapshot saved = Saved;
            Saved = null;
            PrivateValue[] values = new List<PrivateValue>(Private.Values).ToArray();
            Private.Clear();
            if (pointer != IntPtr.Zero)
                Marshal.FreeHGlobal(pointer);
            if (saved != null)
                saved.Dispose();
            foreach (PrivateValue value in values)
                DropPrivate(value);
            Unref(device);
        }
    }
    // Construction owns the supplied device and snapshot. Publication does not call native code.
    static State NewState(IntPtr ownedDevice, Snapshot snapshot)
    {
        State state = null;
        try
        {
            state = new State(ownedDevice, snapshot);
            lock (Gate)
            {
                States.Add(state.Pointer, state);
                CreatedStates++;
            }
            return state;
        }
        catch
        {
            if (state != null)
                state.DisposeDetached();
            else
            {
                snapshot.Dispose();
                Unref(ownedDevice);
            }
            throw;
        }
    }
    static State PinState(IntPtr pointer)
    {
        lock (Gate)
        {
            State state = States[pointer];
            state.Hold();
            return state;
        }
    }
    static uint DropState(State state)
    {
        bool dispose;
        uint left;
        lock (Gate)
        {
            if (state.References == 0)
                throw new InvalidOperationException("State reference underflow.");
            left = --state.References;
            dispose = left == 0;
            if (dispose)
                States.Remove(state.Pointer);
        }
        // No native Release, driver call or unmanaged deallocation is made under Gate.
        if (dispose)
            state.DisposeDetached();
        return left;
    }
    static int Query(IntPtr self, ref Guid iid, out IntPtr result)
    {
        result = IntPtr.Zero;
        try
        {
            if (iid != UnknownId && iid != ChildId && iid != StateId)
                return NoInterface;
            lock (Gate)
            {
                State state;
                if (!States.TryGetValue(self, out state))
                    return NoInterface;
                state.Hold();
                result = self;
            }
            return 0;
        }
        catch (Exception ex) { Error(ex); return Fail; }
    }
    static uint Add(IntPtr self)
    {
        try
        {
            lock (Gate)
            {
                State state = States[self];
                state.Hold();
                return state.References;
            }
        }
        catch (Exception ex) { Error(ex); return 0; }
    }
    static uint Release(IntPtr self)
    {
        try
        {
            State state;
            // The caller's COM reference keeps the object alive until DropState consumes it.
            lock (Gate)
                state = States[self];
            return DropState(state);
        }
        catch (Exception ex) { Error(ex); return 0; }
    }
    static void GetDevice(IntPtr self, out IntPtr device)
    {
        device = IntPtr.Zero;
        State state = null;
        try
        {
            state = PinState(self);
            IntPtr value = state.Device;
            Ref(value);
            device = value;
        }
        catch (Exception ex) { Error(ex); }
        finally { if (state != null) DropState(state); }
    }
    static int GetData(IntPtr self, ref Guid key, ref uint size, IntPtr data)
    {
        State state = null;
        PrivateValue value = null;
        try
        {
            state = PinState(self);
            lock (Gate)
            {
                if (!state.Private.TryGetValue(key, out value))
                    return NotFound;
                checked
                {
                    value.References++;
                }
            }
            uint needed = value.Interface != IntPtr.Zero ? (uint)IntPtr.Size : (uint)value.Bytes.Length;
            uint supplied = size;
            size = needed;
            if (data == IntPtr.Zero)
                return 0;
            if (supplied < needed)
                return MoreData;
            if (value.Interface != IntPtr.Zero)
            {
                Ref(value.Interface);
                Marshal.WriteIntPtr(data, value.Interface);
            }
            else
                Marshal.Copy(value.Bytes, 0, data, value.Bytes.Length);
            return 0;
        }
        catch (Exception ex) { Error(ex); return Fail; }
        finally { DropPrivate(value); if (state != null) DropState(state); }
    }
    static int SetData(IntPtr self, ref Guid key, uint size, IntPtr data)
    {
        State state = null;
        PrivateValue previous = null, replacement = null;
        bool published = false;
        try
        {
            if (size > Int32.MaxValue || (size != 0 && data == IntPtr.Zero))
                return InvalidArg;
            state = PinState(self);
            if (size != 0)
            {
                byte[] bytes = new byte[(int)size];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                replacement = new PrivateValue { Bytes = bytes };
            }
            lock (Gate)
            {
                PrivateValue found;
                state.Private.TryGetValue(key, out found);
                if (replacement == null)
                    state.Private.Remove(key);
                else
                    state.Private[key] = replacement;
                previous = found;
                published = true;
            }
            return 0;
        }
        catch (Exception ex) { Error(ex); return Fail; }
        finally { DropPrivate(previous); if (!published) DropPrivate(replacement); if (state != null) DropState(state); }
    }
    static int SetInterface(IntPtr self, ref Guid key, IntPtr value)
    {
        State state = null;
        PrivateValue previous = null, replacement = null;
        bool published = false;
        try
        {
            state = PinState(self);
            if (value != IntPtr.Zero)
            {
                Ref(value);
                replacement = new PrivateValue { Interface = value };
            }
            lock (Gate)
            {
                PrivateValue found;
                state.Private.TryGetValue(key, out found);
                if (replacement == null)
                    state.Private.Remove(key);
                else
                    state.Private[key] = replacement;
                previous = found;
                published = true;
            }
            return 0;
        }
        catch (Exception ex) { Error(ex); return Fail; }
        finally { DropPrivate(previous); if (!published) DropPrivate(replacement); if (state != null) DropState(state); }
    }
    static bool IsLevel(uint level)
    {
        return level == 0x9100 || level == 0x9200 || level == 0x9300 || level == 0xa000 || level == 0xa100 || level == 0xb000 || level == 0xb100 || level == 0xc000 || level == 0xc100;
    }
    static int CreateState(IntPtr self, uint flags, IntPtr levels, uint count, uint sdk, ref Guid emulated, IntPtr chosen, IntPtr output)
    {
        if (output != IntPtr.Zero)
            Marshal.WriteIntPtr(output, IntPtr.Zero);
        try
        {
            if (flags > 1 || levels == IntPtr.Zero || count == 0 || count > 32 || sdk != 7)
                return InvalidArg;
            if (emulated != NativeIds.Device && emulated != NativeIds.Device1)
                return NoInterface;
            uint actual = Fn<GetUIntFn>(self, 37)(self), selected = 0;
            for (uint i = 0; i < count; ++i)
            {
                uint l = unchecked((uint)Marshal.ReadInt32(levels, checked((int)i * 4)));
                if (!IsLevel(l))
                    return InvalidArg;
                if (selected == 0 && l <= actual)
                    selected = l;
            }
            if (selected == 0)
                return unchecked((int)0x887A0004);
            if (chosen != IntPtr.Zero)
                Marshal.WriteInt32(chosen, unchecked((int)selected));
            if (output == IntPtr.Zero)
                return 1;
            State state = NewState(QueryInterface(self, NativeIds.Device), new Snapshot());
            Marshal.WriteIntPtr(output, state.Pointer);
            return 0;
        }
        catch (Exception ex) { Error(ex); return Fail; }
    }
    static void SupplyPreviousAfterFailure(IntPtr context, IntPtr incomingPointer, IntPtr previous, State pinnedOld)
    {
        State fallback = null;
        lock (Gate)
        {
            if (!Active.TryGetValue(context, out fallback) && incomingPointer != IntPtr.Zero)
                States.TryGetValue(incomingPointer, out fallback);
            if (fallback == null && pinnedOld != null && pinnedOld.References != 0)
                fallback = pinnedOld;
            if (fallback != null)
                fallback.Hold();
        }
        if (fallback == null)
        {
            IntPtr device;
            Fn<GetDeviceFn>(context, 3)(context, out device);
            fallback = NewState(device, new Snapshot());
        }
        // The held reference is transferred directly to the native caller.
        Marshal.WriteIntPtr(previous, fallback.Pointer);
    }
    static void SwapState(IntPtr context, IntPtr incomingPointer, IntPtr previous)
    {
        long started = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref SwapEntered);
        if (previous != IntPtr.Zero)
            Marshal.WriteIntPtr(previous, IntPtr.Zero);
        BatchScope batch = CurrentBatch;
        State old = null, incoming = null;
        Snapshot captured = null, obsolete = null;
        bool busy = false, contextRetained = false, contextAdopted = false;
        try
        {
            if (batch != null && !batch.Disabled && TryBatchedSwap(batch, context, incomingPointer, previous))
            {
                Interlocked.Increment(ref SwappedStates);
                return;
            }
            lock (Gate)
            {
                // Immediate-context calls require caller serialization. Detect reentry rather than waiting while native locks may be held.
                if (!BusyContexts.Add(context))
                    throw new InvalidOperationException("Concurrent or reentrant SwapDeviceContextState on one context.");
                busy = true;
                if (incomingPointer != IntPtr.Zero)
                {
                    if (!States.TryGetValue(incomingPointer, out incoming))
                        throw new NotSupportedException("Foreign context-state object.");
                    incoming.Hold();
                }
                if (Active.TryGetValue(context, out old))
                    old.Hold();
            }
            captured = Snapshot.Capture(context);
            if (old == null)
            {
                IntPtr device;
                Fn<GetDeviceFn>(context, 3)(context, out device);
                old = NewState(device, captured);
                captured = null;
                Ref(context);
                contextRetained = true;
            }
            else
            {
                lock (Gate)
                {
                    obsolete = old.Saved;
                    old.Saved = captured;
                    captured = null;
                }
                obsolete.Dispose();
                obsolete = null;
            }
            if (incoming == old)
            {
                if (previous != IntPtr.Zero)
                {
                    lock (Gate)
                        old.Hold();
                    Marshal.WriteIntPtr(previous, old.Pointer);
                }
                Interlocked.Increment(ref SwappedStates);
                return;
            }
            if (incoming == null)
            {
                IntPtr device;
                Fn<GetDeviceFn>(context, 3)(context, out device);
                incoming = NewState(device, new Snapshot());
            }
            Snapshot restore;
            lock (Gate)
                restore = incoming.Saved;
            restore.Restore(context);
            State oldMapping = null;
            lock (Gate)
            {
                Active.TryGetValue(context, out oldMapping);
                incoming.Hold();
                Active[context] = incoming;
                contextAdopted = contextRetained;
                if (previous != IntPtr.Zero)
                    old.Hold();
            }
            if (previous != IntPtr.Zero)
                Marshal.WriteIntPtr(previous, old.Pointer);
            if (oldMapping != null)
                DropState(oldMapping);
            Interlocked.Increment(ref SwappedStates);
            if (batch != null && !batch.Disabled)
                RememberBatchSwap(batch, context, incomingPointer, previous);
        }
        catch (Exception ex)
        {
            Error(ex);
            // Wine Direct2D expects a releasable previous-state handle even though this API cannot return an HRESULT.
            if (previous != IntPtr.Zero && Marshal.ReadIntPtr(previous) == IntPtr.Zero)
            {
                try
                {
                    SupplyPreviousAfterFailure(context, incomingPointer, previous, old);
                }
                catch (Exception fallback) { Error(new AggregateException("Could not construct a previous-state fallback.", ex, fallback)); }
            }
        }
        finally
        {
            // Temporary pins and detached snapshots are released without holding the dictionary lock.
            try
            {
                if (captured != null)
                    captured.Dispose();
                if (obsolete != null)
                    obsolete.Dispose();
                if (incoming != null)
                    DropState(incoming);
                if (old != null)
                    DropState(old);
                if (contextRetained && !contextAdopted)
                    Unref(context);
            }
            catch (Exception ex) { Error(ex); }
            if (busy)
            {
                lock (Gate)
                    BusyContexts.Remove(context);
            }
            long elapsed = Stopwatch.GetTimestamp() - started;
            long max;
            do
            {
                max = Interlocked.Read(ref MaxSwapTicks);
                if (elapsed <= max)
                    break;
            }
            while (Interlocked.CompareExchange(ref MaxSwapTicks, elapsed, max) != max);
            Interlocked.Increment(ref SwapCompleted);
        }
    }
    sealed class Memory : IDisposable
    {
        public IntPtr P; public int Length;
        public Memory(int bytes) : this(bytes, true) { }
        Memory(int bytes, bool zero)
        {
            Length = bytes;
            P = Marshal.AllocHGlobal(bytes);
            if (zero)
            for (int i = 0; i < bytes; ++i)
                Marshal.WriteByte(P, i, 0);
        }
        public void Dispose()
        {
            if (P != IntPtr.Zero)
                Marshal.FreeHGlobal(P);
            P = IntPtr.Zero;
        }
        public IntPtr[] Pointers(int count)
        {
            IntPtr[] values = new IntPtr[count];
            Marshal.Copy(P, values, 0, count);
            return values;
        }
        public byte[] Bytes()
        {
            byte[] values = new byte[Length];
            Marshal.Copy(P, values, 0, Length);
            return values;
        }
        public static Memory From(IntPtr[] values)
        {
            Memory m = new Memory(values.Length * IntPtr.Size, false);
            Marshal.Copy(values, 0, m.P, values.Length);
            return m;
        }
        public static Memory From(byte[] values)
        {
            Memory m = new Memory(values.Length, false);
            Marshal.Copy(values, 0, m.P, values.Length);
            return m;
        }
    }
    static readonly IntPtr[] EmptyPointers = new IntPtr[0];
    static readonly IntPtr NullBindings = AllocateNullBindings();
    static IntPtr AllocateNullBindings()
    {
        IntPtr pointer = Marshal.AllocHGlobal(8 * IntPtr.Size);
        for (int i = 0; i < 8; ++i)
            Marshal.WriteIntPtr(pointer, i * IntPtr.Size, IntPtr.Zero);
        return pointer;
    }
    sealed class Stage : IDisposable
    {
        public IntPtr Shader; public IntPtr[] Instances, Buffers, Resources, Samplers;
        public int SetShader, SetBuffers, SetResources, SetSamplers;
        public static Stage Capture(IntPtr c, int getShader, int getBuffers, int getResources, int getSamplers,
            int setShader, int setBuffers, int setResources, int setSamplers, int bufferCount, int resourceCount, int samplerCount)
        {
            Stage s = new Stage();
            s.SetShader = setShader;
            s.SetBuffers = setBuffers;
            s.SetResources = setResources;
            s.SetSamplers = setSamplers;
            try
            {
                uint count = 0;
                ShaderGetFn get = Fn<ShaderGetFn>(c, getShader);
                get(c, out s.Shader, IntPtr.Zero, ref count);
                if (count > 256)
                    throw new InvalidOperationException("Class-instance overflow.");
                if (count == 0)
                    s.Instances = EmptyPointers;
                else
                {
                    using (Memory instances = new Memory(checked((int)count * IntPtr.Size)))
                    {
                        IntPtr first = s.Shader;
                        s.Shader = IntPtr.Zero;
                        try
                        {
                            get(c, out s.Shader, instances.P, ref count);
                        }
                        finally { Unref(first); }
                        if (count > instances.Length / IntPtr.Size)
                            throw new InvalidOperationException("Class-instance count changed during capture.");
                        s.Instances = instances.Pointers((int)count);
                    }
                }
                if (bufferCount != 0)
                    s.Buffers = GetArray(c, getBuffers, bufferCount);
                if (resourceCount != 0)
                    s.Resources = GetArray(c, getResources, resourceCount);
                if (samplerCount != 0)
                    s.Samplers = GetArray(c, getSamplers, samplerCount);
                return s;
            }
            catch { s.Dispose(); throw; }
        }
        public void Restore(IntPtr c)
        {
            if (Instances.Length == 0)
                Fn<ShaderSetFn>(c, SetShader)(c, Shader, IntPtr.Zero, 0);
            else
            using (Memory m = Memory.From(Instances))
                Fn<ShaderSetFn>(c, SetShader)(c, Shader, m.P, (uint)Instances.Length);
            if (Buffers != null)
                PutArray(c, SetBuffers, Buffers);
            if (Resources != null)
                PutArray(c, SetResources, Resources);
            if (Samplers != null)
                PutArray(c, SetSamplers, Samplers);
        }
        public void Dispose()
        {
            Unref(Shader);
            Shader = IntPtr.Zero;
            ReleaseArray(Instances);
            ReleaseArray(Buffers);
            ReleaseArray(Resources);
            ReleaseArray(Samplers);
            Instances = Buffers = Resources = Samplers = null;
        }
    }
    static IntPtr[] GetArray(IntPtr c, int slot, int count)
    {
        using (Memory m = new Memory(count * IntPtr.Size))
        {
            Fn<ArrayFn>(c, slot)(c, 0, (uint)count, m.P);
            return m.Pointers(count);
        }
    }
    static void PutArray(IntPtr c, int slot, IntPtr[] values)
    {
        using (Memory m = Memory.From(values))
            Fn<ArrayFn>(c, slot)(c, 0, (uint)values.Length, m.P);
    }
    static void ReleaseArray(IntPtr[] values)
    {
        if (values != null)
        foreach (IntPtr p in values)
            Unref(p);
    }
    sealed class Snapshot : IDisposable
    {
        bool Default = true; readonly List<Stage> Stages = new List<Stage>();
        IntPtr Layout, Index, DepthView, Blend, DepthState, Raster;
        IntPtr[] Vertices, Targets; byte[] Strides, Offsets, BlendFactor, Viewports, Scissors;
        uint IndexFormat, IndexOffset, Topology, Mask, StencilRef, ViewportCount, ScissorCount;
        public static Snapshot Capture(IntPtr c)
        {
            Snapshot s = new Snapshot();
            s.Default = false;
            try
            {
                s.Stages.Add(Stage.Capture(c, 76, 72, 84, 85, 11, 7, 25, 26, 1, 0, 0));
                s.Stages.Add(Stage.Capture(c, 74, 77, 73, 75, 9, 16, 8, 10, 1, 4, 2));
                s.Stages.Add(Stage.Capture(c, 82, 81, 87, 88, 23, 22, 31, 32, 0, 0, 0));
                s.Stages.Add(Stage.Capture(c, 98, 100, 97, 99, 60, 62, 59, 61, 0, 0, 0));
                s.Stages.Add(Stage.Capture(c, 102, 104, 101, 103, 64, 66, 63, 65, 0, 0, 0));
                s.Stages.Add(Stage.Capture(c, 107, 109, 105, 108, 69, 71, 67, 70, 0, 0, 0));
                Fn<GetPointerFn>(c, 78)(c, out s.Layout);
                Fn<IndexGetFn>(c, 80)(c, out s.Index, out s.IndexFormat, out s.IndexOffset);
                Fn<UIntGetFn>(c, 83)(c, out s.Topology);
                using (Memory b = new Memory(IntPtr.Size))
                using (Memory st = new Memory(4))
                using (Memory of = new Memory(4))
                {
                    Fn<VertexFn>(c, 79)(c, 0, 1, b.P, st.P, of.P);
                    s.Vertices = b.Pointers(1);
                    s.Strides = st.Bytes();
                    s.Offsets = of.Bytes();
                }
                using (Memory r = new Memory(8 * IntPtr.Size))
                {
                    Fn<TargetsGetFn>(c, 89)(c, 8, r.P, out s.DepthView);
                    s.Targets = r.Pointers(8);
                }
                using (Memory f = new Memory(16))
                {
                    Fn<BlendGetFn>(c, 91)(c, out s.Blend, f.P, out s.Mask);
                    s.BlendFactor = f.Bytes();
                }
                Fn<DepthGetFn>(c, 92)(c, out s.DepthState, out s.StencilRef);
                Fn<GetPointerFn>(c, 94)(c, out s.Raster);
                using (Memory v = new Memory(16 * 24))
                {
                    s.ViewportCount = 16;
                    Fn<SizedGetFn>(c, 95)(c, ref s.ViewportCount, v.P);
                    s.Viewports = v.Bytes();
                }
                using (Memory r = new Memory(16 * 16))
                {
                    s.ScissorCount = 16;
                    Fn<SizedGetFn>(c, 96)(c, ref s.ScissorCount, r.P);
                    s.Scissors = r.Bytes();
                }
                if (s.ViewportCount > 16 || s.ScissorCount > 16)
                    throw new InvalidOperationException("Rasterizer array overflow.");
                return s;
            }
            catch { s.Dispose(); throw; }
        }
        public void Restore(IntPtr c)
        {
            if (Default)
            {
                RestoreD2DDefaults(c);
                return;
            }
            // Unbind tracked inputs and outputs first so resource hazards cannot silently discard restored bindings.
            Fn<ArrayFn>(c, 8)(c, 0, 4, NullBindings);
            using (Memory emptyTargets = new Memory(8 * IntPtr.Size))
                Fn<TargetsSetFn>(c, 33)(c, 0, emptyTargets.P, IntPtr.Zero);
            Fn<SetPointerFn>(c, 17)(c, Layout);
            Fn<IndexSetFn>(c, 19)(c, Index, IndexFormat, IndexOffset);
            Fn<UIntSetFn>(c, 24)(c, Topology);
            using (Memory b = Memory.From(Vertices))
            using (Memory st = Memory.From(Strides))
            using (Memory of = Memory.From(Offsets))
                Fn<VertexFn>(c, 18)(c, 0, 1, b.P, st.P, of.P);
            using (Memory t = Memory.From(Targets))
                Fn<TargetsSetFn>(c, 33)(c, 8, t.P, DepthView);
            using (Memory f = Memory.From(BlendFactor))
                Fn<BlendSetFn>(c, 35)(c, Blend, f.P, Mask);
            Fn<DepthSetFn>(c, 36)(c, DepthState, StencilRef);
            Fn<SetPointerFn>(c, 43)(c, Raster);
            using (Memory v = Memory.From(Viewports))
                Fn<SizedSetFn>(c, 44)(c, ViewportCount, v.P);
            using (Memory r = Memory.From(Scissors))
                Fn<SizedSetFn>(c, 45)(c, ScissorCount, r.P);
            foreach (Stage stage in Stages)
                stage.Restore(c);
        }
        static void RestoreD2DDefaults(IntPtr c)
        {
            // Reset only Wine Direct2D's touched slots. ClearState would also erase bindings that this adapter does not snapshot.
            Fn<ShaderSetFn>(c, 11)(c, IntPtr.Zero, IntPtr.Zero, 0);
            Fn<ShaderSetFn>(c, 9)(c, IntPtr.Zero, IntPtr.Zero, 0);
            Fn<ShaderSetFn>(c, 23)(c, IntPtr.Zero, IntPtr.Zero, 0);
            Fn<ShaderSetFn>(c, 60)(c, IntPtr.Zero, IntPtr.Zero, 0);
            Fn<ShaderSetFn>(c, 64)(c, IntPtr.Zero, IntPtr.Zero, 0);
            Fn<ShaderSetFn>(c, 69)(c, IntPtr.Zero, IntPtr.Zero, 0);
            Fn<ArrayFn>(c, 7)(c, 0, 1, NullBindings);
            Fn<ArrayFn>(c, 16)(c, 0, 1, NullBindings);
            Fn<ArrayFn>(c, 8)(c, 0, 4, NullBindings);
            Fn<ArrayFn>(c, 10)(c, 0, 2, NullBindings);
            Fn<SetPointerFn>(c, 17)(c, IntPtr.Zero);
            Fn<IndexSetFn>(c, 19)(c, IntPtr.Zero, 0, 0);
            Fn<VertexFn>(c, 18)(c, 0, 1, NullBindings, NullBindings, NullBindings);
            Fn<UIntSetFn>(c, 24)(c, 0);
            Fn<TargetsSetFn>(c, 33)(c, 0, NullBindings, IntPtr.Zero);
            Fn<BlendSetFn>(c, 35)(c, IntPtr.Zero, IntPtr.Zero, UInt32.MaxValue);
            Fn<DepthSetFn>(c, 36)(c, IntPtr.Zero, 0);
            Fn<SetPointerFn>(c, 43)(c, IntPtr.Zero);
            Fn<SizedSetFn>(c, 44)(c, 0, IntPtr.Zero);
            Fn<SizedSetFn>(c, 45)(c, 0, IntPtr.Zero);
        }
        public void Dispose()
        {
            foreach (Stage s in Stages)
                s.Dispose();
            Stages.Clear();
            Unref(Layout);
            Unref(Index);
            Unref(DepthView);
            Unref(Blend);
            Unref(DepthState);
            Unref(Raster);
            Layout = Index = DepthView = Blend = DepthState = Raster = IntPtr.Zero;
            ReleaseArray(Vertices);
            ReleaseArray(Targets);
            Vertices = Targets = null;
        }
    }
}
