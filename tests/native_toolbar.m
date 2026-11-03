// Standalone AppKit regression fixture. It never starts Wine or the game.
#import "../native/CommandNativeHost/NativeHost.m"

@interface WineWindow : NSWindow { @public void *hwnd; }
@end
@implementation WineWindow
- (NSRect)constrainFrameRect:(NSRect)frame toScreen:(NSScreen *)screen { return frame; }
@end
@interface FixtureContent : NSView
@property NSColor *fill;
@end
@implementation FixtureContent
- (void)drawRect:(NSRect)rect { [self.fill setFill];NSRectFill(rect); }
@end

static void require(BOOL value, NSString *message) {
    if(!value){fprintf(stderr,"FAIL %s\n",message.UTF8String);exit(1);}
    printf("PASS %s\n",message.UTF8String);
}
static NSMutableDictionary *model(NSInteger identifier, NSString *kind, NSString *text, NSRect frame, CGFloat width, BOOL dark) {
    return [@{@"id":@(identifier),@"kind":kind,@"text":text,@"hwnd":@1,
        @"fx":@0,@"fy":@0,@"fw":@(width),@"fh":@150,
        @"x":@(frame.origin.x),@"y":@(frame.origin.y),@"w":@(frame.size.width),@"h":@(frame.size.height),
        @"cx":@0,@"cy":@0,@"cw":@(width),@"ch":@150,
        @"background":dark?@[@60,@63,@65]:@[@240,@240,@240],
        @"foreground":dark?@[@235,@237,@239]:@[@28,@30,@32],
        @"visible":@YES,@"enabled":@YES,@"font":@12,@"toolbar":@YES} mutableCopy];
}
static NSDictionary *lastEvent(void) {
    for(NSData *data in replies.reverseObjectEnumerator){
        NSDictionary *event=[NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
        if(event[@"event"])return event;
    }
    return nil;
}
static void fixture(NSString *directory, NSString *assets, CGFloat width, BOOL dark) {
    WineWindow *window=[[WineWindow alloc]initWithContentRect:NSMakeRect(80,80,width,150) styleMask:NSWindowStyleMaskTitled backing:NSBackingStoreBuffered defer:NO];
    window->hwnd=(void *)1;window.releasedWhenClosed=NO;
    window.title=@"Command toolbar validation";
    window.backgroundColor=modelColor(dark?@[@60,@63,@65]:@[@240,@240,@240]);
    FixtureContent *content=[[FixtureContent alloc]initWithFrame:NSMakeRect(0,0,width,150)];
    content.fill=window.backgroundColor;window.contentView=content;
    [window orderFront:nil];
    NSMutableArray *views=[NSMutableArray new];
    NSArray *titles=@[@"File",@"View",@"Editor",@"Map Settings",@"Quick Battle",@"Unit Orders",@"Missions + Ref. Points",@"Campaign",@"Help"];
    NSArray *widths=@[@36,@42,@46,@118,@76,@76,@126,@68,@40];
    CGFloat x=0;
    for(NSUInteger i=0;i<titles.count;i++){
        NSMutableDictionary *entry=model(i+1,@"menu",titles[i],NSMakeRect(x,0,[widths[i] doubleValue],24),width,dark);
        entry[@"menubar"]=@YES;
        entry[@"enabled"]=@(i!=7);
        entry[@"items"]=@[@{@"id":@(100+i),@"text":@"Open scenario…",@"enabled":@YES},
            @{@"id":@(200+i),@"text":@"Unavailable command",@"enabled":@NO},
            @{@"id":@(300+i),@"text":@"Show reference points",@"enabled":@YES,@"checked":@YES},
            @{@"id":@(400+i),@"text":@"More",@"enabled":@YES,@"children":@[@{@"id":@(500+i),@"text":@"Nested command",@"enabled":@YES}]}];
        [views addObject:entry];x+=[widths[i] doubleValue];
    }
    [views addObject:model(20,@"label",@"Time",NSMakeRect(4,30,52,26),width,dark)];
    [views addObject:model(21,@"label",@"1×",NSMakeRect(70,30,30,26),width,dark)];
    NSMutableDictionary *resume=model(22,@"button",@"Start / Resume",NSMakeRect(178,30,104,26),width,dark);
    NSData *play=[NSData dataWithContentsOfFile:[assets stringByAppendingPathComponent:@"Play.gif"]];
    require(play!=nil,@"game play icon is available");
    resume[@"image"]=[play base64EncodedStringWithOptions:0];resume[@"imageWidth"]=@12;resume[@"imageHeight"]=@12;
    [views addObject:resume];
    [views addObject:model(23,@"button",@"Custom Image",NSMakeRect(295,30,89,26),width,dark)];
    [views addObject:model(24,@"button",@"Record",NSMakeRect(390,30,66,26),width,dark)];
    NSArray *files=@[@"Btn_Auto_Engage_Target.png",@"Btn_Manually_Engage_Target.png",@"Btn_Formation_Editor.png",@"Btn_Magazines.png",@"Btn_Sensors.png",@"Btn_Mission_Editor.png"];
    for(NSUInteger i=0;i<files.count;i++){
        NSMutableDictionary *entry=model(30+i,@"button",[files[i] stringByDeletingPathExtension],NSMakeRect(6+i*114,64,106,30),width,dark);
        NSData *data=[NSData dataWithContentsOfFile:[assets stringByAppendingPathComponent:files[i]]];
        require(data!=nil,[@"game toolbar asset: " stringByAppendingString:files[i]]);
        entry[@"image"]=[data base64EncodedStringWithOptions:0];entry[@"imageOnly"]=@YES;
        // The game's strip supplies a square icon box for wide command artwork.
        entry[@"imageWidth"]=@16;entry[@"imageHeight"]=@16;
        [views addObject:entry];
    }
    // A normal dialog button and combo retain their usual native affordances.
    NSMutableDictionary *ordinary=model(60,@"button",@"Apply orders",NSMakeRect(8,108,120,28),width,dark);ordinary[@"toolbar"]=@NO;ordinary[@"systemForeground"]=@YES;[views addObject:ordinary];
    NSMutableDictionary *combo=model(61,@"combo",@"",NSMakeRect(150,108,150,28),width,dark);combo[@"toolbar"]=@NO;combo[@"items"]=@[@"Patrol",@"Strike"];combo[@"value"]=@1;[views addObject:combo];
    command(@{@"op":@"gui",@"browser":@1,@"views":views});
    NSDictionary *peers=forms[@1];
    for(NSNumber *key in @[@1,@2,@3,@4,@5,@6,@7,@9,@22,@23,@24]){
        CMOPeer *peer=peers[key];NSButton *button=(NSButton *)peer.view;
        CGFloat titleWidth=[button.title sizeWithAttributes:@{NSFontAttributeName:button.font}].width;
        NSRect titleRect=[button.cell titleRectForBounds:button.bounds];
        require(titleWidth<=titleRect.size.width+0.5,[NSString stringWithFormat:@"caption fits: %@ (%.1f / %.1f)",button.title,titleWidth,titleRect.size.width]);
        require(button.window==window && !button.bordered,@"compact toolbar peer is embedded and borderless");
    }
    CMOPeer *file=peers[@1];NSPopUpButton *menu=(NSPopUpButton *)file.view;
    require([(NSPopUpButtonCell *)menu.cell arrowPosition]==NSPopUpNoArrow,@"top-level menu reserves no popup arrow");
    require(![menu.menu itemAtIndex:2].enabled && [menu.menu itemAtIndex:3].state==NSControlStateValueOn,@"menu disabled and checked states survive");
    [menu.menu performActionForItemAtIndex:1];
    require([lastEvent()[@"control"] isEqual:@100],@"native menu dispatches the original command identity");
    [[menu.menu itemAtIndex:4].submenu performActionForItemAtIndex:0];
    require([lastEvent()[@"control"] isEqual:@500],@"nested native menu dispatches the original command identity");
    NSButton *start=(NSButton *)[(CMOPeer *)peers[@22] view];
    [start performClick:nil];require([lastEvent()[@"control"] isEqual:@22],@"toolbar button dispatches its original action");
    NSUInteger beforeDisabled=replies.count;
    NSMutableDictionary *disabled=[resume mutableCopy];disabled[@"enabled"]=@NO;
    [(CMOPeer *)peers[@22] update:disabled];[start performClick:nil];
    require(replies.count==beforeDisabled,@"disabled toolbar command does not fire");
    [(CMOPeer *)peers[@22] update:resume];
    require([window makeFirstResponder:menu] && window.firstResponder==menu,@"native menu accepts keyboard focus");
    NSButton *icon=(NSButton *)[(CMOPeer *)peers[@30] view];
    NSRect imageRect=[icon.cell imageRectForBounds:icon.bounds];
    require(imageRect.size.width>=99 && imageRect.size.height>=27,@"114 by 32 artwork occupies the toolbar at its natural ratio");
    NSImageRep *source=icon.image.representations.firstObject;
    CGFloat ratio=(CGFloat)source.pixelsWide/source.pixelsHigh;
    require(fabs(icon.image.size.width/icon.image.size.height-ratio)<0.001,@"square strip metadata preserves the artwork's pixel aspect ratio");
    CMOPeer *banner=peers[@30];NSMutableDictionary *wide=[banner.model mutableCopy];
    NSMutableDictionary *narrow=[wide mutableCopy];narrow[@"w"]=@48;
    [banner update:narrow];imageRect=[icon.cell imageRectForBounds:icon.bounds];
    require(imageRect.size.width<=44.1 && fabs(icon.image.size.width/icon.image.size.height-ratio)<0.001,@"narrow toolbar items shrink artwork proportionally inside their hit area");
    [banner update:wide];
    require(icon.image.size.width>=99 && fabs(icon.image.size.width/icon.image.size.height-ratio)<0.001,@"resizing restores the banner without accumulating distortion");
    NSButton *dialog=(NSButton *)[(CMOPeer *)peers[@60] view];
    require(dialog.bordered,@"ordinary dialog buttons keep their native bezel");
    require(dialog.contentTintColor==nil,@"system text colors follow the native appearance");
    NSColor *background=[NSColor colorWithCGColor:file.clip.layer.backgroundColor];
    require(fabs(background.redComponent-(dark?60:240)/255.0)<0.01,@"opaque peer background matches the toolbar palette");
    [window display];
    NSBitmapImageRep *bitmap=[window.contentView bitmapImageRepForCachingDisplayInRect:window.contentView.bounds];
    [window.contentView cacheDisplayInRect:window.contentView.bounds toBitmapImageRep:bitmap];
    NSString *name=[NSString stringWithFormat:@"toolbar-%@-%.0f.png",dark?@"dark":@"light",width];
    require([[bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}] writeToFile:[directory stringByAppendingPathComponent:name] atomically:YES],@"preview saved");
    command(@{@"op":@"guiDispose",@"browser":@1});
    require(window.contentView.subviews.count==0,@"disposing the toolbar removes every native peer");
    window->hwnd=NULL;
    [window close];
}
int main(int argc,const char **argv){
    @autoreleasepool {
        if(argc!=3){fprintf(stderr,"usage: native-toolbar OUTPUT_DIRECTORY GAME_MENU_ASSETS\n");return 2;}
        [NSApplication sharedApplication];[NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];initializeQueue();
        NSString *directory=@(argv[1]);NSString *assets=@(argv[2]);
        [[NSFileManager defaultManager] createDirectoryAtPath:directory withIntermediateDirectories:YES attributes:nil error:nil];
        fixture(directory,assets,1000,YES);fixture(directory,assets,1600,YES);fixture(directory,assets,1000,NO);
        return 0;
    }
}
