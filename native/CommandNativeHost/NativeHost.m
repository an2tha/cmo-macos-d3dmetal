#import <AppKit/AppKit.h>
#import <WebKit/WebKit.h>
#import <objc/runtime.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <stdint.h>
#include <string.h>

// Native queue ownership never crosses into the CLR. Messages are copied.
static NSMutableArray<NSData *> *replies;
static NSObject *queueLock;
static NSUInteger queueBytes;
static const NSUInteger maxFrame = 8 * 1024 * 1024;
static void initializeQueue(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{ queueLock = [NSObject new]; replies = [NSMutableArray new]; });
}
static void emit(NSDictionary *message) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:message options:NSJSONWritingFragmentsAllowed error:nil];
    if (!data || data.length > maxFrame) return;
    @synchronized(queueLock) {
        if (queueBytes + data.length > 32 * 1024 * 1024) return;
        [replies addObject:data]; queueBytes += data.length;
    }
}
static void reply(NSDictionary *request, NSDictionary *values) {
    NSMutableDictionary *message = [values mutableCopy] ?: [NSMutableDictionary new];
    message[@"reply"] = request[@"id"] ?: @0; emit(message);
}
static void fail(NSDictionary *request, NSString *error) { reply(request, @{@"error": error}); }
static NSWindow *wineWindow(uint64_t hwnd) {
    Class cls = NSClassFromString(@"WineWindow");
    Ivar ivar = cls ? class_getInstanceVariable(cls, "hwnd") : NULL;
    if (!ivar || strcmp(ivar_getTypeEncoding(ivar), "^v") != 0) return nil;
    ptrdiff_t offset = ivar_getOffset(ivar);
    if (offset < 0 || (size_t)offset + sizeof(void *) > class_getInstanceSize(cls)) return nil;
    for (NSWindow *window in NSApp.windows) {
        if (![window isKindOfClass:cls]) continue;
        void *handle = NULL;
        memcpy(&handle, (const char *)(__bridge const void *)window + offset, sizeof(handle));
        if ((uint64_t)handle == hwnd) return window;
    }
    return nil;
}
#include "Views.inc"

struct cmo_call { uint64_t operation; void *bytes; uint64_t capacity; uint64_t used; };
__attribute__((visibility("default"))) int32_t cmo_native_call(struct cmo_call *call) {
    if (!call || call->capacity > maxFrame) return (int32_t)0xc000000d;
    initializeQueue(); call->used = 0;
    @autoreleasepool {
        if (call->operation == 0) {
            if (!call->bytes || !call->capacity) return (int32_t)0xc000000d;
            NSData *data = [NSData dataWithBytes:call->bytes length:(NSUInteger)call->capacity];
            id request = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
            if (![request isKindOfClass:NSDictionary.class]) return (int32_t)0xc000000d;
            CFRunLoopPerformBlock(CFRunLoopGetMain(), kCFRunLoopCommonModes, ^{
                @try { command(request); }
                @catch (NSException *exception) { fail(request, exception.reason ?: @"Native UI exception"); }
            });
            CFRunLoopWakeUp(CFRunLoopGetMain());
            return 0;
        }
        if (call->operation == 1) {
            @synchronized(queueLock) {
                NSData *data = replies.firstObject;
                call->used = data.length;
                if (data && call->bytes && call->capacity >= data.length) {
                    memcpy(call->bytes, data.bytes, data.length);
                    queueBytes -= data.length; [replies removeObjectAtIndex:0];
                }
            }
            return 0;
        }
    }
    return (int32_t)0xc000000d;
}
