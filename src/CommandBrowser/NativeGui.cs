using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using HarmonyLib;

// Native peers keep the game's original models and event handlers authoritative.
// Unsupported/custom-drawn controls remain visible and interactive in Wine.
internal static class NativeGui
{
    private static readonly ConcurrentDictionary<int, Action<Dictionary<string, object>>> Actions = new ConcurrentDictionary<int, Action<Dictionary<string, object>>>();
    private static readonly ConditionalWeakTable<object, Identity> Identities = new ConditionalWeakTable<object, Identity>();
    private static readonly HashSet<Form> Forms = new HashSet<Form>();
    private static readonly ConditionalWeakTable<Control, object> NativePaint = new ConditionalWeakTable<Control, object>();
    private static readonly ConditionalWeakTable<Image, ImageData> Images = new ConditionalWeakTable<Image, ImageData>();
    private static readonly ConditionalWeakTable<ToolStrip, PaletteTrace> Palettes = new ConditionalWeakTable<ToolStrip, PaletteTrace>();
    private sealed class ImageData { internal string Png; }
    private sealed class PaletteTrace { internal string Last; }
    internal static long SuppressedPaints;
    private static int Serial = 100000;
    private sealed class Identity { internal int Value = ++Serial; }
    internal static int Id(object value) { return Identities.GetValue(value, _ => new Identity()).Value; }
    private static Harmony Patcher;
    private static bool Fixtures;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr window, IntPtr rect);

    internal static Dictionary<string, object> Geometry(Control control, bool outer = false)
    {
        Form form = control.FindForm();
        if (form == null || !form.IsHandleCreated) throw new InvalidOperationException("The native view needs an existing game window.");
        Rect window;
        if (!GetWindowRect(form.Handle, out window)) throw new InvalidOperationException("Cannot locate the game window.");
        Point origin = outer && control.Parent != null ? control.Parent.PointToScreen(control.Location) : control.PointToScreen(Point.Empty);
        Size size = outer ? control.Size : control.ClientSize;
        Color background = Background(control);
        var button = control as ButtonBase;
        if (button != null && button.UseVisualStyleBackColor && control.Parent != null) background = Background(control.Parent);
        Point client = form.PointToScreen(Point.Empty);
        // Clip to every ancestor's client area, preserving scrolling and tabs.
        Rectangle clip = new Rectangle(origin, size);
        for (Control parent = control.Parent; parent != null; parent = parent.Parent)
            clip.Intersect(new Rectangle(parent.PointToScreen(Point.Empty), parent.ClientSize));
        return new Dictionary<string, object> {
            {"hwnd", form.Handle.ToInt64()}, {"wx", window.Left}, {"wy", window.Top}, {"ww", window.Right-window.Left}, {"wh", window.Bottom-window.Top},
            {"fx", client.X}, {"fy", client.Y}, {"fw", form.ClientSize.Width}, {"fh", form.ClientSize.Height},
            {"x", origin.X}, {"y", origin.Y}, {"w", size.Width}, {"h", size.Height},
            {"cx", clip.X}, {"cy", clip.Y}, {"cw", Math.Max(0, clip.Width)}, {"ch", Math.Max(0, clip.Height)},
            {"background", new[] { background.R, background.G, background.B }},
            {"visible", control.Visible && form.Visible && form.WindowState != FormWindowState.Minimized}, {"enabled", control.Enabled}
        };
    }

    internal static void Install(bool fixtures = false)
    {
        Fixtures = fixtures;
        Patcher = new Harmony("cmo.native.gui");
        Patcher.Patch(typeof(Form).GetMethod("OnShown", BindingFlags.Instance | BindingFlags.NonPublic),
            postfix: new HarmonyMethod(typeof(NativeGui).GetMethod("Shown", BindingFlags.Static | BindingFlags.NonPublic)));
        Patcher.Patch(typeof(Control).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic),
            prefix: new HarmonyMethod(typeof(NativeGui).GetMethod("Paint", BindingFlags.Static | BindingFlags.NonPublic)));
        NativeHost.Event += Receive;
    }
    private static bool Paint(Control __instance, ref System.Windows.Forms.Message m)
    {
        object marker;
        if ((m.Msg != 0x000f && m.Msg != 0x0014 && m.Msg != 0x0085) || !NativePaint.TryGetValue(__instance, out marker)) return true;
        if (m.Msg == 0x000f && !ValidateRect(m.HWnd, IntPtr.Zero)) return true;
        m.Result = m.Msg == 0x0014 ? new IntPtr(1) : IntPtr.Zero;
        System.Threading.Interlocked.Increment(ref SuppressedPaints);
        return false;
    }
    private static string ImagePng(Image image)
    {
        if (image == null) return null;
        return Images.GetValue(image, value => {
            using (var stream = new MemoryStream()) {
                value.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                return new ImageData { Png = Convert.ToBase64String(stream.ToArray()) };
            }
        }).Png;
    }
    private static string Caption(string text) { return text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&"); }
    private static byte[] Rgb(Color color) { return new[] {color.R, color.G, color.B}; }
    private static Color Background(Control control)
    {
        Color color = control.BackColor;
        if (color.A == 255 || control.Parent == null) return color;
        Color behind = Background(control.Parent);
        int a = color.A;
        return Color.FromArgb((color.R*a + behind.R*(255-a))/255,
            (color.G*a + behind.G*(255-a))/255, (color.B*a + behind.B*(255-a))/255);
    }
    private static void Typography(Dictionary<string, object> view, Font font, Control owner, Color foreground)
    {
        // AppKit lays out in content-view points, not GDI font points. The native
        // side applies the same window scale as the control's pixel rectangle.
        view["font"] = font.SizeInPoints * owner.DeviceDpi / 72.0;
        view["bold"] = font.Bold;
        view["foreground"] = Rgb(foreground);
        view["systemForeground"] = foreground.IsSystemColor;
    }
    private static Color StripBackground(ToolStrip strip)
    {
        Color background = Background(strip);
        if (strip.Width <= 0 || strip.Height <= 0) return background;
        // A renderer may override background painting without overriding its
        // ColorTable or BackColor. Sample its background-only pass, excluding
        // text, icons and selection state. The tiny target clips the full-size
        // render, retaining gradient coordinates without a full toolbar bitmap.
        Color rendered;
        using (var sample = new Bitmap(3, 3))
        using (Graphics graphics = Graphics.FromImage(sample))
        {
            graphics.Clear(background);
            graphics.TranslateTransform(1 - strip.Width / 2, 1 - strip.Height / 2);
            strip.Renderer.DrawToolStripBackground(new ToolStripRenderEventArgs(graphics, strip, strip.ClientRectangle, background));
            rendered = sample.GetPixel(1, 1);
        }
        if (Environment.GetEnvironmentVariable("CMO_NATIVE_TRACE") == "1")
        {
            var details = new Dictionary<string, object> {
                {"control", strip.Name}, {"renderer", strip.Renderer.GetType().FullName},
                {"declared", Rgb(background)}, {"rendered", Rgb(rendered)}, {"foreground", Rgb(strip.ForeColor)}
            };
            string current = NativeHost.Encode(details);
            PaletteTrace trace = Palettes.GetValue(strip, _ => new PaletteTrace());
            if (trace.Last != current) { trace.Last = current; NativeHost.Logger?.Invoke("Native toolbar palette " + current); }
        }
        return rendered;
    }
    private static void Shown(Form __instance)
    {
        if (!Fixtures && __instance.GetType().Assembly.GetName().Name != "Command") return;
        if (!Forms.Add(__instance)) return;
        new Mirror(__instance).Start();
    }
    private static void Receive(Dictionary<string, object> message)
    {
        object kind;
        if (!message.TryGetValue("event", out kind) || Convert.ToString(kind) != "action") return;
        int id = Convert.ToInt32(message["control"]);
        Action<Dictionary<string, object>> action;
        if (Actions.TryGetValue(id, out action)) action(message);
    }
    private static void Register(int id, Control owner, Action<Dictionary<string, object>> action)
    {
        Actions[id] = message => {
            try { owner.BeginInvoke((Action)(() => {
                if (owner.IsDisposed) return;
                try { action(message); } catch (Exception error) { NativeHost.Logger?.Invoke("Native control action failed: " + error); }
            })); } catch (InvalidOperationException) { }
        };
    }
    private static void RemoveAction(int id) { Action<Dictionary<string, object>> removed; Actions.TryRemove(id, out removed); }

    private sealed class Mirror
    {
        private readonly Form form;
        private readonly int formId;
        private readonly Timer timer = new Timer { Interval = 100 };
        private HashSet<int> owned = new HashSet<int>();
        private readonly Dictionary<int, Control> painted = new Dictionary<int, Control>();
        private NativeTimeToolbar timeToolbar;
        private string last;
        private bool sending, disposed;
        internal Mirror(Form value) { form = value; formId = Id(form); }
        internal void Start()
        {
            form.Disposed += (s,e) => { disposed = true; timer.Dispose(); Forms.Remove(form); foreach(int id in owned) RemoveAction(id); RestorePaint(); Observe(NativeHost.Request("guiDispose", formId)); };
            timer.Tick += (s,e) => Tick(); timer.Start(); Tick();
        }
        private async void Observe(Task<Dictionary<string, object>> task)
        { try { await task; } catch(Exception error) { NativeHost.Logger?.Invoke("Native GUI: " + error.Message); } }
        private void RestorePaint()
        { foreach(Control control in painted.Values) { NativePaint.Remove(control); if(!control.IsDisposed) control.Invalidate(); } painted.Clear(); }
        private async void Tick()
        {
            if (sending || disposed || form.IsDisposed) return;
            try
            {
                var views = new List<object>(); var next = new HashSet<int>();
                Walk(form, views, next);
                foreach(int id in owned) if (!next.Contains(id)) RemoveAction(id);
                owned = next;
                var values = new Dictionary<string, object>{{"views", views}};
                string snapshot = NativeHost.Encode(values); if (snapshot == last) return;
                sending = true;
                var response = await NativeHost.Request("gui", formId, values);
                if (disposed) return;
                var attached = new HashSet<int>(((IEnumerable)response["attached"]).Cast<object>().Select(Convert.ToInt32));
                foreach(int id in painted.Keys.ToArray()) if(!attached.Contains(id)) { NativePaint.Remove(painted[id]); painted[id].Invalidate(); painted.Remove(id); }
                foreach(Control control in Descendants(form)) if(attached.Contains(Id(control)) && !painted.ContainsKey(Id(control))) {
                    NativePaint.GetValue(control, _ => new object()); painted[Id(control)] = control;
                }
                if (last == null) NativeHost.Logger?.Invoke("Native GUI attached to " + form.GetType().FullName + "; controls=" + views.Count);
                last = snapshot;
            }
            catch(Exception error) { RestorePaint(); NativeHost.Logger?.Invoke("Native GUI synchronization: " + error.Message); }
            finally { sending = false; }
        }
        private static IEnumerable<Control> Descendants(Control root)
        { foreach(Control child in root.Controls) { yield return child; foreach(Control nested in Descendants(child)) yield return nested; } }
        private void Walk(Control control, List<object> views, HashSet<int> next)
        {
            if (control.IsDisposed || !control.Visible) return;
            if (control is ToolStrip) AddToolStrip((ToolStrip)control, views, next);
            else {
                string kind = Kind(control);
                if (kind != null) {
                    int id = Id(control); next.Add(id);
                    var view = Geometry(control, true); view["id"] = id; view["kind"] = kind;
                    view["text"] = Caption(control.Text);
                    Typography(view, control.Font, control, control.ForeColor);
                    view["tooltip"] = control.AccessibleDescription ?? "";
                    view["accessibility"] = control.AccessibleName ?? control.Text;
                    if (control is ButtonBase) view["image"] = ImagePng(((ButtonBase)control).Image);
                    Register(id, control, message => { if (!control.IsDisposed && control.Visible && control.Enabled) Act(control, message); });
                    if (control is CheckBox) view["value"] = (int)((CheckBox)control).CheckState;
                    if (control is RadioButton) view["value"] = ((RadioButton)control).Checked ? 1 : 0;
                    if (control is TextBox) { view["multiline"] = ((TextBox)control).Multiline; view["password"] = ((TextBox)control).UseSystemPasswordChar || ((TextBox)control).PasswordChar != '\0'; view["readonly"] = ((TextBox)control).ReadOnly; }
                    if (control is ComboBox) { var c=(ComboBox)control; view["items"] = c.Items.Cast<object>().Select(c.GetItemText).ToArray(); view["value"] = c.SelectedIndex; }
                    if (control is NumericUpDown) {var c=(NumericUpDown)control; view["value"]=c.Value; view["min"]=c.Minimum; view["max"]=c.Maximum; view["step"]=c.Increment;}
                    if (control is TrackBar) {var c=(TrackBar)control; view["value"]=c.Value; view["min"]=c.Minimum; view["max"]=c.Maximum;}
                    if (control is ProgressBar) {var c=(ProgressBar)control; view["value"]=c.Value; view["min"]=c.Minimum; view["max"]=c.Maximum; view["indeterminate"]=c.Style==ProgressBarStyle.Marquee;}
                    if (control is TabControl) {var c=(TabControl)control; view["items"]=c.TabPages.Cast<TabPage>().Select(p=>p.Text).ToArray(); view["value"]=c.SelectedIndex; if(c.TabCount>0) {Rectangle r=c.GetTabRect(0); view["h"]=r.Bottom+2; view["ch"]=r.Bottom+2;}}
                    if (control is ListBox) {var c=(ListBox)control; view["items"]=c.Items.Cast<object>().Select(c.GetItemText).ToArray(); view["value"]=c.SelectedIndex;}
                    if (control is TreeView) {var c=(TreeView)control; view["nodes"]=Nodes(c.Nodes); view["selected"]=c.SelectedNode==null ? 0 : Id(c.SelectedNode);}
                    views.Add(view);
                }
            }
            // Compound native peers own their children; tab pages remain game-owned.
            if (control is TextBoxBase || control is NumericUpDown || control is ComboBox || control is ListBox || control is TreeView || control is ToolStrip) return;
            foreach(Control child in control.Controls) Walk(child, views, next);
        }
        private object[] Nodes(TreeNodeCollection nodes)
        {
            return nodes.Cast<TreeNode>().Select(n => (object)new Dictionary<string, object>{{"id",Id(n)},{"text",n.Text},{"expanded",n.IsExpanded},{"children",Nodes(n.Nodes)}}).ToArray();
        }
        private void AddToolStrip(ToolStrip strip, List<object> views, HashSet<int> next)
        {
            Color background = StripBackground(strip);
            Dictionary<ToolStripItem,NativeTimeToolbar.Item> compact=null;
            if(strip.Name=="ToolStrip1")
            {
                if(timeToolbar==null)timeToolbar=NativeTimeToolbar.Create(form,strip,Fixtures);
                compact=timeToolbar?.Layout();
            }
            if(compact!=null)
            {
                var surface=Geometry(strip,true);int surfaceId=Id(strip);
                surface["id"]=surfaceId;surface["kind"]="surface";surface["background"]=Rgb(background);
                views.Add(surface);next.Add(surfaceId);
            }
            foreach(ToolStripItem item in strip.Items) {
                if (!item.Available || item.Placement != ToolStripItemPlacement.Main || item is ToolStripSeparator || item is ToolStripControlHost) continue;
                NativeTimeToolbar.Item presentation=null;
                if(compact!=null && !compact.TryGetValue(item,out presentation))continue;
                int id=Id(item); next.Add(id); var view=Geometry(strip, true); Rectangle r=presentation?.Bounds ?? item.Bounds;
                if(strip.Name=="ToolStrip_ShortcutButtons" && item.DisplayStyle==ToolStripItemDisplayStyle.Image && item.Image!=null && item.Image.Width>=2*item.Image.Height)
                {
                    // Use the banner row's reserved vertical margin as well as
                    // its icon box. Keep the original horizontal command slots.
                    r=Rectangle.FromLTRB(r.Left,Math.Max(0,r.Top-item.Margin.Top),r.Right,Math.Min(strip.ClientSize.Height,r.Bottom+item.Margin.Bottom));
                }
                view["x"] = Convert.ToInt32(view["x"])+r.X; view["y"]=Convert.ToInt32(view["y"])+r.Y; view["w"]=r.Width; view["h"]=r.Height;
                view["id"]=id; view["text"]=presentation?.Text ?? Caption(item.Text); view["enabled"]=strip.Enabled && item.Enabled;
                view["background"] = Rgb(background);
                Color foreground = item.ForeColor;
                if (foreground.IsSystemColor && background.GetBrightness() < 0.45f) foreground = Color.FromArgb(235, 237, 239);
                Typography(view, item.Font, strip, foreground);
                view["toolbar"]=true; view["menubar"]=strip is MenuStrip;
                view["tooltip"]=string.IsNullOrEmpty(item.ToolTipText) ? Caption(item.Text) : item.ToolTipText;
                bool showImage = (item.DisplayStyle & ToolStripItemDisplayStyle.Image) != 0 && presentation?.HideImage!=true;
                view["image"]=showImage && presentation?.Symbol==null ? ImagePng(item.Image) : null;
                view["imageOnly"]=presentation?.ForceText!=true && (item.DisplayStyle & ToolStripItemDisplayStyle.Text)==0;
                if (showImage && item.Image != null) {
                    Size imageSize = item.ImageScaling == ToolStripItemImageScaling.None ? item.Image.Size : strip.ImageScalingSize;
                    view["imageWidth"]=imageSize.Width; view["imageHeight"]=imageSize.Height;
                }
                if(presentation?.Symbol!=null)
                {
                    view["symbol"]=presentation.Symbol;view["imageWidth"]=view["imageHeight"]=14*strip.DeviceDpi/96.0;
                    view["tooltip"]=presentation.Text+" simulation";
                }
                if(item is ToolStripButton) {
                    var button = (ToolStripButton)item;
                    view["value"]=(int)button.CheckState;
                    view["checkable"]=button.CheckOnClick || button.Checked;
                }
                var menuSource=presentation?.MenuSource ?? item as ToolStripDropDownItem;
                view["kind"]=menuSource!=null ? "menu" : item is ToolStripLabel ? "label" : "button";
                if(presentation?.MenuSource!=null)
                {
                    view["enabled"]=strip.Enabled && menuSource.Enabled;
                    view["tooltip"]="Time compression: "+presentation.Text;
                }
                view["accessibility"]=presentation?.Text ?? item.AccessibleName ?? item.ToolTipText ?? item.Text;
                if(menuSource!=null)view["items"]=Menu(menuSource.DropDownItems,next);
                Register(id, strip, m => {if(strip.Enabled && item.Available && item.Enabled) item.PerformClick();}); views.Add(view);
            }
        }
        private object[] Menu(ToolStripItemCollection items, HashSet<int> next)
        {
            var entries = new List<object>();
            foreach(ToolStripItem item in items) {
                if(!item.Available) continue;
                if(item is ToolStripSeparator) {entries.Add(new Dictionary<string,object>{{"separator",true}}); continue;}
                int id=Id(item);next.Add(id);
                var entry=new Dictionary<string,object>{{"id",id},{"text",Caption(item.Text)},{"enabled",item.Enabled},{"checked",item is ToolStripMenuItem && ((ToolStripMenuItem)item).Checked}};
                if(item is ToolStripDropDownItem) entry["children"]=Menu(((ToolStripDropDownItem)item).DropDownItems,next);
                Register(id, form, m=>{if(item.Available && item.Enabled) item.PerformClick();});entries.Add(entry);
            }
            return entries.ToArray();
        }
    }
    private static string Kind(Control control)
    {
        if (control is Button) return "button";
        if (control is CheckBox) return "check";
        if (control is RadioButton) return "radio";
        if (control is LinkLabel) return null; // Preserve custom link handlers and hit ranges.
        if (control is Label) return "label";
        if (control is TextBox) return "text";
        if (control is ComboBox && ((ComboBox)control).DropDownStyle==ComboBoxStyle.DropDownList) return "combo";
        if (control is NumericUpDown) return "number";
        if (control is TrackBar) return "slider";
        if (control is ProgressBar) return "progress";
        if (control is TabControl && ((TabControl)control).Alignment==TabAlignment.Top && !((TabControl)control).Multiline) return "tabs";
        if (control is ListBox && ((ListBox)control).SelectionMode==SelectionMode.One) return "list";
        if (control is TreeView && !((TreeView)control).CheckBoxes && ((TreeView)control).ImageList==null) return "tree";
        return null;
    }
    private static void Act(Control control, Dictionary<string, object> message)
    {
        object value; message.TryGetValue("value",out value);
        if(control is Button) ((Button)control).PerformClick();
        else if(control is CheckBox) ((CheckBox)control).CheckState=(CheckState)Convert.ToInt32(value);
        else if(control is RadioButton) ((RadioButton)control).Checked=true;
        else if(control is TextBox) {if(!((TextBox)control).ReadOnly) control.Text=Convert.ToString(value);}
        else if(control is ComboBox) ((ComboBox)control).SelectedIndex=Convert.ToInt32(value);
        else if(control is NumericUpDown) {var c=(NumericUpDown)control; decimal parsed;if(decimal.TryParse(Convert.ToString(value),System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out parsed)) c.Value=Math.Min(c.Maximum,Math.Max(c.Minimum,parsed));}
        else if(control is TrackBar) {var c=(TrackBar)control;c.Value=Math.Min(c.Maximum,Math.Max(c.Minimum,Convert.ToInt32(value)));}
        else if(control is TabControl) ((TabControl)control).SelectedIndex=Convert.ToInt32(value);
        else if(control is ListBox) ((ListBox)control).SelectedIndex=Convert.ToInt32(value);
        else if(control is TreeView) {
            var tree=(TreeView)control; var node=FindNode(tree.Nodes,Convert.ToInt32(value));
            if(node!=null) {object expanded;if(message.TryGetValue("expanded",out expanded)) {if(Convert.ToBoolean(expanded)) node.Expand();else node.Collapse();} else tree.SelectedNode=node;}
        }
    }
    private static TreeNode FindNode(TreeNodeCollection nodes,int id) {foreach(TreeNode node in nodes) {if(Id(node)==id)return node;var child=FindNode(node.Nodes,id);if(child!=null)return child;}return null;}
}
