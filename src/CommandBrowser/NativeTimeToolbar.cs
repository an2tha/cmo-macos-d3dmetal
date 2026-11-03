using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Presentation of the reviewed MainForm time controls. Commands remain owned
// by their original ToolStripItems; no game state or item layout is rewritten.
internal sealed class NativeTimeToolbar
{
    internal sealed class Item
    {
        internal Rectangle Bounds;
        internal string Text, Symbol;
        internal bool HideImage, ForceText;
        internal ToolStripDropDownItem MenuSource;
    }
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Form form;
    private readonly ToolStrip strip;
    private readonly ToolStripItem play, rate, mode;
    private readonly FieldInfo playImage, pauseImage;
    private readonly PropertyInfo currentScenario, timeCompression;
    private string lastTrace;

    private NativeTimeToolbar(Form owner, ToolStrip toolbar, ToolStripItem playButton)
    {
        form=owner;strip=toolbar;play=playButton;
        rate=Find("TSB_TimeAccel");mode=Find("ButtonGameSpeed");
        playImage=form.GetType().GetField("_image_StartResume",Members);
        pauseImage=form.GetType().GetField("_image_StopPause",Members);
        Type client=form.GetType().Assembly.GetType("Command.Client");
        currentScenario=client?.GetProperty("CurrentScenario",BindingFlags.Public|BindingFlags.Static);
        timeCompression=currentScenario?.PropertyType.GetProperty("TimeCompression",BindingFlags.Public|BindingFlags.Instance);
    }
    private ToolStripItem Find(string name)
    {
        ToolStripItem item=strip.Items[name];
        if(item!=null)return item;
        return form.GetType().GetProperty(name,Members)?.GetValue(form) as ToolStripItem;
    }
    internal static NativeTimeToolbar Create(Form form,ToolStrip strip,bool fixture)
    {
        if((!fixture && form.GetType().FullName!="Command.MainForm") || strip.Name!="ToolStrip1")return null;
        ToolStripItem play=strip.Items["TSB_StartGame"] ?? form.GetType().GetProperty("TSB_StartGame",Members)?.GetValue(form) as ToolStripItem;
        return play?.Owner==strip ? new NativeTimeToolbar(form,strip,play) : null;
    }
    private static string Caption(string text)
    {return (text ?? "").Replace("&&","\u0001").Replace("&","").Replace("\u0001","&").Trim();}
    private static string WithoutPrefix(string text)
    {
        string value=Caption(text);
        foreach(string prefix in new[]{"Time Compression:","Time Mode:"})
            if(value.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))value=value.Substring(prefix.Length).Trim();
        return value;
    }
    private static IEnumerable<ToolStripMenuItem> Choices(ToolStripItem root)
    {
        var dropdown=root as ToolStripDropDownItem;
        if(dropdown==null)yield break;
        foreach(ToolStripItem item in dropdown.DropDownItems)
        {
            var choice=item as ToolStripMenuItem;
            if(choice!=null)yield return choice;
            foreach(var child in Choices(item))yield return child;
        }
    }
    private string RateCaption()
    {
        string selected=null;
        if(currentScenario!=null && timeCompression!=null)
        {
            object scenario=currentScenario.GetValue(null);
            if(scenario!=null)selected=Convert.ToString(timeCompression.GetValue(scenario));
        }
        string name=null,fallback=null;
        switch(selected)
        {
            case "OneSec":name="TSMI_TimeAccel1x";fallback="1×";break;
            case "TwoSec":name="TSMI_TimeAccel2x";fallback="2×";break;
            case "FiveSec":name="TSMI_TimeAccel5x";fallback="5×";break;
            case "FifteenSec":name="TSMI_TimeAccel15x";fallback="15×";break;
            case "Coarse_OneSecSlice":name="TSMI_TimeAccelTurbo";break;
            case "Coarse_FiveSecSlice":name="TSMI_TimeAccelDoubleFlame";break;
        }
        if(fallback!=null)return fallback;
        var choices=Choices(rate).ToArray();
        var option=name==null?choices.FirstOrDefault(c=>c.Checked):choices.FirstOrDefault(c=>c.Name==name);
        string text=option!=null?Caption(option.Text):WithoutPrefix(rate?.Text);
        if(text.EndsWith("x",StringComparison.OrdinalIgnoreCase) && int.TryParse(text.Substring(0,text.Length-1).Trim(),out _))text=text.Substring(0,text.Length-1).Trim()+"×";
        return string.IsNullOrWhiteSpace(text)?"Speed":text;
    }
    private string PlayCaption()
    {
        if(pauseImage!=null && play.Image!=null && ReferenceEquals(play.Image,pauseImage.GetValue(form)))return "Pause";
        if(playImage!=null && play.Image!=null && ReferenceEquals(play.Image,playImage.GetValue(form)))return "Play";
        string text=Caption(play.Text);
        if(text.IndexOf("Pause",StringComparison.OrdinalIgnoreCase)>=0 || text.IndexOf("Stop",StringComparison.OrdinalIgnoreCase)>=0)return "Pause";
        if(text.IndexOf("Start",StringComparison.OrdinalIgnoreCase)>=0 || text.IndexOf("Resume",StringComparison.OrdinalIgnoreCase)>=0 || text=="Play")return "Play";
        return text;
    }
    internal Dictionary<ToolStripItem,Item> Layout()
    {
        var visible=strip.Items.Cast<ToolStripItem>().Where(i=>i.Available).ToArray();
        // Keep the original overflow and custom-host implementation when a row
        // cannot be wholly mirrored. Never cover an unrepresented control.
        if(visible.Any(i=>i.Placement!=ToolStripItemPlacement.Main || i is ToolStripControlHost) || strip.OverflowButton.Visible)return null;
        // The game can retain the compression menu while hiding its original
        // image button. Replace the visible caption slot, not that hidden box.
        var selector=visible.FirstOrDefault(i=>i is ToolStripLabel && Caption(i.Text).StartsWith("Time Compression:",StringComparison.OrdinalIgnoreCase))
            ?? visible.FirstOrDefault(i=>i==rate);
        var rateMenu=rate as ToolStripDropDownItem;
        if(selector==null || rateMenu==null || rateMenu.DropDownItems.Count==0)return null;
        float dpi=strip.DeviceDpi/96f;
        int x=strip.DisplayRectangle.Left;
        var result=new Dictionary<ToolStripItem,Item>();
        string selected=RateCaption(),playText=PlayCaption();
        foreach(ToolStripItem item in visible)
        {
            if(item==rate && item!=selector)continue;
            if(item is ToolStripSeparator){x+=(int)Math.Ceiling(6*dpi);continue;}
            string text=Caption(item.Text);
            if(item is ToolStripLabel && item!=selector)
            {
                text=WithoutPrefix(text);
                if(text.Length==0)continue;
            }
            var entry=new Item{Bounds=item.Bounds,Text=text};
            int width=item.Bounds.Width;
            if(item==selector || item==mode)
            {
                entry.Text=item==selector?selected:WithoutPrefix(item.Text);
                if(entry.Text.Length==0)entry.Text=selected;
                entry.HideImage=true;
                entry.ForceText=true;
                if(item==selector)entry.MenuSource=rateMenu;
                width=Math.Max((int)Math.Ceiling(56*dpi),TextRenderer.MeasureText(entry.Text,item.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+(int)Math.Ceiling(22*dpi));
            }
            if(item==play)
            {
                entry.Text=playText;
                entry.ForceText=true;
                if(playText=="Play" || playText=="Pause")entry.Symbol=playText=="Play"?"play.fill":"pause.fill";
                width=Math.Max((int)Math.Ceiling(76*dpi),TextRenderer.MeasureText(entry.Text,item.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+(int)Math.Ceiling(30*dpi));
            }
            int height=item.Bounds.Height>0?item.Bounds.Height:play.Bounds.Height;
            entry.Bounds=new Rectangle(x,item.Bounds.Y,width,height);
            x+=width+(int)Math.Ceiling(4*dpi);result[item]=entry;
        }
        if(x>strip.DisplayRectangle.Right)return null;
        string trace=selected+" / "+playText;
        if(Environment.GetEnvironmentVariable("CMO_NATIVE_TRACE")=="1" && trace!=lastTrace)
        {lastTrace=trace;NativeHost.Logger?.Invoke("Native time controls: "+trace+"; selector="+selector.Name+" ("+selector.GetType().Name+"); source="+rate.Name+" available="+rate.Available+" bounds="+rate.Bounds);}
        return result;
    }
}
