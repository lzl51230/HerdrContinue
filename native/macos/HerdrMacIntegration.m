#import <AppKit/AppKit.h>
#import <ServiceManagement/ServiceManagement.h>
#import <Carbon/Carbon.h>

// All exports are called on the application's main thread, inside its signed bundle.
int hdac_login_status(void) { return (int)SMAppService.mainAppService.status; }

int hdac_login_set(int enabled, char *errorText, int capacity)
{
    @autoreleasepool {
        SMAppService *service = SMAppService.mainAppService;
        SMAppServiceStatus before = service.status;
        if ((enabled && (before == SMAppServiceStatusEnabled || before == SMAppServiceStatusRequiresApproval)) ||
            (!enabled && before == SMAppServiceStatusNotRegistered)) return 0;
        NSError *error = nil;
        BOOL success = enabled ? [service registerAndReturnError:&error] : [service unregisterAndReturnError:&error];
        if (success || (enabled && service.status == SMAppServiceStatusRequiresApproval)) return 0;
        NSString *message = [NSString stringWithFormat:@"%@ (%@:%ld)", error.localizedDescription ?: @"Login item operation failed", error.domain ?: @"ServiceManagement", (long)error.code];
        if (capacity > 0) snprintf(errorText, (size_t)capacity, "%s", message.UTF8String);
        return -1;
    }
}

void hdac_login_open_settings(void) { [SMAppService openSystemSettingsLoginItems]; }

static void ShowApprovalPrompt(int qaResponse)
{
    NSAlert *alert = [NSAlert new];
    alert.messageText = @"需要在系统设置中允许登录启动";
    alert.informativeText = @"Herdr Auto Continue 已登记登录项，但 macOS 尚未允许它自动启动。请前往“通用 → 登录项”，允许此应用在登录时打开。此提示框本身不会授予权限。";
    [alert addButtonWithTitle:@"前往设置"];
    [alert addButtonWithTitle:@"稍后处理"];
    alert.buttons[1].keyEquivalent = @"\e";
#ifdef HDAC_QA
    if (qaResponse >= 0) dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC), dispatch_get_main_queue(), ^{
        [alert.buttons[qaResponse ? 0 : 1] performClick:nil];
    });
#else
    (void)qaResponse;
#endif
    if ([alert runModal] == NSAlertFirstButtonReturn) [SMAppService openSystemSettingsLoginItems];
}
void hdac_login_prompt(void) { ShowApprovalPrompt(-1); }
#ifdef HDAC_QA
void hdac_login_qa_prompt(int settings) { ShowApprovalPrompt(settings); }
#endif

typedef void (*LaunchCallback)(int hidden);
static LaunchCallback launchCallback;
static BOOL forcedHidden;
static BOOL loginLaunch;
static BOOL delivered;

static BOOL IsLoginEvent(void)
{
    NSAppleEventDescriptor *event = NSAppleEventManager.sharedAppleEventManager.currentAppleEvent;
    if (event.eventClass == kCoreEventClass && event.eventID == kAEOpenApplication) {
        NSAppleEventDescriptor *reason = [event paramDescriptorForKeyword:keyAEPropData];
        return reason.enumCodeValue == keyAELaunchedAsLogInItem || reason.typeCodeValue == keyAELaunchedAsLogInItem;
    }
    return NO;
}
static void CaptureLaunchReason(void) { if (IsLoginEvent()) loginLaunch = YES; }

// Forward the framework's delegate contract, changing only initial presentation.
// In particular, suppress its automatic activation for a login/background launch.
@interface HDACLaunchDelegate : NSObject <NSApplicationDelegate>
@property(strong) id original;
@end
@implementation HDACLaunchDelegate
- (BOOL)respondsToSelector:(SEL)selector { return [super respondsToSelector:selector] || [self.original respondsToSelector:selector]; }
- (NSMethodSignature *)methodSignatureForSelector:(SEL)selector { return [super methodSignatureForSelector:selector] ?: [self.original methodSignatureForSelector:selector]; }
- (void)forwardInvocation:(NSInvocation *)invocation { [invocation invokeWithTarget:self.original]; }
- (BOOL)applicationShouldOpenUntitledFile:(NSApplication *)application { CaptureLaunchReason(); return NO; }
- (BOOL)applicationShouldHandleReopen:(NSApplication *)application hasVisibleWindows:(BOOL)visible
{
    if (IsLoginEvent()) return NO;
    return [self.original respondsToSelector:_cmd] ? [self.original applicationShouldHandleReopen:application hasVisibleWindows:visible] : YES;
}
- (void)applicationWillFinishLaunching:(NSNotification *)notification
{
    CaptureLaunchReason();
    if ([self.original respondsToSelector:_cmd]) [self.original applicationWillFinishLaunching:notification];
}
- (void)applicationDidFinishLaunching:(NSNotification *)notification
{
    CaptureLaunchReason();
    BOOL hidden = forcedHidden || loginLaunch;
    [NSApp setActivationPolicy:hidden ? NSApplicationActivationPolicyAccessory : NSApplicationActivationPolicyRegular];
    if (!delivered) { delivered = YES; if (launchCallback) launchCallback(hidden ? 1 : 0); }
    if (!hidden && [self.original respondsToSelector:_cmd]) [self.original applicationDidFinishLaunching:notification];
}
@end
static HDACLaunchDelegate *launchDelegate;

void hdac_launch_install(LaunchCallback callback, int hidden)
{
    launchCallback = callback; forcedHidden = hidden != 0;
    launchDelegate = [HDACLaunchDelegate new];
    launchDelegate.original = NSApp.delegate;
    NSApp.delegate = launchDelegate;
}
void hdac_launch_reveal(void) { [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular]; }
void hdac_launch_restore(void)
{
    if (launchDelegate && NSApp.delegate == launchDelegate) NSApp.delegate = launchDelegate.original;
    launchDelegate = nil; launchCallback = NULL;
}
