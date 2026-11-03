// Included in NativeHost.m. All objects below are confined to AppKit's thread.
static NSMutableDictionary<NSNumber *, id> *browsers;
static NSMutableDictionary<NSNumber *, NSMutableDictionary<NSNumber *, id> *> *forms;
static NSColor *modelColor(NSArray *rgb) {
    return rgb.count==3 ? [NSColor colorWithSRGBRed:[rgb[0] doubleValue]/255.0 green:[rgb[1] doubleValue]/255.0 blue:[rgb[2] doubleValue]/255.0 alpha:1] : nil;
}
static NSRect nativeRect(NSWindow *window, NSDictionary *g, NSString *prefix) {
    double width = [g[@"fw"] doubleValue], height = [g[@"fh"] doubleValue];
    if (width <= 0 || height <= 0) return NSZeroRect;
    NSRect bounds = window.contentView.bounds;
    double sx = bounds.size.width / width, sy = bounds.size.height / height;
    NSString *x = [prefix stringByAppendingString:@"x"], *y = [prefix stringByAppendingString:@"y"];
    NSString *w = [prefix stringByAppendingString:@"w"], *h = [prefix stringByAppendingString:@"h"];
    double top = ([g[y] doubleValue] - [g[@"fy"] doubleValue]) * sy;
    double rh = [g[h] doubleValue] * sy;
    return NSMakeRect(bounds.origin.x + ([g[x] doubleValue] - [g[@"fx"] doubleValue]) * sx,
        bounds.origin.y + (window.contentView.isFlipped ? top : bounds.size.height - top - rh),
        [g[w] doubleValue] * sx, rh);
}
@interface CMOClip : NSView
@end
@implementation CMOClip
- (BOOL)isFlipped { return YES; }
@end
static BOOL position(NSView *view, CMOClip *clip, NSDictionary *geometry) {
    NSWindow *window = wineWindow([geometry[@"hwnd"] unsignedLongLongValue]);
    if (!window) { clip.hidden = YES; return NO; }
    NSRect full = nativeRect(window, geometry, @"");
    NSRect visible = NSIntersectionRect(full, nativeRect(window, geometry, @"c"));
    clip.wantsLayer = YES; clip.layer.masksToBounds = YES;
    NSArray *rgb=geometry[@"background"];
    if(rgb.count==3) {
        double r=[rgb[0] doubleValue]/255.0,g=[rgb[1] doubleValue]/255.0,b=[rgb[2] doubleValue]/255.0;
        clip.layer.backgroundColor=[NSColor colorWithSRGBRed:r green:g blue:b alpha:1].CGColor;
        clip.appearance=[NSAppearance appearanceNamed:(r*0.2126+g*0.7152+b*0.0722)<0.45?NSAppearanceNameDarkAqua:NSAppearanceNameAqua];
    }
    clip.frame = visible;
    // The clip is flipped, its parent may not be. Convert the full origin.
    double top = window.contentView.isFlipped ? full.origin.y-visible.origin.y : NSMaxY(visible)-NSMaxY(full);
    view.frame = NSMakeRect(full.origin.x-visible.origin.x, top, full.size.width, full.size.height);
    if (clip.superview != window.contentView) [window.contentView addSubview:clip positioned:NSWindowAbove relativeTo:nil];
    clip.hidden = ![geometry[@"visible"] boolValue] || visible.size.width <= 0 || visible.size.height <= 0;
    return YES;
}
static NSString *mappedPath(NSString *windows, NSString *prefix) {
    if (windows.length < 3 || [windows characterAtIndex:1] != ':') return nil;
    NSString *drive = [[windows substringToIndex:1] lowercaseString];
    NSString *rest = [[windows substringFromIndex:2] stringByReplacingOccurrencesOfString:@"\\" withString:@"/"];
    while ([rest hasPrefix:@"/"]) rest = [rest substringFromIndex:1];
    NSString *root = [drive isEqual:@"z"] ? @"/" : [prefix stringByAppendingPathComponent:[@"dosdevices/" stringByAppendingFormat:@"%@:", drive]];
    if (root.length == 0) return nil;
    return [[[root stringByAppendingPathComponent:rest] stringByStandardizingPath] stringByResolvingSymlinksInPath];
}
static NSString *json(id value) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:value ?: NSNull.null options:NSJSONWritingFragmentsAllowed error:nil];
    return data ? [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] : @"null";
}
@interface CMOWebView : WKWebView
@property BOOL contextMenus;
@end
@implementation CMOWebView
- (NSMenu *)menuForEvent:(NSEvent *)event { return self.contextMenus ? [super menuForEvent:event] : nil; }
@end
@interface CMOBrowser : NSObject <WKNavigationDelegate, WKUIDelegate, WKScriptMessageHandler, WKURLSchemeHandler>
@property NSNumber *identifier;
@property CMOWebView *web;
@property CMOClip *clip;
@property NSString *prefix;
@property NSMutableDictionary<NSString *, NSString *> *roots;
@property NSMutableDictionary<NSString *, NSString *> *fileOrigins;
@property NSMutableDictionary<NSString *, NSString *> *fileHosts;
@property NSMutableDictionary<NSNumber *, id> *decisions;
@property uint64_t navigation;
@property BOOL disposed;
@property BOOL scriptsEnabled;
- (NSString *)rewrite:(NSString *)text;
- (NSURL *)navigationURL:(NSDictionary *)request error:(NSString **)error;
- (NSString *)publicURL:(NSURL *)url;
- (void)dispose;
@end
@implementation CMOBrowser
- (instancetype)init {
    if ((self = [super init])) { _roots = [NSMutableDictionary new]; _fileOrigins = [NSMutableDictionary new]; _fileHosts = [NSMutableDictionary new]; _decisions = [NSMutableDictionary new]; _scriptsEnabled = YES; }
    return self;
}
- (void)create:(NSDictionary *)request {
    self.identifier=request[@"browser"]; self.prefix=request[@"winePrefix"];
    WKWebViewConfiguration *config=[WKWebViewConfiguration new];
    config.websiteDataStore=WKWebsiteDataStore.nonPersistentDataStore;
    [config setURLSchemeHandler:self forURLScheme:@"cmo-resource"];
    NSString *bridge=@"(()=>{const c=window.chrome||(window.chrome={});c.webview={postMessage:v=>window.webkit.messageHandlers.cmo.postMessage(JSON.stringify(v))};})()";
    [config.userContentController addScriptMessageHandler:self name:@"cmo"];
    [config.userContentController addUserScript:[[WKUserScript alloc] initWithSource:bridge injectionTime:WKUserScriptInjectionTimeAtDocumentStart forMainFrameOnly:YES]];
    self.web=[[CMOWebView alloc] initWithFrame:NSZeroRect configuration:config];
    self.web.contextMenus=YES; self.web.navigationDelegate=self; self.web.UIDelegate=self;
    self.clip=[CMOClip new]; [self.clip addSubview:self.web];
    if (!position(self.web,self.clip,request)) { [self dispose]; fail(request,@"The game's AppKit window is not available."); return; }
    reply(request,@{@"embedded":@YES});
}
- (NSString *)rewrite:(NSString *)text {
    for(NSString *host in self.roots) {
        NSString *target=[@"cmo-resource://" stringByAppendingString:host];
        for(NSString *scheme in @[@"https://",@"http://"]) {
            NSString *source=[scheme stringByAppendingString:host];
            // Require a URL boundary after the mapped host.
            NSString *pattern=[NSString stringWithFormat:@"%@(?![A-Za-z0-9.:-])",[NSRegularExpression escapedPatternForString:source]];
            NSRegularExpression *regex=[NSRegularExpression regularExpressionWithPattern:pattern options:NSRegularExpressionCaseInsensitive error:nil];
            text=[regex stringByReplacingMatchesInString:text options:0 range:NSMakeRange(0,text.length) withTemplate:target];
        }
    }
    return text;
}
- (NSURL *)navigationURL:(NSDictionary *)request error:(NSString **)error {
    NSURL *url=[NSURL URLWithString:request[@"url"]];
    if(!url){*error=@"Invalid browser URL.";return nil;}
    if(!url.isFileURL)return [NSURL URLWithString:[self rewrite:request[@"url"]]];
    NSString *path=mappedPath(request[@"filePath"],self.prefix);
    NSString *root=mappedPath(request[@"fileDirectory"],self.prefix);
    NSURL *origin=[NSURL URLWithString:request[@"fileOrigin"] ?: @""];
    BOOL directory=NO;
    if(!path || !root || !origin.isFileURL || [root isEqual:@"/"] || ![path hasPrefix:[root stringByAppendingString:@"/"]] || ![[NSFileManager defaultManager] fileExistsAtPath:path isDirectory:&directory] || directory){
        *error=@"The local browser page could not be resolved inside its containing folder.";return nil;
    }
    // Give each explicit local folder a scoped resource host. This also lets
    // the existing resource handler rewrite virtual-host URLs inside its HTML
    // and CSS, while relative links and images keep their directory context.
    NSString *key=[root stringByAppendingFormat:@"\n%@",origin.absoluteString];
    NSString *host=self.fileHosts[key];
    if(!host){
        host=[@"local-" stringByAppendingString:NSUUID.UUID.UUIDString.lowercaseString];
        self.fileHosts[key]=host;self.roots[host]=root;self.fileOrigins[host]=origin.absoluteString;
    }
    NSURLComponents *target=[NSURLComponents componentsWithString:[@"cmo-resource://" stringByAppendingString:host]];
    NSURLComponents *source=[NSURLComponents componentsWithURL:url resolvingAgainstBaseURL:NO];
    target.path=[@"/" stringByAppendingString:source.path.lastPathComponent];
    target.percentEncodedQuery=source.percentEncodedQuery;target.percentEncodedFragment=source.percentEncodedFragment;
    return target.URL;
}
- (NSString *)publicURL:(NSURL *)url {
    if(!url)return @"about:blank";
    NSString *origin=self.fileOrigins[url.host.lowercaseString];
    if(![url.scheme isEqual:@"cmo-resource"] || !origin)return url.absoluteString ?: @"about:blank";
    NSURLComponents *source=[NSURLComponents componentsWithURL:url resolvingAgainstBaseURL:NO];
    NSURLComponents *result=[NSURLComponents componentsWithString:origin];
    result.path=[result.path stringByAppendingString:[source.path hasPrefix:@"/"]?[source.path substringFromIndex:1]:source.path];
    result.percentEncodedQuery=source.percentEncodedQuery;result.percentEncodedFragment=source.percentEncodedFragment;
    return result.URL.absoluteString;
}
- (void)dispose {
    if(self.disposed)return; self.disposed=YES;
    for(id block in self.decisions.allValues) ((void (^)(WKNavigationActionPolicy))block)(WKNavigationActionPolicyCancel);
    [self.decisions removeAllObjects];
    [self.web stopLoading]; self.web.navigationDelegate=nil; self.web.UIDelegate=nil;
    [self.web.configuration.userContentController removeScriptMessageHandlerForName:@"cmo"];
    [self.clip removeFromSuperview]; self.web=nil; self.clip=nil;
}
- (void)webView:(WKWebView *)web decidePolicyForNavigationAction:(WKNavigationAction *)action decisionHandler:(void (^)(WKNavigationActionPolicy))handler {
    if(self.disposed){handler(WKNavigationActionPolicyCancel);return;}
    if(action.targetFrame && !action.targetFrame.mainFrame){handler(WKNavigationActionPolicyAllow);return;}
    NSURL *url=action.request.URL;
    if(![@[@"about",@"http",@"https",@"cmo-resource"] containsObject:url.scheme.lowercaseString]){
        emit(@{@"event":@"error",@"browser":self.identifier,@"error":[NSString stringWithFormat:@"Unsupported browser navigation scheme: %@",url.scheme ?: @"(missing)"]});
        handler(WKNavigationActionPolicyCancel);return;
    }
    NSNumber *navigation=@(++self.navigation); self.decisions[navigation]=[handler copy];
    emit(@{@"event":@"navigationStarting",@"browser":self.identifier,@"url":[self publicURL:url],@"navigation":navigation,@"user":@(action.navigationType==WKNavigationTypeLinkActivated)});
    __weak CMOBrowser *weak=self;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW,5*NSEC_PER_SEC),dispatch_get_main_queue(),^{
        CMOBrowser *strong=weak; id pending=strong.decisions[navigation];
        if(pending){[strong.decisions removeObjectForKey:navigation];((void (^)(WKNavigationActionPolicy))pending)(WKNavigationActionPolicyCancel);}
    });
}
- (void)webView:(WKWebView *)web decidePolicyForNavigationAction:(WKNavigationAction *)action preferences:(WKWebpagePreferences *)preferences decisionHandler:(void (^)(WKNavigationActionPolicy, WKWebpagePreferences *))handler {
    preferences.allowsContentJavaScript=self.scriptsEnabled;
    [self webView:web decidePolicyForNavigationAction:action decisionHandler:^(WKNavigationActionPolicy policy){handler(policy,preferences);}];
}
- (void)complete:(BOOL)success {
    emit(@{@"event":@"navigationCompleted",@"browser":self.identifier,@"url":[self publicURL:self.web.URL],@"navigation":@(self.navigation),@"success":@(success)});
}
- (void)webView:(WKWebView *)web didFinishNavigation:(WKNavigation *)navigation { [self complete:YES]; }
- (void)webView:(WKWebView *)web didFailNavigation:(WKNavigation *)navigation withError:(NSError *)error { if(error.code!=NSURLErrorCancelled)[self complete:NO]; }
- (void)webView:(WKWebView *)web didFailProvisionalNavigation:(WKNavigation *)navigation withError:(NSError *)error { if(error.code!=NSURLErrorCancelled)[self complete:NO]; }
- (void)webViewWebContentProcessDidTerminate:(WKWebView *)web { emit(@{@"event":@"error",@"browser":self.identifier,@"error":@"WebKit stopped. Reopen this browser pane to reload it."}); }
- (void)userContentController:(WKUserContentController *)controller didReceiveScriptMessage:(WKScriptMessage *)message {
    if(!message.frameInfo.mainFrame || ![message.body isKindOfClass:NSString.class])return;
    NSString *scheme=message.frameInfo.request.URL.scheme;
    if(![scheme isEqual:@"about"] && ![scheme isEqual:@"cmo-resource"])return;
    NSData *data=[message.body dataUsingEncoding:NSUTF8StringEncoding];
    if(data.length>maxFrame/2)return;
    id value=[NSJSONSerialization JSONObjectWithData:data options:NSJSONReadingFragmentsAllowed error:nil];
    NSMutableDictionary *event=[@{@"event":@"message",@"browser":self.identifier,@"json":message.body,@"url":[self publicURL:message.frameInfo.request.URL]} mutableCopy];
    if([value isKindOfClass:NSString.class])event[@"string"]=value;
    emit(event);
}
- (void)webView:(WKWebView *)web startURLSchemeTask:(id<WKURLSchemeTask>)task {
    NSURL *url=task.request.URL; NSString *root=self.roots[url.host.lowercaseString];
    // Decode exactly once, so a literal "%20" in a filename is not read as a space.
    NSString *relative=[NSURLComponents componentsWithURL:url resolvingAgainstBaseURL:NO].percentEncodedPath.stringByRemovingPercentEncoding;
    while([relative hasPrefix:@"/"])relative=[relative substringFromIndex:1];
    NSString *path=[[root stringByAppendingPathComponent:relative] stringByStandardizingPath].stringByResolvingSymlinksInPath;
    if(!root || !path || ![path hasPrefix:[root stringByAppendingString:@"/"]]) {
        [task didFailWithError:[NSError errorWithDomain:NSURLErrorDomain code:NSURLErrorNoPermissionsToReadFile userInfo:nil]];return;
    }
    NSError *error=nil;
    NSNumber *size=nil; [[NSURL fileURLWithPath:path] getResourceValue:&size forKey:NSURLFileSizeKey error:&error];
    if(error || size.unsignedLongLongValue>32*1024*1024){[task didFailWithError:error ?: [NSError errorWithDomain:NSURLErrorDomain code:NSURLErrorDataLengthExceedsMaximum userInfo:nil]];return;}
    NSData *data=[NSData dataWithContentsOfFile:path options:NSDataReadingMappedIfSafe error:&error];
    if(!data){[task didFailWithError:error];return;}
    NSString *mime=[UTType typeWithFilenameExtension:path.pathExtension].preferredMIMEType ?: @"application/octet-stream";
    if([mime isEqual:@"text/css"] || [mime isEqual:@"text/html"] || [mime containsString:@"javascript"]) {
        NSString *text=[[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
        if(text)data=[[self rewrite:text] dataUsingEncoding:NSUTF8StringEncoding];
    }
    [task didReceiveResponse:[[NSURLResponse alloc] initWithURL:url MIMEType:mime expectedContentLength:data.length textEncodingName:[mime hasPrefix:@"text/"] ? @"utf-8" : nil]];
    [task didReceiveData:data];[task didFinish];
}
- (void)webView:(WKWebView *)web stopURLSchemeTask:(id<WKURLSchemeTask>)task { }
- (WKWebView *)webView:(WKWebView *)web createWebViewWithConfiguration:(WKWebViewConfiguration *)config forNavigationAction:(WKNavigationAction *)action windowFeatures:(WKWindowFeatures *)features { [web loadRequest:action.request];return nil; }
- (void)webView:(WKWebView *)web runJavaScriptAlertPanelWithMessage:(NSString *)message initiatedByFrame:(WKFrameInfo *)frame completionHandler:(void (^)(void))completion {
    NSAlert *alert=[NSAlert new];alert.messageText=message;[alert addButtonWithTitle:@"OK"];
    [alert beginSheetModalForWindow:web.window completionHandler:^(NSModalResponse result){completion();}];
}
- (void)webView:(WKWebView *)web runJavaScriptConfirmPanelWithMessage:(NSString *)message initiatedByFrame:(WKFrameInfo *)frame completionHandler:(void (^)(BOOL))completion {
    NSAlert *alert=[NSAlert new];alert.messageText=message;[alert addButtonWithTitle:@"OK"];[alert addButtonWithTitle:@"Cancel"];
    [alert beginSheetModalForWindow:web.window completionHandler:^(NSModalResponse result){completion(result==NSAlertFirstButtonReturn);}];
}
- (void)webView:(WKWebView *)web runJavaScriptTextInputPanelWithPrompt:(NSString *)prompt defaultText:(NSString *)text initiatedByFrame:(WKFrameInfo *)frame completionHandler:(void (^)(NSString *))completion {
    NSAlert *alert=[NSAlert new];alert.messageText=prompt;[alert addButtonWithTitle:@"OK"];[alert addButtonWithTitle:@"Cancel"];
    NSTextField *field=[[NSTextField alloc]initWithFrame:NSMakeRect(0,0,300,24)];field.stringValue=text ?: @"";alert.accessoryView=field;
    [alert beginSheetModalForWindow:web.window completionHandler:^(NSModalResponse result){completion(result==NSAlertFirstButtonReturn?field.stringValue:nil);}];
}
@end

@interface CMOPeer : NSObject <NSTextFieldDelegate, NSTextViewDelegate, NSTableViewDataSource, NSTableViewDelegate, NSOutlineViewDataSource, NSOutlineViewDelegate>
@property NSNumber *identifier;
@property NSString *kind;
@property NSView *view;
@property CMOClip *clip;
@property NSDictionary *model;
@property BOOL updating;
@property NSArray *rows;
@property NSImage *image;
- (void)update:(NSDictionary *)model;
@end
@implementation CMOPeer
- (void)action:(id)sender {
    if(self.updating)return;
    id value=@0; NSNumber *control=self.identifier;
    if([sender isKindOfClass:NSMenuItem.class])control=@([sender tag]);
    else if([self.kind isEqual:@"check"] || [self.kind isEqual:@"radio"])value=@([(NSButton *)sender state]==NSControlStateValueMixed?2:[(NSButton *)sender state]);
    else if([self.kind isEqual:@"combo"])value=@([(NSPopUpButton *)sender indexOfSelectedItem]);
    else if([self.kind isEqual:@"tabs"])value=@([(NSSegmentedControl *)sender selectedSegment]);
    else if([self.kind isEqual:@"slider"])value=@([(NSSlider *)sender integerValue]);
    else if([self.kind isEqual:@"number"] || [self.kind isEqual:@"text"])value=[sender stringValue];
    emit(@{@"event":@"action",@"control":control,@"value":value});
}
- (void)controlTextDidChange:(NSNotification *)notification { [self action:notification.object]; }
- (void)textDidChange:(NSNotification *)notification {
    if(!self.updating)emit(@{@"event":@"action",@"control":self.identifier,@"value":[notification.object string]});
}
- (NSMenu *)menu:(NSArray *)items {
    NSMenu *menu=[NSMenu new];menu.autoenablesItems=NO;
    for(NSDictionary *item in items){
        if([item[@"separator"] boolValue]){[menu addItem:NSMenuItem.separatorItem];continue;}
        NSMenuItem *entry=[[NSMenuItem alloc]initWithTitle:item[@"text"] ?: @"" action:@selector(action:) keyEquivalent:@""];
        entry.tag=[item[@"id"] integerValue];entry.target=self;entry.enabled=[item[@"enabled"] boolValue];entry.state=[item[@"checked"] boolValue]?NSControlStateValueOn:NSControlStateValueOff;
        if([item[@"children"] count])entry.submenu=[self menu:item[@"children"]];
        [menu addItem:entry];
    }
    return menu;
}
- (void)create:(NSDictionary *)model {
    self.identifier=model[@"id"];self.kind=model[@"kind"];self.clip=[CMOClip new];NSString *kind=self.kind;
    if([@[@"button",@"check",@"radio"] containsObject:kind]){
        NSButton *button=[NSButton buttonWithTitle:@"" target:self action:@selector(action:)];
        if([kind isEqual:@"check"]){button.buttonType=NSButtonTypeSwitch;button.allowsMixedState=YES;}
        if([kind isEqual:@"radio"])button.buttonType=NSButtonTypeRadio;
        self.view=button;
    } else if([kind isEqual:@"surface"])self.view=[NSView new];
    else if([kind isEqual:@"label"])self.view=[NSTextField wrappingLabelWithString:@""];
    else if([kind isEqual:@"text"] && [model[@"multiline"] boolValue]){
        NSScrollView *scroll=[NSScrollView new];scroll.hasVerticalScroller=YES;
        NSTextView *text=[[NSTextView alloc]initWithFrame:NSZeroRect];text.delegate=self;text.richText=NO;text.autoresizingMask=NSViewWidthSizable;text.verticallyResizable=YES;
        scroll.documentView=text;self.view=scroll;
    } else if([kind isEqual:@"text"] || [kind isEqual:@"number"]){
        NSTextField *field=[model[@"password"] boolValue]?[NSSecureTextField new]:[NSTextField new];field.delegate=self;field.target=self;field.action=@selector(action:);self.view=field;
    } else if([kind isEqual:@"combo"] || [kind isEqual:@"menu"]){
        NSPopUpButton *button=[[NSPopUpButton alloc]initWithFrame:NSZeroRect pullsDown:[kind isEqual:@"menu"]];button.target=self;button.action=@selector(action:);self.view=button;
    } else if([kind isEqual:@"slider"]){NSSlider *slider=[NSSlider new];slider.target=self;slider.action=@selector(action:);slider.continuous=YES;self.view=slider;}
    else if([kind isEqual:@"progress"])self.view=[NSProgressIndicator new];
    else if([kind isEqual:@"tabs"]){NSSegmentedControl *tabs=[NSSegmentedControl new];tabs.target=self;tabs.action=@selector(action:);self.view=tabs;}
    else if([kind isEqual:@"list"] || [kind isEqual:@"tree"]){
        NSScrollView *scroll=[NSScrollView new];scroll.hasVerticalScroller=YES;scroll.hasHorizontalScroller=YES;
        NSTableView *table=[kind isEqual:@"tree"]?[NSOutlineView new]:[NSTableView new];
        NSTableColumn *column=[[NSTableColumn alloc]initWithIdentifier:@"text"];column.width=300;[table addTableColumn:column];table.headerView=nil;table.dataSource=self;table.delegate=self;
        if([table isKindOfClass:NSOutlineView.class])[(NSOutlineView *)table setOutlineTableColumn:column];
        scroll.documentView=table;self.view=scroll;
    }
    if(self.view){[self.clip addSubview:self.view];self.view.accessibilityIdentifier=[@"cmo-control-" stringByAppendingString:self.identifier.stringValue];}
}
- (void)update:(NSDictionary *)model {
    if(self.view && ![self.kind isEqual:model[@"kind"]]){
        [self.clip removeFromSuperview];self.view=nil;self.clip=nil;self.image=nil;self.model=nil;
    }
    if(!self.view)[self create:model];if(!self.view)return;
    self.updating=YES;NSDictionary *previous=self.model;self.model=model;
    position(self.view,self.clip,model);
    NSString *text=model[@"text"] ?: @"", *kind=self.kind;
    self.view.accessibilityLabel=model[@"accessibility"] ?: text;self.view.toolTip=model[@"tooltip"];
    if([self.view isKindOfClass:NSControl.class]){
        NSControl *control=(NSControl *)self.view;control.enabled=[model[@"enabled"] boolValue];
        CGFloat scale=[model[@"h"] doubleValue]>0?self.view.frame.size.height/[model[@"h"] doubleValue]:1;
        CGFloat size=[model[@"font"] doubleValue]*scale;if(size>0)control.font=[model[@"bold"] boolValue]?[NSFont boldSystemFontOfSize:size]:[NSFont systemFontOfSize:size];
        if([model[@"toolbar"] boolValue])control.controlSize=NSControlSizeSmall;
    }
    NSColor *foreground=[model[@"systemForeground"] boolValue]?nil:modelColor(model[@"foreground"]);
    if([self.view isKindOfClass:NSButton.class]){
        NSButton *button=(NSButton *)self.view;
        button.contentTintColor=foreground;
        if([model[@"toolbar"] boolValue]){
            // Toolstrip rectangles have no room for a dialog button's bezel,
            // shadow and large popup arrow. Keep the native control and menu
            // tracking, but use its compact, borderless presentation.
            button.bezelStyle=NSBezelStyleShadowlessSquare;
            button.bordered=NO;button.imageScaling=NSImageScaleProportionallyDown;
            button.alignment=[model[@"menubar"] boolValue]?NSTextAlignmentLeft:NSTextAlignmentCenter;
            button.cell.lineBreakMode=NSLineBreakByClipping;
            if([button isKindOfClass:NSPopUpButton.class]){
                NSPopUpButtonCell *cell=(NSPopUpButtonCell *)button.cell;
                cell.arrowPosition=[model[@"menubar"] boolValue]?NSPopUpNoArrow:NSPopUpArrowAtBottom;
                cell.altersStateOfSelectedItem=NO;
            } else button.buttonType=[model[@"checkable"] boolValue]?NSButtonTypePushOnPushOff:NSButtonTypeMomentaryChange;
        }
        if((model[@"image"]!=previous[@"image"] && ![model[@"image"] isEqual:previous[@"image"]]) || (model[@"symbol"]!=previous[@"symbol"] && ![model[@"symbol"] isEqual:previous[@"symbol"]])) {
            if([model[@"symbol"] isKindOfClass:NSString.class])self.image=[[NSImage imageWithSystemSymbolName:model[@"symbol"] accessibilityDescription:text] copy];
            else {
                NSData *png=[model[@"image"] isKindOfClass:NSString.class]?[[NSData alloc]initWithBase64EncodedString:model[@"image"] options:0]:nil;
                self.image=png?[[NSImage alloc]initWithData:png]:nil;
            }
        }
        if(self.image && [model[@"imageWidth"] doubleValue]>0 && [model[@"imageHeight"] doubleValue]>0){
            // WinForms exposes one icon box for the whole strip. It is often
            // square even when a command's artwork is a wide labelled banner.
            // Keep the encoded pixel ratio, including after repeated resizes.
            NSImageRep *source=self.image.representations.firstObject;
            CGFloat sourceWidth=source.pixelsWide,sourceHeight=source.pixelsHigh;
            if(model[@"symbol"]){sourceWidth=self.image.size.width;sourceHeight=self.image.size.height;}
            if(sourceWidth>0 && sourceHeight>0){
                CGFloat sx=self.view.frame.size.width/MAX(1,[model[@"w"] doubleValue]);
                CGFloat sy=self.view.frame.size.height/MAX(1,[model[@"h"] doubleValue]);
                BOOL banner=[model[@"imageOnly"] boolValue] && sourceWidth>=2*sourceHeight;
                CGFloat height=banner?MAX(0,button.bounds.size.height-2):MIN([model[@"imageHeight"] doubleValue]*sy,MAX(0,button.bounds.size.height-2));
                CGFloat width=[model[@"imageOnly"] boolValue]?MAX(0,button.bounds.size.width-4):[model[@"imageWidth"] doubleValue]*sx;
                CGFloat scale=MIN(width/sourceWidth,height/sourceHeight);
                self.image.size=NSMakeSize(sourceWidth*scale,sourceHeight*scale);
            }
        }
        button.imagePosition=[model[@"imageOnly"] boolValue]?NSImageOnly:NSImageLeft;
        if(![kind isEqual:@"menu"])button.image=self.image;
    }
    if([@[@"button",@"check",@"radio"] containsObject:kind]){NSButton *button=(NSButton *)self.view;button.title=[model[@"imageOnly"] boolValue]?@"":text;
        button.state=[model[@"value"] integerValue]==2?NSControlStateValueMixed:[model[@"value"] integerValue];}
    else if([kind isEqual:@"label"]){
        NSTextField *label=(NSTextField *)self.view;label.stringValue=text;
        NSColor *color=foreground ?: NSColor.labelColor;
        label.textColor=[model[@"enabled"] boolValue]?color:[color colorWithAlphaComponent:0.5];
        if([model[@"toolbar"] boolValue]){
            label.maximumNumberOfLines=1;label.lineBreakMode=NSLineBreakByClipping;
            NSRect frame=label.frame;CGFloat height=MIN(frame.size.height,[label.cell cellSizeForBounds:frame].height);
            frame.origin.y+=(frame.size.height-height)/2;frame.size.height=height;label.frame=frame;
        }
    }
    else if([kind isEqual:@"text"] || [kind isEqual:@"number"]){
        if([self.view isKindOfClass:NSScrollView.class]){
            NSTextView *editor=(NSTextView *)[(NSScrollView *)self.view documentView];
            if(![editor.string isEqual:text] && editor.window.firstResponder!=editor)editor.string=text;
            editor.editable=![model[@"readonly"] boolValue] && [model[@"enabled"] boolValue];
        } else {
            NSTextField *field=(NSTextField *)self.view;
            if(![field.stringValue isEqual:text] && !field.currentEditor)field.stringValue=text;
            field.editable=![model[@"readonly"] boolValue];
        }
    } else if([kind isEqual:@"combo"]){
        NSPopUpButton *button=(NSPopUpButton *)self.view;NSArray *items=model[@"items"];
        if(![items isEqual:previous[@"items"]]){[button removeAllItems];[button addItemsWithTitles:items];}
        NSInteger selected=[model[@"value"] integerValue];if(selected>=0 && selected<button.numberOfItems)[button selectItemAtIndex:selected];
    } else if([kind isEqual:@"menu"]){
        NSPopUpButton *button=(NSPopUpButton *)self.view;
        if(![model[@"items"] isEqual:previous[@"items"]] || ![text isEqual:previous[@"text"]]){
            NSMenu *menu=[self menu:model[@"items"]];[menu insertItemWithTitle:text action:nil keyEquivalent:@"" atIndex:0];button.menu=menu;
        }
        NSMenuItem *title=button.itemArray.firstObject;title.image=self.image;
        title.title=[model[@"imageOnly"] boolValue]?@"":text;
    } else if([kind isEqual:@"slider"]){NSSlider *slider=(NSSlider *)self.view;slider.minValue=[model[@"min"] doubleValue];slider.maxValue=[model[@"max"] doubleValue];slider.doubleValue=[model[@"value"] doubleValue];}
    else if([kind isEqual:@"progress"]){NSProgressIndicator *progress=(NSProgressIndicator *)self.view;progress.minValue=[model[@"min"] doubleValue];progress.maxValue=[model[@"max"] doubleValue];progress.doubleValue=[model[@"value"] doubleValue];progress.indeterminate=[model[@"indeterminate"] boolValue];if(progress.indeterminate)[progress startAnimation:nil];else [progress stopAnimation:nil];}
    else if([kind isEqual:@"tabs"]){NSSegmentedControl *tabs=(NSSegmentedControl *)self.view;NSArray *items=model[@"items"];tabs.segmentCount=items.count;for(NSUInteger i=0;i<items.count;i++)[tabs setLabel:items[i] forSegment:i];tabs.selectedSegment=[model[@"value"] integerValue];}
    else if([kind isEqual:@"list"] || [kind isEqual:@"tree"]){
        NSTableView *table=(NSTableView *)[(NSScrollView *)self.view documentView];
        NSArray *rows=model[[kind isEqual:@"tree"]?@"nodes":@"items"];
        if(![rows isEqual:self.rows]){self.rows=rows;[table reloadData];if([table isKindOfClass:NSOutlineView.class])[self expand:(NSOutlineView *)table nodes:rows];}
        NSInteger selected=[model[@"value"] integerValue];
        if([kind isEqual:@"tree"]){selected=-1;for(NSInteger i=0;i<table.numberOfRows;i++){NSDictionary *node=[(NSOutlineView *)table itemAtRow:i];if([node[@"id"] isEqual:model[@"selected"]]){selected=i;break;}}}
        if(selected>=0 && selected<table.numberOfRows)[table selectRowIndexes:[NSIndexSet indexSetWithIndex:selected] byExtendingSelection:NO];else [table deselectAll:nil];
        table.enabled=[model[@"enabled"] boolValue];
    }
    if([model[@"toolbar"] boolValue] && [self.view isKindOfClass:NSButton.class] && ![model[@"imageOnly"] boolValue]){
        NSButton *button=(NSButton *)self.view;
        // Font metrics differ slightly from Segoe/Tahoma. Fit small differences
        // in the original hit rectangle rather than truncating a menu caption.
        CGFloat minimum=button.font.pointSize*0.9;
        for(int pass=0;pass<2;pass++){
            CGFloat available=[button.cell titleRectForBounds:button.bounds].size.width;
            CGFloat needed=[button.title sizeWithAttributes:@{NSFontAttributeName:button.font}].width;
            if(available<=0 || needed<=available)break;
            CGFloat size=MAX(minimum,button.font.pointSize*available/needed);
            button.font=[NSFont fontWithDescriptor:button.font.fontDescriptor size:size];
        }
    }
    self.updating=NO;
}
- (void)expand:(NSOutlineView *)outline nodes:(NSArray *)nodes {for(NSDictionary *node in nodes){if([node[@"expanded"] boolValue])[outline expandItem:node];[self expand:outline nodes:node[@"children"]];}}
- (NSInteger)numberOfRowsInTableView:(NSTableView *)table {return self.rows.count;}
- (id)tableView:(NSTableView *)table objectValueForTableColumn:(NSTableColumn *)column row:(NSInteger)row {return self.rows[row];}
- (void)tableViewSelectionDidChange:(NSNotification *)note {if(!self.updating)emit(@{@"event":@"action",@"control":self.identifier,@"value":@([(NSTableView *)note.object selectedRow])});}
- (NSInteger)outlineView:(NSOutlineView *)outline numberOfChildrenOfItem:(id)item {return item?[item[@"children"] count]:self.rows.count;}
- (id)outlineView:(NSOutlineView *)outline child:(NSInteger)index ofItem:(id)item {return item?item[@"children"][index]:self.rows[index];}
- (BOOL)outlineView:(NSOutlineView *)outline isItemExpandable:(id)item {return [item[@"children"] count]>0;}
- (id)outlineView:(NSOutlineView *)outline objectValueForTableColumn:(NSTableColumn *)column byItem:(id)item {return item[@"text"];}
- (void)outlineViewSelectionDidChange:(NSNotification *)note {NSOutlineView *outline=note.object;id node=[outline itemAtRow:outline.selectedRow];if(!self.updating && node)emit(@{@"event":@"action",@"control":self.identifier,@"value":node[@"id"]});}
- (void)outlineViewItemDidExpand:(NSNotification *)note {if(!self.updating)emit(@{@"event":@"action",@"control":self.identifier,@"value":note.userInfo[@"NSObject"][@"id"],@"expanded":@YES});}
- (void)outlineViewItemDidCollapse:(NSNotification *)note {if(!self.updating)emit(@{@"event":@"action",@"control":self.identifier,@"value":note.userInfo[@"NSObject"][@"id"],@"expanded":@NO});}
@end

static void command(NSDictionary *request) {
    if(!browsers)browsers=[NSMutableDictionary new];if(!forms)forms=[NSMutableDictionary new];
    NSString *op=request[@"op"];NSNumber *identifier=request[@"browser"];
    if([op isEqual:@"hello"]){reply(request,@{@"version":@1,@"mainThread":@([NSThread isMainThread]),@"wineClass":@(NSClassFromString(@"WineWindow")!=Nil)});return;}
    if([op isEqual:@"probe"]){NSWindow *window=wineWindow([request[@"hwnd"] unsignedLongLongValue]);reply(request,@{@"found":@(window!=nil),@"class":window?NSStringFromClass(window.class):@"none",@"bounds":window?NSStringFromRect(window.contentView.bounds):@"none",@"browsers":@(browsers.count),@"forms":@(forms.count)});return;}
    if([op isEqual:@"selfTestControl"] && getenv("CMO_NATIVE_SELF_TEST") && strcmp(getenv("CMO_NATIVE_SELF_TEST"),"1")==0){
        CMOPeer *peer=nil; for(NSDictionary *peers in forms.allValues) if(peers[request[@"control"]])peer=peers[request[@"control"]];
        if(!peer){fail(request,@"Native control not found.");return;}
        if([request[@"click"] boolValue] && [peer.view isKindOfClass:NSButton.class])[(NSButton *)peer.view performClick:nil];
        if(request[@"menuItem"] && [peer.view isKindOfClass:NSPopUpButton.class]){
            NSMenu *menu=[(NSPopUpButton *)peer.view menu];NSInteger index=[menu indexOfItemWithTag:[request[@"menuItem"] integerValue]];
            if(index<0){fail(request,@"Native menu item missing.");return;}[menu performActionForItemAtIndex:index];
        }
        NSColor *background=[[NSColor colorWithCGColor:peer.clip.layer.backgroundColor] colorUsingColorSpace:NSColorSpace.sRGBColorSpace];
        NSString *appearance=[peer.view.effectiveAppearance bestMatchFromAppearancesWithNames:@[NSAppearanceNameAqua,NSAppearanceNameDarkAqua]];
        NSView *content=peer.view.window.contentView;
        NSPoint center=[peer.view convertPoint:NSMakePoint(NSMidX(peer.view.bounds),NSMidY(peer.view.bounds)) toView:content.superview];
        NSView *hit=[content hitTest:center];
        reply(request,@{@"class":NSStringFromClass(peer.view.class),@"hitVisible":@(hit==peer.clip || [hit isDescendantOf:peer.clip]),@"embedded":@([peer.clip.superview isEqual:content]),@"frame":NSStringFromRect(peer.clip.frame),@"background":@[@(lround(background.redComponent*255)),@(lround(background.greenComponent*255)),@(lround(background.blueComponent*255))],@"appearance":appearance ?: @"",@"text":peer.model[@"text"] ?: @"",@"symbol":peer.model[@"symbol"] ?: @"",@"x":@(peer.clip.frame.origin.x),@"width":@(peer.clip.frame.size.width),@"imageSize":@[@(peer.image.size.width),@(peer.image.size.height)]});return;
    }
    if([op isEqual:@"selfTestSnapshot"] && getenv("CMO_NATIVE_SELF_TEST") && strcmp(getenv("CMO_NATIVE_SELF_TEST"),"1")==0){
        NSWindow *window=wineWindow([request[@"hwnd"] unsignedLongLongValue]);
        if(!window){fail(request,@"Window missing.");return;}
        NSBitmapImageRep *bitmap=[window.contentView bitmapImageRepForCachingDisplayInRect:window.contentView.bounds];
        [window.contentView cacheDisplayInRect:window.contentView.bounds toBitmapImageRep:bitmap];
        NSData *png=[bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
        BOOL saved=[png writeToFile:request[@"path"] atomically:YES];
        CMOBrowser *browser=browsers.allValues.firstObject;
        if(browser) {
            [browser.web takeSnapshotWithConfiguration:nil completionHandler:^(NSImage *image,NSError *error){
                NSData *tiff=image.TIFFRepresentation;
                NSData *webPNG=tiff?[[NSBitmapImageRep imageRepWithData:tiff] representationUsingType:NSBitmapImageFileTypePNG properties:@{}]:nil;
                NSString *path=[request[@"path"] stringByAppendingString:@".web.png"];
                BOOL webSaved=webPNG && [webPNG writeToFile:path atomically:YES];
                if(error)fail(request,error.localizedDescription);else reply(request,@{@"saved":@(saved),@"webSaved":@(webSaved)});
            }];
        } else reply(request,@{@"saved":@(saved)});return;
    }
    if([op isEqual:@"guiDispose"]){for(CMOPeer *peer in forms[identifier].allValues)[peer.clip removeFromSuperview];[forms removeObjectForKey:identifier];reply(request,nil);return;}
    if([op isEqual:@"gui"]){
        NSMutableDictionary *peers=forms[identifier];if(!peers){peers=[NSMutableDictionary new];forms[identifier]=peers;}
        NSMutableSet *active=[NSMutableSet new];NSMutableArray *attached=[NSMutableArray new];
        for(NSDictionary *model in request[@"views"]){NSNumber *key=model[@"id"];[active addObject:key];CMOPeer *peer=peers[key];if(!peer){peer=[CMOPeer new];peers[key]=peer;}[peer update:model];if(peer.view.window && peer.clip.superview==peer.view.window.contentView)[attached addObject:key];}
        for(NSNumber *key in peers.allKeys)if(![active containsObject:key]){[(CMOPeer *)peers[key] clip].hidden=YES;[[(CMOPeer *)peers[key] clip] removeFromSuperview];[peers removeObjectForKey:key];}
        // A compact row can appear after its controls already exist. Keep its
        // new background below those peers, above the underlying Wine view.
        for(CMOPeer *surface in peers.allValues)if([surface.kind isEqual:@"surface"] && surface.clip.superview){
            NSView *parent=surface.clip.superview;NSArray *siblings=parent.subviews;
            NSUInteger first=[siblings indexOfObject:surface.clip];NSView *anchor=nil;
            for(CMOPeer *peer in peers.allValues)if(peer!=surface && peer.clip.superview==parent && ![peer.kind isEqual:@"surface"] && NSIntersectsRect(peer.clip.frame,surface.clip.frame)){
                NSUInteger index=[siblings indexOfObject:peer.clip];if(index<first){first=index;anchor=peer.clip;}
            }
            if(anchor)[parent addSubview:surface.clip positioned:NSWindowBelow relativeTo:anchor];
        }
        for(CMOPeer *peer in peers.allValues)if(peer.view.window){[peer.view.window recalculateKeyViewLoop];break;}
        reply(request,@{@"controls":@(peers.count),@"attached":attached});return;
    }
    if([op isEqual:@"create"]){if(browsers[identifier]){fail(request,@"Browser already exists.");return;}CMOBrowser *browser=[CMOBrowser new];[browser create:request];if(!browser.disposed)browsers[identifier]=browser;return;}
    CMOBrowser *browser=browsers[identifier];
    if([op isEqual:@"dispose"]){[browser dispose];[browsers removeObjectForKey:identifier];reply(request,nil);return;}
    if(!browser){fail(request,@"The native browser is not initialized.");return;}
    if([op isEqual:@"geometry"]){position(browser.web,browser.clip,request);}
    else if([op isEqual:@"html"]){[browser.web loadHTMLString:[browser rewrite:request[@"html"]] baseURL:nil];}
    else if([op isEqual:@"navigate"]){NSString *error=nil;NSURL *url=[browser navigationURL:request error:&error];if(!url){fail(request,error ?: @"Invalid browser URL.");return;}[browser.web loadRequest:[NSURLRequest requestWithURL:url]];}
    else if([op isEqual:@"map"]){
        NSString *host=[request[@"host"] lowercaseString], *root=mappedPath(request[@"path"],browser.prefix);BOOL directory=NO;
        if(host.length==0 || [host rangeOfCharacterFromSet:[[NSCharacterSet characterSetWithCharactersInString:@"abcdefghijklmnopqrstuvwxyz0123456789.-"] invertedSet]].location!=NSNotFound || ![[NSFileManager defaultManager] fileExistsAtPath:root isDirectory:&directory] || !directory){fail(request,@"Invalid native resource folder mapping.");return;}
        browser.roots[host]=root;
    } else if([op isEqual:@"unmap"])[browser.roots removeObjectForKey:[request[@"host"] lowercaseString]];
    else if([op isEqual:@"script"]){
        NSString *script=[browser rewrite:request[@"script"]];
        [browser.web evaluateJavaScript:script completionHandler:^(id result,NSError *error){if(error)fail(request,error.localizedDescription);else reply(request,@{@"result":json(result)});}];return;
    } else if([op isEqual:@"decision"]){NSNumber *key=request[@"navigation"];id handler=browser.decisions[key];if(handler){[browser.decisions removeObjectForKey:key];((void (^)(WKNavigationActionPolicy))handler)([request[@"cancel"] boolValue]?WKNavigationActionPolicyCancel:WKNavigationActionPolicyAllow);}}
    else if([op isEqual:@"settings"]){if(request[@"IsScriptEnabled"])browser.scriptsEnabled=[request[@"IsScriptEnabled"] boolValue];if(request[@"AreDefaultContextMenusEnabled"])browser.web.contextMenus=[request[@"AreDefaultContextMenusEnabled"] boolValue];}
    else if([op isEqual:@"zoom"])browser.web.pageZoom=[request[@"value"] doubleValue];
    else if([op isEqual:@"reload"])[browser.web reload];
    else if([op isEqual:@"stop"])[browser.web stopLoading];
    else if([op isEqual:@"goback"])[browser.web goBack];
    else if([op isEqual:@"goforward"])[browser.web goForward];
    else {fail(request,[@"Unsupported native command: " stringByAppendingString:op ?: @"(missing)"]);return;}
    reply(request,nil);
}
