using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using HarmonyLib;
using Timer = System.Windows.Forms.Timer;

// Keeps the native map HWNDs disjoint from the actual selected-unit ElementHost.
// Game-owned controls, Dock/Anchor settings, and WPF content are not replaced.
internal static class PanelLayout
{
    private const string GameHash = "53fc37bb0b634adbe46f4ddf3a87c99847d273cc6f1d980ba9796da6389d25fc";
    private const string Owner = "cmo.selected-sidebar.layout";
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Action<string> Logger;
    private static Harmony Patcher;
    private static Timer Discovery;
    private static Form Pending;
    private static int Attempts;
    private static bool BoundsHooked;
    private static LayoutState Current;

    internal static void Install(string gameDirectory, Action<string> logger)
    {
        Logger = logger;
        try
        {
            string hash;
            using (SHA256 sha = SHA256.Create())
            using (Stream stream = File.OpenRead(Path.Combine(gameDirectory, "Command.exe")))
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (hash != GameHash)
            {
                Log("Sidebar layout skipped: unreviewed game hash " + hash);
                return;
            }
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
                || Current != null || Discovery != null)
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
            EnsureBoundsHook();
            Current = new LayoutState(main, sidebar, panel, map,
                () => sidebar.Visible && sidebar.Child.IsVisible,
                () => ReferenceEquals(selectedField.GetValue(main), sidebar) && ReferenceEquals(rootField.GetValue(main), root)
                    && ReferenceEquals(sidebar.Child, root) && ReferenceEquals(mapField.GetValue(main), map)
                    && ReferenceEquals(panelField.GetValue(main), panel), sidebar.Child);
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
        private readonly int PanelRightGap;
        internal readonly int MapRightGap;
        internal bool Clamping;
        internal int RightEdge;
        private int Queued;
        private bool Updating, Disposed;
        private int LastWidth = -1;

        internal LayoutState(Form main, Control sidebar, Control panel, Control map, Func<bool> visible, Func<bool> identityValid,
            System.Windows.UIElement selectedRoot = null)
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
            PanelRightGap = PanelParent.ClientSize.Width - panel.Right;
            MapRightGap = MapParent.ClientSize.Width - map.Right;
        }

        internal void Subscribe()
        {
            Main.Resize += Changed;
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
                    if (!Disposed)
                        Synchronize();
                });
            }
            catch (Exception error)
            {
                Interlocked.Exchange(ref Queued, 0);
                Clamping = false;
                if (ReferenceEquals(Current, this))
                    Current = null;
                Log("Sidebar UI is unavailable; enforcement disabled: " + error.Message);
                if (!Main.InvokeRequired)
                    Rollback();
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
                Clamping = width < fullWidth;
                RightEdge = Panel.Left + width;
                if (Panel.Width != width)
                    Panel.Width = width;
                int mapWidth = Math.Max(1, Panel.ClientSize.Width - Map.Left - MapRightGap);
                if (Map.Width != mapWidth)
                    Map.Width = mapWidth;
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
            finally { Updating = false; }
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
            if (Disposed)
                return;
            Disposed = true;
            Clamping = false;
            if (ReferenceEquals(Current, this))
                Current = null;
            Try(() => Main.Resize -= Changed, "main resize subscription");
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
            LayoutState state = new LayoutState(form, sidebar, panel, map, () => visible, () => valid);
            Current = state;
            state.Subscribe();
            try
            {
                state.Synchronize();
                Assert(panel.Width == 1452 && map.Width == 1452, "initial reservation");
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
                valid = false;
                state.Synchronize();
                Assert(Current == null && panel.Width == 1400 && map.Width == 1400, "identity-change rollback");
                panel.Width = 1300;
                Assert(panel.Width == 1300, "rollback disables enforcement");
            }
            finally { state.Rollback(); Current = null; }
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
