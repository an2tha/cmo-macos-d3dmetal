using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using HarmonyLib;
using Timer = System.Windows.Forms.Timer;

// Keeps the native map HWNDs disjoint from the actual selected-unit ElementHost.
// Game-owned controls, Dock/Anchor settings, and WPF content are not replaced.
internal static class PanelLayout
{
    private const string Owner = "cmo.selected-sidebar.layout";
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Action<string> Logger;
    private static Harmony Patcher;
    private static Timer Discovery;
    private static Form Pending;
    private static int Attempts;
    private static bool BoundsHooked;
    private static LayoutState Current, Restoring;

    internal static void Install(string gameDirectory, Action<string> logger)
    {
        Logger = logger;
        try
        {
            if (!ReviewedGame.IsKnown(gameDirectory, Log))
                return;
            Patcher = new Harmony(Owner);
            Patcher.Patch(typeof(Form).GetMethod("OnShown", BindingFlags.Instance | BindingFlags.NonPublic),
                postfix: new HarmonyMethod(typeof(PanelLayout).GetMethod("AfterShown", BindingFlags.Static | BindingFlags.NonPublic)));
            Log("Sidebar layout waiting for the real visible MainForm.");
        }
        catch (Exception error) { Log("Sidebar layout installation failed; original layout retained: " + error); }
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

    private static void AfterShown(Form __instance)
    {
        try
        {
            if (__instance.GetType().FullName != "Command.MainForm" || !__instance.Visible || !__instance.IsHandleCreated
                || Current != null || Restoring != null || Discovery != null)
                return;
            Pending = __instance;
            Attempts = 0;
            Discovery = new Timer { Interval = 500 };
            Discovery.Tick += Discover;
            Discovery.Start();
        }
        catch (Exception error) { Log("Sidebar discovery setup failed: " + error); }
    }

    private static void StopDiscovery()
    {
        if (Discovery != null)
        {
            Discovery.Stop();
            Discovery.Dispose();
            Discovery = null;
        }
        Pending = null;
    }

    private static void Discover(object sender, EventArgs args)
    {
        try
        {
            if (Pending == null || Pending.IsDisposed || ++Attempts > 120)
            {
                StopDiscovery();
                return;
            }
            Form main = Pending;
            Type type = main.GetType();
            FieldInfo selectedField = type.GetField("XT0EyPE4hQ", Fields), rootField = type.GetField("RightColumnWPF1", Fields),
                mapField = type.GetField("MM1EgclS2r", Fields), panelField = type.GetField("fb6EaQn3Nc", Fields);
            if (selectedField == null || rootField == null || mapField == null || panelField == null)
                throw new MissingFieldException("The reviewed sidebar fields are missing.");
            ElementHost sidebar = selectedField.GetValue(main) as ElementHost;
            Control map = mapField.GetValue(main) as Control, panel = panelField.GetValue(main) as Control;
            object root = rootField.GetValue(main);
            if (sidebar == null || sidebar.Child == null || root == null || map == null || panel == null
                || !sidebar.IsHandleCreated || !map.IsHandleCreated || !panel.IsHandleCreated)
                return;
            if (!ReferenceEquals(sidebar.Child, root) || root.GetType().FullName != "Command.RightColumnWPF"
                || !ReferenceEquals(sidebar.Parent, main) || !ReferenceEquals(panel.Parent, main) || !ReferenceEquals(map.Parent, panel))
                throw new InvalidOperationException("The selected sidebar or parent relationships differ from the reviewed layout.");
            RendererViewport viewport = RendererViewport.TryCreate(map,
                () => !main.IsDisposed && ReferenceEquals(mapField.GetValue(main), map) && ReferenceEquals(panelField.GetValue(main), panel));
            if (viewport == null)
                return;
            EnsureBoundsHook();
            Current = new LayoutState(main, sidebar, panel, map,
                () => sidebar.Visible && sidebar.Child.IsVisible,
                () => ReferenceEquals(selectedField.GetValue(main), sidebar) && ReferenceEquals(rootField.GetValue(main), root)
                    && ReferenceEquals(sidebar.Child, root) && ReferenceEquals(mapField.GetValue(main), map)
                    && ReferenceEquals(panelField.GetValue(main), panel), viewport, sidebar.Child);
            StopDiscovery();
            Current.Subscribe();
            Current.Synchronize();
            if (Current != null)
                Log("Sidebar layout attached to exact selected host; hover host excluded.");
        }
        catch (Exception error)
        {
            StopDiscovery();
            if (Current != null)
                Current.Rollback();
            Log("Sidebar discovery failed; original layout retained: " + error);
        }
    }

    private static void EnsureBoundsHook()
    {
        if (BoundsHooked)
            return;
        if (Patcher == null)
            Patcher = new Harmony(Owner);
        Patcher.Patch(typeof(Control).GetMethod("SetBoundsCore", BindingFlags.Instance | BindingFlags.NonPublic),
            prefix: new HarmonyMethod(typeof(PanelLayout).GetMethod("BeforeBounds", BindingFlags.Static | BindingFlags.NonPublic)));
        BoundsHooked = true;
    }

    private static void BeforeBounds(Control __instance, int __0, ref int __2)
    {
        LayoutState state = Current;
        if (state == null || !state.Clamping)
            return;
        if (ReferenceEquals(__instance, state.Panel))
            __2 = Math.Min(__2, Math.Max(64, state.RightEdge - __0));
        else if (ReferenceEquals(__instance, state.Map))
            __2 = Math.Min(__2, Math.Max(1, state.Panel.ClientSize.Width - __0 - state.MapRightGap));
    }

    private sealed class LayoutState
    {
        internal readonly Form Main;
        internal readonly Control Sidebar, Panel, Map;
        private readonly Control PanelParent, MapParent;
        private readonly Func<bool> Visible, IdentityValid;
        private readonly System.Windows.UIElement SelectedRoot;
        private readonly IMapViewport Viewport;
        private Timer Maintenance;
        private int WaitingTicks, RestoreFailures;
        private bool Resizing, RollbackPending, RestorePending;
        private readonly int PanelRightGap;
        internal readonly int MapRightGap;
        internal bool Clamping;
        internal int RightEdge;
        private int Queued;
        private bool Updating, Disposed;
        private int LastWidth = -1;

        internal LayoutState(Form main, Control sidebar, Control panel, Control map, Func<bool> visible, Func<bool> identityValid,
            IMapViewport viewport, System.Windows.UIElement selectedRoot = null)
        {
            Main = main;
            Sidebar = sidebar;
            Panel = panel;
            Map = map;
            PanelParent = panel.Parent;
            MapParent = map.Parent;
            Visible = visible;
            IdentityValid = identityValid;
            SelectedRoot = selectedRoot;
            Viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
            PanelRightGap = PanelParent.ClientSize.Width - panel.Right;
            MapRightGap = MapParent.ClientSize.Width - map.Right;
        }

        internal void Subscribe()
        {
            Main.Resize += Changed;
            Main.HandleCreated += Changed;
            Main.Layout += LaidOut;
            Main.Disposed += MainDisposed;
            Sidebar.LocationChanged += Changed;
            Sidebar.SizeChanged += Changed;
            Sidebar.VisibleChanged += Changed;
            Sidebar.ParentChanged += Changed;
            Panel.ParentChanged += Changed;
            Map.ParentChanged += Changed;
            Sidebar.Disposed += Changed;
            Panel.Disposed += Changed;
            Map.Disposed += Changed;
            if (SelectedRoot != null)
                SelectedRoot.IsVisibleChanged += RootVisibilityChanged;
        }

        private void LaidOut(object sender, LayoutEventArgs args)
        {
            Schedule();
        }
        private void Changed(object sender, EventArgs args)
        {
            Schedule();
        }
        private void MainDisposed(object sender, EventArgs args)
        {
            Rollback();
        }
        private void RootVisibilityChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs args)
        {
            Schedule();
        }

        private void Schedule()
        {
            if (Disposed || Updating || !Main.IsHandleCreated || Interlocked.Exchange(ref Queued, 1) != 0)
                return;
            try
            {
                Main.BeginInvoke((System.Windows.Forms.MethodInvoker)delegate
                {
                    Interlocked.Exchange(ref Queued, 0);
                    if (RollbackPending)
                    {
                        RollbackPending = false;
                        Rollback();
                    }
                    else if (!Disposed)
                        Synchronize();
                });
            }
            catch (Exception error)
            {
                Interlocked.Exchange(ref Queued, 0);
                Log("Sidebar UI scheduling failed; restoration requested: " + error.Message);
                if (!Main.InvokeRequired)
                    Rollback();
                else
                    RollbackPending = true;
            }
        }

        internal void Synchronize()
        {
            if (Disposed || Updating)
                return;
            Updating = true;
            try
            {
                if (Main.IsDisposed || Sidebar.IsDisposed || Panel.IsDisposed || Map.IsDisposed || !IdentityValid()
                    || !ReferenceEquals(Panel.Parent, PanelParent) || !ReferenceEquals(Map.Parent, MapParent)
                    || !ReferenceEquals(Sidebar.Parent, PanelParent))
                    throw new InvalidOperationException("Sidebar action targets were replaced or reparented.");
                if (!Viewport.IsCurrent)
                    throw new InvalidOperationException("The renderer or picking camera was replaced.");
                int fullWidth = Math.Max(1, PanelParent.ClientSize.Width - Panel.Left - PanelRightGap);
                int fullRight = Panel.Left + fullWidth;
                int width = fullWidth;
                // A hidden/sliding-offscreen sidebar restores the ordinary full viewport.
                if (Visible() && Sidebar.Right > Panel.Left && Sidebar.Left < fullRight)
                {
                    int available = Sidebar.Left - Panel.Left;
                    if (available >= 64)
                        width = Math.Min(fullWidth, available);
                }
                bool needsBounds = Panel.Width != width || Map.Width != Math.Max(1, width - Panel.Padding.Horizontal - Map.Left - MapRightGap);
                if ((needsBounds || !Viewport.RendererSizeMatches) && !Viewport.CanResize)
                {
                    WaitForViewport();
                    return;
                }
                Clamping = width < fullWidth;
                RightEdge = Panel.Left + width;
                if (Panel.Width != width)
                    Panel.Width = width;
                int mapWidth = Math.Max(1, Panel.ClientSize.Width - Map.Left - MapRightGap);
                if (Map.Width != mapWidth)
                    Map.Width = mapWidth;
                if (!Viewport.RendererSizeMatches)
                    ResizeViewport();
                if (!Viewport.Matches)
                    WaitForViewport();
                else
                    StopMaintenance();
                if (width != LastWidth)
                {
                    LastWidth = width;
                    Log("Sidebar layout: mapPanel=" + width + " map=" + Map.Width + " sidebarX=" + Sidebar.Left + " reserved=" + Clamping);
                }
            }
            catch (Exception error)
            {
                Log("Sidebar layout rolled back after failure: " + error);
                Rollback();
            }
            finally
            {
                Updating = false;
                if (RollbackPending)
                {
                    RollbackPending = false;
                    Rollback();
                }
            }
        }

        private void ResizeViewport()
        {
            if (Resizing)
                throw new InvalidOperationException("Nested renderer resize withheld.");
            Resizing = true;
            try
            {
                Viewport.Resize();
            }
            finally { Resizing = false; }
        }

        private void WaitForViewport()
        {
            if (Maintenance != null)
                return;
            WaitingTicks = 0;
            Maintenance = new Timer { Interval = 50 };
            Maintenance.Tick += Maintain;
            Maintenance.Start();
        }

        private void StopMaintenance()
        {
            if (Maintenance != null)
            {
                Maintenance.Stop();
                Maintenance.Dispose();
                Maintenance = null;
            }
            WaitingTicks = 0;
        }

        private void Maintain(object sender, EventArgs args)
        {
            try
            {
                if (Resizing)
                    return;
                if (Main.IsDisposed || Map.IsDisposed)
                {
                    FinishRestoration();
                    return;
                }
                if (RestorePending)
                {
                    RestoreViewport();
                    return;
                }
                if (++WaitingTicks > 40)
                {
                    Log("Viewport did not settle; rolling back the sidebar layout.");
                    Rollback();
                }
                else
                    Synchronize();
            }
            catch (Exception error) { Log("Viewport maintenance failed: " + error); Rollback(); }
        }

        internal void CheckMaintenance()
        {
            Maintain(this, EventArgs.Empty);
        }

        private void FinishRestoration()
        {
            RestorePending = false;
            if (Disposed)
                RollbackPending = false;
            if (ReferenceEquals(Restoring, this))
                Restoring = null;
            StopMaintenance();
        }

        private void RestoreViewport()
        {
            try
            {
                RestoreViewportCore();
            }
            catch (Exception error)
            {
                RestoreFailures++;
                Try(WaitForViewport, "restoration scheduling");
                if (Maintenance != null)
                    Maintenance.Interval = RestoreFailures >= 3 ? 5000 : 500;
                if (RestoreFailures <= 3 || (RestoreFailures & (RestoreFailures - 1)) == 0)
                    Log("Viewport restoration inspection remains pending: " + error.Message);
            }
        }

        private void RestoreViewportCore()
        {
            if (Resizing)
            {
                RollbackPending = true;
                return;
            }
            if (Main.IsDisposed || Map.IsDisposed || !ReferenceEquals(Map.Parent, MapParent) || !Viewport.IsCurrent)
            {
                Log("Old viewport restoration stopped: original live targets are unavailable.");
                FinishRestoration();
                return;
            }
            if (Viewport.Matches)
            {
                FinishRestoration();
                return;
            }
            WaitForViewport();
            if (Maintenance != null && ++WaitingTicks > 40 && RestoreFailures < 3)
                Maintenance.Interval = 500;
            if (Viewport.RendererSizeMatches || !Viewport.CanResize)
                return;
            try
            {
                ResizeViewport();
                if (Viewport.Matches)
                    FinishRestoration();
            }
            catch (Exception error)
            {
                RestoreFailures++;
                if (Maintenance != null)
                    Maintenance.Interval = RestoreFailures >= 3 ? 5000 : 500;
                if (RestoreFailures <= 3 || (RestoreFailures & (RestoreFailures - 1)) == 0)
                    Log("Viewport restoration remains pending; retrying when idle (attempt " + RestoreFailures + "): " + error.Message);
            }
        }

        private void Try(Action action, string name)
        {
            try
            {
                action();
            }
            catch (Exception error) { Log("Sidebar rollback " + name + " failed: " + error.Message); }
        }

        internal void Rollback()
        {
            if (Resizing)
            {
                RollbackPending = true;
                return;
            }
            if (Disposed)
            {
                if (RestorePending)
                    RestoreViewport();
                return;
            }
            Disposed = true;
            Clamping = false;
            if (ReferenceEquals(Current, this))
                Current = null;
            StopMaintenance();
            Try(() => Main.Resize -= Changed, "main resize subscription");
            Try(() => Main.HandleCreated -= Changed, "main handle subscription");
            Try(() => Main.Layout -= LaidOut, "main layout subscription");
            Try(() => Main.Disposed -= MainDisposed, "main disposal subscription");
            Try(() => Sidebar.LocationChanged -= Changed, "sidebar location subscription");
            Try(() => Sidebar.SizeChanged -= Changed, "sidebar size subscription");
            Try(() => Sidebar.VisibleChanged -= Changed, "sidebar visibility subscription");
            Try(() => Sidebar.ParentChanged -= Changed, "sidebar parent subscription");
            Try(() => Panel.ParentChanged -= Changed, "panel parent subscription");
            Try(() => Map.ParentChanged -= Changed, "map parent subscription");
            Try(() => Sidebar.Disposed -= Changed, "sidebar disposal subscription");
            Try(() => Panel.Disposed -= Changed, "panel disposal subscription");
            Try(() => Map.Disposed -= Changed, "map disposal subscription");
            Try(() => { if (SelectedRoot != null) SelectedRoot.IsVisibleChanged -= RootVisibilityChanged; }, "WPF visibility subscription");
            Try(() =>
            {
                if (!Panel.IsDisposed && !PanelParent.IsDisposed && ReferenceEquals(Panel.Parent, PanelParent))
                    Panel.Width = Math.Max(1, PanelParent.ClientSize.Width - Panel.Left - PanelRightGap);
            }, "original panel width");
            Try(() =>
            {
                if (!Map.IsDisposed && !MapParent.IsDisposed && ReferenceEquals(Map.Parent, MapParent))
                    Map.Width = Math.Max(1, MapParent.ClientSize.Width - Map.Left - MapRightGap);
            }, "original map width");
            Try(() => { if (!Main.IsDisposed) Main.PerformLayout(); }, "original layout");
            RestorePending = true;
            Restoring = this;
            RestoreViewport();
        }
    }

    private sealed class FixtureViewport : IMapViewport
    {
        private readonly Control Map;
        private System.Drawing.Size RenderSize, CameraSize;
        internal int Calls, Depth, MaximumDepth;
        internal bool Busy, DelayCamera, ThrowNext, FailAfterChange;
        internal bool CurrentIdentity = true;
        internal int FailuresRemaining;
        internal Action DuringResize;
        internal FixtureViewport(Control map)
        {
            Map = map;
            RenderSize = CameraSize = map.ClientSize;
        }
        public bool IsCurrent
        {
            get
            {
                return CurrentIdentity;
            }
        }
        public bool CanResize
        {
            get
            {
                return !Busy;
            }
        }
        public bool RendererSizeMatches
        {
            get
            {
                return RenderSize == Map.ClientSize;
            }
        }
        public bool Matches
        {
            get
            {
                return RendererSizeMatches && CameraSize == Map.ClientSize;
            }
        }
        internal void CompleteFrame()
        {
            CameraSize = RenderSize;
        }
        public void Resize()
        {
            Calls++;
            Depth++;
            MaximumDepth = Math.Max(MaximumDepth, Depth);
            try
            {
                if (ThrowNext)
                {
                    ThrowNext = false;
                    throw new InvalidOperationException("Injected viewport failure.");
                }
                if (FailuresRemaining > 0 && !FailAfterChange)
                {
                    FailuresRemaining--;
                    throw new InvalidOperationException("Transient restore failure.");
                }
                RenderSize = Map.ClientSize;
                if (!DelayCamera)
                    CameraSize = RenderSize;
                if (FailAfterChange)
                {
                    FailAfterChange = false;
                    throw new InvalidOperationException("Partial resize failure.");
                }
                if (DuringResize != null)
                    DuringResize();
            }
            finally { Depth--; }
        }
    }

    // Uses owned WinForms controls, with no game, WPF content, or visible window.
    internal static void SelfTest()
    {
        EnsureBoundsHook();
        using (Form form = new Form())
        using (Panel sidebar = new Panel())
        using (Panel panel = new Panel())
        using (Panel map = new Panel())
        {
            form.ClientSize = new System.Drawing.Size(1728, 1056);
            panel.SetBounds(0, 87, 1728, 943);
            panel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            sidebar.SetBounds(1452, 0, 270, 3000);
            form.Controls.Add(panel);
            form.Controls.Add(sidebar);
            panel.Controls.Add(map);
            map.Dock = DockStyle.Fill;
            bool visible = true, valid = true;
            FixtureViewport viewport = new FixtureViewport(map);
            LayoutState state = new LayoutState(form, sidebar, panel, map, () => visible, () => valid, viewport);
            Current = state;
            state.Subscribe();
            try
            {
                state.Synchronize();
                Assert(panel.Width == 1452 && map.Width == 1452 && viewport.Matches && viewport.Calls == 1, "initial reservation and viewport synchronization");
                panel.Width = 1728;
                map.Width = 1728;
                Assert(panel.Width == 1452 && map.Width == 1452, "game layout overwrite clamp");
                form.ClientSize = new System.Drawing.Size(1400, 900);
                sidebar.Left = 1124;
                state.Synchronize();
                Assert(panel.Width == 1124 && map.Width == 1124, "window resize");
                visible = false;
                state.Synchronize();
                Assert(panel.Width == 1400 && map.Width == 1400, "hidden-sidebar restoration");
                visible = true;
                sidebar.Left = 1408;
                state.Synchronize();
                Assert(panel.Width == 1400 && map.Width == 1400, "offscreen-sidebar restoration");
                sidebar.Left = 1124;
                state.Synchronize();
                int calls = viewport.Calls;
                viewport.DelayCamera = true;
                visible = false;
                state.Synchronize();
                Assert(viewport.RendererSizeMatches && !viewport.Matches, "camera may settle on next frame");
                state.Synchronize();
                Assert(viewport.Calls == calls + 1, "camera lag does not repeat GPU resize");
                viewport.CompleteFrame();
                state.Synchronize();
                viewport.DelayCamera = false;
                viewport.Busy = true;
                visible = true;
                state.Synchronize();
                Assert(map.Width == 1400 && viewport.Matches, "busy renderer postpones bounds changes");
                viewport.Busy = false;
                state.Synchronize();
                Assert(map.Width == 1124 && viewport.Matches, "idle retry synchronizes bounds and viewport");
                valid = false;
                state.Synchronize();
                Assert(Current == null && panel.Width == 1400 && map.Width == 1400 && viewport.Matches, "identity-change rollback restores viewport");
                panel.Width = 1300;
                Assert(panel.Width == 1300, "rollback disables enforcement");
                panel.Width = 1400;
                valid = true;
                viewport = new FixtureViewport(map) { ThrowNext = true };
                state = new LayoutState(form, sidebar, panel, map, () => visible, () => valid, viewport);
                Current = state;
                state.Subscribe();
                state.Synchronize();
                Assert(Current == null && map.Width == 1400 && viewport.Matches, "resize failure restores original geometry");
                viewport = new FixtureViewport(map);
                state = new LayoutState(form, sidebar, panel, map, () => visible, () => valid, viewport);
                viewport.DuringResize = () => state.Rollback();
                Current = state;
                state.Subscribe();
                state.Synchronize();
                Assert(Current == null && map.Width == 1400 && viewport.Matches && viewport.MaximumDepth == 1,
                    "reentrant rollback waits for resize return");
                viewport = new FixtureViewport(map) { FailAfterChange = true, FailuresRemaining = 4 };
                state = new LayoutState(form, sidebar, panel, map, () => visible, () => valid, viewport);
                Current = state;
                state.Subscribe();
                state.Synchronize();
                Assert(Current == null && !viewport.Matches, "partial failure retains pending restoration");
                for (int i = 0; i < 5; i++)
                    state.CheckMaintenance();
                Assert(viewport.Matches && viewport.Calls >= 6 && Restoring == null,
                    "restoration recovers after more than three transient failures");
                viewport = new FixtureViewport(map);
                state = new LayoutState(form, sidebar, panel, map, () => visible, () => valid, viewport);
                Current = state;
                state.Subscribe();
                state.Synchronize();
                calls = viewport.Calls;
                viewport.CurrentIdentity = false;
                valid = false;
                state.Synchronize();
                Assert(Current == null && map.Width == 1400 && viewport.Calls == calls,
                    "replaced map is never passed to the old renderer");
            }
            finally { state.Rollback(); Current = null; Restoring = null; }
        }
        Patcher.Unpatch(typeof(Control).GetMethod("SetBoundsCore", BindingFlags.Instance | BindingFlags.NonPublic), HarmonyPatchType.Prefix, Owner);
        BoundsHooked = false;
    }

    private static void Assert(bool value, string label)
    {
        if (!value)
            throw new InvalidOperationException("Sidebar layout self-test failed: " + label);
    }
}
