using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class NativeUiSelfTest
{
    private sealed class TimeForm : Form
    {
        public Image _image_StartResume=new Bitmap(2,2);
        public Image _image_StopPause=new Bitmap(2,2);
        protected override void Dispose(bool disposing)
        {base.Dispose(disposing);if(disposing){_image_StartResume.Dispose();_image_StopPause.Dispose();}}
    }
    private static async Task<Dictionary<string,object>> Inspect(ToolStripItem item)
    {await Task.Delay(250);return await NativeHost.Request("selfTestControl",0,new Dictionary<string,object>{{"control",NativeGui.Id(item)}});}
    // Model a themed renderer whose metadata still advertises a white strip.
    private sealed class WhitePalette : ProfessionalColorTable
    {
        public override Color MenuStripGradientEnd { get { return Color.White; } }
        public override Color ToolStripGradientMiddle { get { return Color.White; } }
    }
    private sealed class ThemeRenderer : ToolStripProfessionalRenderer
    {
        internal Color Surface = Color.FromArgb(60, 63, 65);
        internal ThemeRenderer() : base(new WhitePalette()) { }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        { using (var brush = new SolidBrush(Surface)) e.Graphics.FillRectangle(brush, e.ToolStrip.ClientRectangle); }
    }
    private static async Task CheckToolbar(ToolStripItem item, Color expected, bool dark)
    {
        await Task.Delay(300);
        var state=await NativeHost.Request("selfTestControl",0,new Dictionary<string,object>{{"control",NativeGui.Id(item)}});
        var rgb=(System.Collections.IList)state["background"];
        Require(Convert.ToInt32(rgb[0])==expected.R && Convert.ToInt32(rgb[1])==expected.G && Convert.ToInt32(rgb[2])==expected.B,
            "native "+item.Owner.GetType().Name+" background follows the renderer's "+(dark?"dark":"light")+" paint");
        Require(Convert.ToString(state["appearance"])==(dark?"NSAppearanceNameDarkAqua":"NSAppearanceNameAqua"),"native toolbar appearance follows the rendered surface");
    }
    private static TaskCompletionSource<bool> Loaded, Message;
    private static bool Cancelled;
    private static string LastStarted, LastMessageSource;
    private static int UiThread;
    private static Action<string> Log;
    private static object Browser;
    private static int Result = 1;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); Log("PASS " + message); }
    private static object Invoke(object target, string name, params object[] args)
    {
        MethodInfo found = null;
        foreach(MethodInfo method in target.GetType().GetMethods()) if(method.Name == name && method.GetParameters().Length == args.Length) { found=method; break; }
        if(found==null) throw new MissingMethodException(name);
        return found.Invoke(target,args);
    }
    private static void Subscribe(object target,string name,string handler)
    {
        EventInfo info=target.GetType().GetEvent(name);
        info.AddEventHandler(target,Delegate.CreateDelegate(info.EventHandlerType,typeof(NativeUiSelfTest).GetMethod(handler,BindingFlags.Static|BindingFlags.NonPublic)));
    }
    private static void Completed(object sender,object args) { if(Convert.ToBoolean(args.GetType().GetProperty("IsSuccess").GetValue(args)))Loaded.TrySetResult(true); }
    private static void Received(object sender,object args)
    {
        try { Require(System.Threading.Thread.CurrentThread.ManagedThreadId==UiThread,"web messages return on the game UI thread"); Require((string)Invoke(args,"TryGetWebMessageAsString")=="native-webkit-ok","JavaScript messages reach the SDK event"); LastMessageSource=(string)args.GetType().GetProperty("Source").GetValue(args);Message.TrySetResult(true); }
        catch(Exception error){Message.TrySetException(error);}
    }
    private static void Starting(object sender,object args)
    {
        string url=(string)args.GetType().GetProperty("Uri").GetValue(args);
        LastStarted=url;
        if(url.Contains("cancel.invalid")){args.GetType().GetProperty("Cancel").SetValue(args,true);Cancelled=true;}
    }
    internal static int Run()
    {
        string output=Environment.GetEnvironmentVariable("CMO_NATIVE_TEST_LOG");
        Log=text=>{Console.WriteLine(text);if(!string.IsNullOrEmpty(output))File.AppendAllText(output,text+Environment.NewLine);};
        try
        {
            if(Environment.GetEnvironmentVariable("CMO_SERIALIZE_BARRIERS")=="1")BarrierCompat.Install(Log);
            NativeHost.Install(Environment.GetEnvironmentVariable("CMO_NATIVE_HOST"),Log);
            string directory=Environment.GetEnvironmentVariable("CMO_WEBVIEW_SDK_DIR");
            NativeBrowser.Install(directory,Log);NativeGui.Install(true);
            Type browserType=Assembly.LoadFrom(Path.Combine(directory,"Microsoft.Web.WebView2.WinForms.dll")).GetType("Microsoft.Web.WebView2.WinForms.WebView2",true);
            UiThread=System.Threading.Thread.CurrentThread.ManagedThreadId;
            Browser=Activator.CreateInstance(browserType);
            var browser=(Control)Browser;
            // Exercise the actual game's early, detached-control startup path.
            Task detached=(Task)Invoke(Browser,"EnsureCoreWebView2Async",new object[]{null});
            Require(!detached.IsCompleted,"detached browser initialization waits for its parent");
            var form=new TimeForm {Text="Command native UI validation",ClientSize=new Size(900,650),StartPosition=FormStartPosition.CenterScreen};
            var button=new Button {Text="Apply orders",Location=new Point(20,20),Size=new Size(150,30)};
            var check=new CheckBox {Text="Show briefing",Checked=true,Location=new Point(190,20),Size=new Size(170,30)};
            var edit=new TextBox {Text="Task group Alpha",Location=new Point(20,65),Size=new Size(250,26)};
            var label=new Label {Text="Native AppKit controls, embedded in the Wine window",Location=new Point(20,105),Size=new Size(700,24)};
            var tree=new TreeView {Location=new Point(20,180),Size=new Size(200,440)};tree.Nodes.Add("Operations").Nodes.Add("Northern patrol");tree.ExpandAll();
            var theme=new ThemeRenderer();
            var menu=new MenuStrip {Dock=DockStyle.None,Location=new Point(530,15),Size=new Size(320,24),AutoSize=false,BackColor=Color.White,ForeColor=Color.Gainsboro,Renderer=theme};
            var menuItem=new ToolStripMenuItem("Map Settings + Tools");menuItem.DropDownItems.Add("Show reference points");menu.Items.Add(menuItem);
            var toolbar=new ToolStrip {Name="ToolStrip_ShortcutButtons",Dock=DockStyle.None,Location=new Point(530,55),Size=new Size(320,28),AutoSize=false,BackColor=Color.White,ForeColor=Color.Gainsboro,Renderer=theme};
            var order=new ToolStripButton("Start / Resume");toolbar.Items.Add(order);
            Image bannerImage=Image.FromFile(Path.Combine(directory,"Symbols","Menu","Btn_Magazines.png"));
            var banner=new ToolStripButton("Magazines",bannerImage){DisplayStyle=ToolStripItemDisplayStyle.Image,AutoSize=false,Size=new Size(114,24)};toolbar.Items.Add(banner);
            form.Disposed+=(s,e)=>bannerImage.Dispose();
            var timeBar=new ToolStrip{Name="ToolStrip1",Dock=DockStyle.None,Location=new Point(20,135),Size=new Size(850,32),AutoSize=false,BackColor=Color.White,ForeColor=Color.Gainsboro,Renderer=theme};
            var rate=new ToolStripDropDownButton("Time compression"){Name="TSB_TimeAccel",Available=false};
            var rateSlot=new ToolStripLabel("Time Compression:"){Name="CompressionCaption"};
            var normal=new ToolStripMenuItem("1x"){Name="TSMI_TimeAccel1x",Checked=true};
            var fast=new ToolStripMenuItem("5x"){Name="TSMI_TimeAccel5x"};rate.DropDownItems.AddRange(new ToolStripItem[]{normal,fast});
            int rateClicks=0,playClicks=0;bool running=false;
            normal.Click+=(s,e)=>{normal.Checked=true;fast.Checked=false;rateClicks++;};fast.Click+=(s,e)=>{normal.Checked=false;fast.Checked=true;rateClicks++;};
            var playback=new ToolStripButton("Start / Resume",form._image_StartResume){Name="TSB_StartGame"};
            playback.Click+=(s,e)=>{running=!running;playback.Image=running?form._image_StopPause:form._image_StartResume;playClicks++;};
            timeBar.Items.AddRange(new ToolStripItem[]{rateSlot,rate,new ToolStripLabel("Time Mode:"),new ToolStripButton("Time Mode: 1 sec"){Name="ButtonGameSpeed"},playback});
            browser.Location=new Point(240,180);browser.Size=new Size(640,440);browser.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;
            form.Controls.AddRange(new Control[]{button,check,edit,label,tree,browser,menu,toolbar,timeBar});
            bool clicked=false;button.Click+=(s,e)=>clicked=true;
            var watchdog=new Timer{Interval=45000};watchdog.Tick+=(s,e)=>{Log("FAIL native UI timeout");form.Close();};watchdog.Start();
            form.Shown+=async (s,e)=>{
                try
                {
                    Loaded=new TaskCompletionSource<bool>();Message=new TaskCompletionSource<bool>();
                    Subscribe(Browser,"NavigationCompleted","Completed");Subscribe(Browser,"NavigationStarting","Starting");Subscribe(Browser,"WebMessageReceived","Received");
                    Task first=(Task)Invoke(Browser,"EnsureCoreWebView2Async",new object[]{null});await first;
                    Require(object.ReferenceEquals(first,Invoke(Browser,"EnsureCoreWebView2Async",new object[]{null})),"initialization task is shared");
                    object core=browserType.GetProperty("CoreWebView2").GetValue(Browser);Require(core!=null,"SDK core is available without Edge");
                    var probe=await NativeHost.Request("probe",0,new Dictionary<string,object>{{"hwnd",form.Handle.ToInt64()}});
                    Require(Convert.ToBoolean(probe["found"]),"correct Wine AppKit window found");
                    await CheckToolbar(menuItem,theme.Surface,true);await CheckToolbar(order,theme.Surface,true);
                    var bannerState=await Inspect(banner);var bannerSize=(System.Collections.IList)bannerState["imageSize"];
                    Require(Convert.ToDouble(bannerSize[1])>toolbar.ImageScalingSize.Height && Math.Abs(Convert.ToDouble(bannerSize[0])/Convert.ToDouble(bannerSize[1])-(double)bannerImage.Width/bannerImage.Height)<0.001,"game banner fills the available toolbar height with its original proportions");
                    var rateState=await Inspect(rateSlot);var playState=await Inspect(playback);
                    Require((string)rateState["text"]=="1×" && (string)playState["text"]=="Play" && (string)playState["symbol"]=="play.fill","compact time controls show the selected rate and play action");
                    Require((string)rateState["class"]=="NSPopUpButton" && Convert.ToBoolean(rateState["hitVisible"]),"visible compression label becomes an interactive selector even when the source button is hidden");
                    Require(Convert.ToDouble(rateState["x"])<40 && Convert.ToDouble(playState["x"])<240,"static time labels are removed and the native controls reclaim their space");
                    await NativeHost.Request("selfTestControl",0,new Dictionary<string,object>{{"control",NativeGui.Id(rateSlot)},{"menuItem",NativeGui.Id(fast)}});
                    rateState=await Inspect(rateSlot);Require(rateClicks==1 && (string)rateState["text"]=="5×","native rate selection invokes the original handler and updates its caption");
                    normal.Checked=true;fast.Checked=false;rateState=await Inspect(rateSlot);
                    Require((string)rateState["text"]=="1×","external rate changes update the native caption");
                    using(var customHost=new ToolStripTextBox())
                    {
                        timeBar.Items.Add(customHost);
                        rateState=await Inspect(rateSlot);
                        Require((string)rateState["class"]=="NSTextField" && (string)rateState["text"]=="Time Compression:","custom hosts restore the original label presentation");
                        timeBar.Items.Remove(customHost);
                    }
                    rateState=await Inspect(rateSlot);playState=await Inspect(playback);
                    Require((string)rateState["class"]=="NSPopUpButton" && Convert.ToBoolean(rateState["hitVisible"]) && Convert.ToBoolean(playState["hitVisible"]),"restoring compact layout keeps its selector and playback above the background");
                    rate.Available=true;rateState=await Inspect(rateSlot);
                    Require(Convert.ToBoolean(rateState["hitVisible"]) && (string)rateState["text"]=="1×","source-button visibility changes preserve the replacement selector");
                    await NativeHost.Request("selfTestControl",0,new Dictionary<string,object>{{"control",NativeGui.Id(playback)},{"click",true}});
                    playState=await Inspect(playback);Require(playClicks==1 && running && (string)playState["text"]=="Pause" && (string)playState["symbol"]=="pause.fill","native play dispatches the game action and follows its pause image state");
                    playback.Enabled=false;
                    await Inspect(playback);await NativeHost.Request("selfTestControl",0,new Dictionary<string,object>{{"control",NativeGui.Id(playback)},{"click",true}});await Task.Delay(150);
                    Require(playClicks==1,"disabled playback cannot dispatch a native action");
                    playback.Enabled=true;running=false;playback.Image=form._image_StartResume;playState=await Inspect(playback);
                    Require((string)playState["text"]=="Play","external pause changes restore the play action");
                    theme.Surface=Color.FromArgb(240,240,240);menu.ForeColor=toolbar.ForeColor=timeBar.ForeColor=Color.FromArgb(28,30,32);
                    menu.Invalidate();toolbar.Invalidate();
                    await CheckToolbar(menuItem,theme.Surface,false);await CheckToolbar(order,theme.Surface,false);await CheckToolbar(rateSlot,theme.Surface,false);
                    theme.Surface=Color.FromArgb(60,63,65);menu.ForeColor=toolbar.ForeColor=timeBar.ForeColor=Color.Gainsboro;
                    menu.Invalidate();toolbar.Invalidate();
                    await CheckToolbar(menuItem,theme.Surface,true);await CheckToolbar(order,theme.Surface,true);await CheckToolbar(rateSlot,theme.Surface,true);
                    string asset=Path.Combine(Path.GetTempPath(),"cmo-native-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(asset);
                    File.WriteAllText(Path.Combine(asset,"style.css"),"body { background: rgb(242, 245, 248); color: #162635; font: 16px -apple-system; padding: 24px; } h1 {font-size:28px;} #status {color:#17694a;}");
                    Type access=core.GetType().GetMethod("SetVirtualHostNameToFolderMapping").GetParameters()[2].ParameterType;
                    Invoke(core,"SetVirtualHostNameToFolderMapping","fixture.invalid",asset,Enum.ToObject(access,1));
                    string relativeFolder="cmo-mapping-test-"+Guid.NewGuid().ToString("N");
                    string relativeAsset=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,relativeFolder);Directory.CreateDirectory(relativeAsset);
                    try
                    {
                        File.WriteAllText(Path.Combine(relativeAsset,"root.css"),"h1 {border-left:7px solid #17694a;}");
                        File.WriteAllText(Path.Combine(relativeAsset,"relative.css"),"h1 {padding-left:11px;}");
                        File.WriteAllText(Path.Combine(relativeAsset,"writable.css"),"h1 {margin-top:13px;}");
                        string previousDirectory=Environment.CurrentDirectory;
                        try
                        {
                            // Captured RenderCustomHTML calls map Root to an empty folder.
                            // A different CWD also detects incorrect relative resolution.
                            Environment.CurrentDirectory=asset;
                            Invoke(core,"SetVirtualHostNameToFolderMapping","dbimages.invalid",asset,Enum.ToObject(access,1));
                            Invoke(core,"SetVirtualHostNameToFolderMapping","Root","",Enum.ToObject(access,1));
                            Invoke(core,"SetVirtualHostNameToFolderMapping","TopWritable",AppDomain.CurrentDomain.BaseDirectory,Enum.ToObject(access,1));
                            Invoke(core,"SetVirtualHostNameToFolderMapping","relative.invalid",relativeFolder,Enum.ToObject(access,1));
                        }
                        finally {Environment.CurrentDirectory=previousDirectory;}
                        Invoke(core,"NavigateToString","<!doctype html><link rel='stylesheet' href='https://fixture.invalid/style.css'><link rel='stylesheet' href='https://Root/"+relativeFolder+"/root.css'><link rel='stylesheet' href='https://TopWritable/"+relativeFolder+"/writable.css'><link rel='stylesheet' href='https://relative.invalid/relative.css'><h1>Operation Northern Patrol</h1><p>WebKit renders this briefing inside the existing Wine window.</p><p id='status'>Native rendering is active.</p><input id='orders' aria-label='Orders' value='Hold position'><button onclick=\"document.getElementById('status').textContent='Orders updated'\">Update orders</button><script>window.chrome.webview.postMessage('native-webkit-ok')</script>");
                        await Loaded.Task;await Message.Task;
                        string mapped=await (Task<string>)Invoke(Browser,"ExecuteScriptAsync","JSON.stringify({border:getComputedStyle(document.querySelector('h1')).borderLeftWidth,padding:getComputedStyle(document.querySelector('h1')).paddingLeft,margin:getComputedStyle(document.querySelector('h1')).marginTop})");
                        Require(mapped.Contains("7px") && mapped.Contains("11px") && mapped.Contains("13px"),"empty Root and relative folder mappings load preview styles independently of the working directory");
                    }
                    finally {Directory.Delete(relativeAsset,true);}
                    string result=await (Task<string>)Invoke(Browser,"ExecuteScriptAsync","JSON.stringify({title:document.querySelector('h1').textContent,color:getComputedStyle(document.body).backgroundColor})");
                    Require(result.Contains("Northern Patrol") && result.Contains("242, 245, 248"),"HTML, mapped CSS, and JavaScript results work");
                    await Task.Delay(300);
                    var nativeButton=await NativeHost.Request("selfTestControl",0,new Dictionary<string,object>{{"control",NativeGui.Id(button)},{"click",true}});
                    await Task.Delay(150);Require(clicked,"native NSButton invokes the existing WinForms click handler");Require(Convert.ToBoolean(nativeButton["embedded"]),"AppKit controls are children of the Wine content view");
                    long paints=NativeGui.SuppressedPaints;button.Invalidate();button.Update();
                    Require(NativeGui.SuppressedPaints>paints,"native peers suppress redundant Windows painting");
                    Invoke(core,"Navigate","https://cancel.invalid/");await Task.Delay(300);Require(Cancelled,"navigation cancellation reaches WebKit");
                    result=await (Task<string>)Invoke(Browser,"ExecuteScriptAsync","document.querySelector('h1').textContent");Require(result.Contains("Northern Patrol"),"cancelled navigation preserves the current page");
                    string localPage=Path.Combine(asset,"scenario %20 #1.html");
                    using(var picture=new Bitmap(4,2))picture.Save(Path.Combine(asset,"status %20.png"),System.Drawing.Imaging.ImageFormat.Png);
                    File.WriteAllText(localPage,"<!doctype html><link rel='stylesheet' href='style.css'><h1>Local scenario preview</h1><img id='status' src='status%20%2520.png'><script>window.chrome.webview.postMessage('native-webkit-ok')</script>");
                    Loaded=new TaskCompletionSource<bool>();Message=new TaskCompletionSource<bool>();
                    Invoke(core,"Navigate",localPage);await Loaded.Task;await Message.Task;
                    result=await (Task<string>)Invoke(Browser,"ExecuteScriptAsync","JSON.stringify({title:document.querySelector('h1').textContent,color:getComputedStyle(document.body).backgroundColor,image:document.querySelector('img').naturalWidth})");
                    Require(result.Contains("Local scenario preview") && result.Contains("242, 245, 248") && result.Contains("image\\\":4"),"raw Windows paths load HTML, relative CSS, and images with literal percent characters");
                    string expected=new Uri(localPage).AbsoluteUri;
                    Require(LastStarted==expected && LastMessageSource==expected && (string)core.GetType().GetProperty("Source").GetValue(core)==expected,"file navigation and message events preserve the public Windows file URL");
                    string unixAsset=Environment.GetEnvironmentVariable("CMO_WINE_PREFIX")+"/drive_c/"+localPage.Substring(3).Replace('\\','/');
                    var zUri=new Uri(NativeHost.WindowsPath(unixAsset));
                    Loaded=new TaskCompletionSource<bool>();Message=new TaskCompletionSource<bool>();
                    browserType.GetProperty("Source").SetValue(Browser,zUri);await Loaded.Task;await Message.Task;
                    Require(LastStarted==zUri.AbsoluteUri && LastMessageSource==zUri.AbsoluteUri,"file URLs on Wine's Z drive work through the Source property");
                    string realPage=Environment.GetEnvironmentVariable("CMO_NATIVE_TEST_PAGE");
                    if(!string.IsNullOrEmpty(realPage)){
                        Loaded=new TaskCompletionSource<bool>();Invoke(core,"Navigate",realPage);await Loaded.Task;
                        result=await (Task<string>)Invoke(Browser,"ExecuteScriptAsync","JSON.stringify({text:document.body.innerText.length,color:getComputedStyle(document.body).backgroundColor})");
                        Log("Scenario page rendered: "+result);
                        string count=await (Task<string>)Invoke(Browser,"ExecuteScriptAsync","document.body.innerText.length");int characters;
                        Require(int.TryParse(count,out characters) && characters>0,"installed scenario description has rendered text");
                    }
                    form.ClientSize=new Size(960,700);await Task.Delay(250);
                    string snapshot=Environment.GetEnvironmentVariable("CMO_NATIVE_TEST_SNAPSHOT");
                    if(!string.IsNullOrEmpty(snapshot))await NativeHost.Request("selfTestSnapshot",0,new Dictionary<string,object>{{"hwnd",form.Handle.ToInt64()},{"path",snapshot}});
                    int hold; if(int.TryParse(Environment.GetEnvironmentVariable("CMO_NATIVE_TEST_HOLD_MS"),out hold)) await Task.Delay(Math.Min(hold,30000));
                    browser.Dispose();await Task.Delay(200);probe=await NativeHost.Request("probe",0,new Dictionary<string,object>{{"hwnd",form.Handle.ToInt64()}});
                    Require(Convert.ToInt32(probe["browsers"])==0,"disposing a browser removes its native view");
                    Directory.Delete(asset,true);Result=0;Log("PASS native UI integration");
                }
                catch(Exception error){Log("FAIL "+error);}
                finally {watchdog.Stop();form.Close();}
            };
            if(Environment.GetEnvironmentVariable("CMO_NATIVE_TEST_MODAL")=="1")form.ShowDialog();else Application.Run(form);
            watchdog.Dispose();form.Dispose();
        }
        catch(Exception error){Log("FAIL "+error);}
        return Result;
    }
}
