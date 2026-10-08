#import <Cocoa/Cocoa.h>
#import <CommonCrypto/CommonDigest.h>
#include <mach-o/dyld.h>
#include <sys/file.h>
#include <unistd.h>
#include "Host.h"
#include "SetupMac.h"

static NSString *applicationPath, *applicationBase, *runtimeConfig, *runtimeBase;
static NSString *recordedRuntime(void) {
    NSString *path = [runtimeBase stringByAppendingPathComponent:@"runtime-root-osx-arm64.txt"];
    NSString *root = [NSString stringWithContentsOfFile:path encoding:NSUTF8StringEncoding error:nil];
    return root.length ? root : nil;
}

static BOOL recordRuntime(NSString *root, NSError **error) {
    if (![NSFileManager.defaultManager createDirectoryAtPath:runtimeBase withIntermediateDirectories:YES attributes:nil error:error]) return NO;
    NSString *path = [runtimeBase stringByAppendingPathComponent:@"runtime-root-osx-arm64.txt"];
    return [root writeToFile:path atomically:YES encoding:NSUTF8StringEncoding error:error];
}

static NSString *findRuntime(void) {
    char result[AM_PATH];
    if (am_find_private_runtime(runtimeBase.fileSystemRepresentation, runtimeConfig.fileSystemRepresentation, result))
        return [NSString stringWithUTF8String:result];
    NSMutableArray<NSString *> *roots = [NSMutableArray array];
    for (NSString *variable in @[@"DOTNET_ROOT_ARM64", @"DOTNET_ROOT"]) {
        const char *value = getenv(variable.UTF8String);
        if (value && value[0]) [roots addObject:[NSString stringWithUTF8String:value]];
    }
    [roots addObjectsFromArray:@[@"/usr/local/share/dotnet", @"/opt/homebrew/share/dotnet", [NSHomeDirectory() stringByAppendingPathComponent:@".dotnet"]]];
    for (NSString *root in roots)
        if (am_runtime_usable(root.fileSystemRepresentation, runtimeConfig.fileSystemRepresentation)) return root;
    return nil;
}

static NSError *failure(NSString *message) {
    return [NSError errorWithDomain:@"AvaMedia.Runtime" code:1 userInfo:@{NSLocalizedDescriptionKey:message}];
}

@interface RuntimeInstaller : NSObject <NSWindowDelegate, NSApplicationDelegate>
@property NSWindow *window;
@property NSTextField *status;
@property NSProgressIndicator *progress;
@property NSButton *button;
@property NSButton *nextButton;
@property NSButton *backButton;
@property NSButton *exitButton;
@property NSTextField *heading;
@property NSTextField *runtimePath;
@property NSTextField *output;
@property NSPopUpButton *language;
@property NSButton *source;
@property NSButton *notify;
@property NSButton *motion;
@property NSButton *updates;
@property NSButton *gpu;
@property NSMutableArray<NSMutableArray<NSView *> *> *pages;
@property NSMutableArray<NSTextField *> *steps;
@property NSMutableArray<SetupSkinView *> *skins;
@property NSButton *previewButton;
@property NSWindow *previewWindow;
@property NSImageView *previewImage;
@property NSSegmentedControl *previewSelector;
@property NSInteger step;
@property NSInteger skin;
@property BOOL busy;
@property NSMutableArray<NSString *> *files;
@property NSString *runtime;
@end

@implementation RuntimeInstaller
- (instancetype)init {
    if ((self = [super init])) self.files = [NSMutableArray array];
    return self;
}
- (void)application:(NSApplication *)application openFiles:(NSArray<NSString *> *)filenames {
    [self.files addObjectsFromArray:filenames];
    [application replyToOpenOrPrint:NSApplicationDelegateReplySuccess];
}
- (BOOL)windowShouldClose:(NSWindow *)sender {
    if (sender == self.previewWindow) { [self closePreview:nil]; return NO; }
    if (self.busy) return NO;
    [NSApp stopModalWithCode:NSModalResponseCancel];
    return YES;
}
- (NSTextField *)label:(NSString *)text frame:(NSRect)frame page:(NSInteger)page {
    NSTextField *label = [NSTextField wrappingLabelWithString:text]; label.frame = frame;
    [self.window.contentView addSubview:label];
    if (page >= 0) [self.pages[page] addObject:label];
    return label;
}
- (NSButton *)action:(NSString *)title frame:(NSRect)frame selector:(SEL)selector page:(NSInteger)page {
    NSButton *button = [NSButton buttonWithTitle:title target:self action:selector]; button.frame = frame;
    [self.window.contentView addSubview:button];
    if (page >= 0) [self.pages[page] addObject:button];
    return button;
}
- (NSButton *)check:(NSString *)title y:(CGFloat)y checked:(BOOL)checked {
    NSButton *button = [NSButton checkboxWithTitle:title target:nil action:nil];
    button.frame = NSMakeRect(220, y, 536, 26); button.state = checked ? NSControlStateValueOn : NSControlStateValueOff;
    [self.window.contentView addSubview:button]; [self.pages[2] addObject:button]; return button;
}
- (void)refresh {
    NSArray *names = @[@"运行环境", @"界面风格", @"使用偏好"];
    NSArray *headings = @[@"准备运行环境", @"选择界面风格", @"设置使用偏好"];
    self.heading.stringValue = headings[self.step];
    for (NSInteger page = 0; page < 3; page++) {
        for (NSView *view in self.pages[page]) view.hidden = page != self.step;
        self.steps[page].stringValue = [NSString stringWithFormat:@"%@ %ld  %@", page == self.step ? @"●" : @"○", (long)page + 1, names[page]];
        self.steps[page].textColor = page == self.step ? NSColor.controlAccentColor : NSColor.secondaryLabelColor;
    }
    self.progress.hidden = self.step != 0 || !self.busy;
    self.nextButton.title = self.step == 2 ? @"完成并进入软件" : @"下一步";
    self.nextButton.enabled = !self.busy && (self.step != 0 || self.runtime != nil);
    self.backButton.enabled = !self.busy && self.step > 0;
    self.exitButton.enabled = !self.busy;
    self.button.enabled = !self.busy && !self.runtime;
    [self.window standardWindowButton:NSWindowCloseButton].enabled = !self.busy;
}
- (void)show {
    self.pages = [NSMutableArray arrayWithArray:@[[NSMutableArray array], [NSMutableArray array], [NSMutableArray array]]];
    self.steps = [NSMutableArray array]; self.skins = [NSMutableArray array];
    self.window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 800, 560)
        styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable backing:NSBackingStoreBuffered defer:NO];
    self.window.title = @"天池万象转换 · 首次使用配置"; self.window.delegate = self;
    [self label:@"天池万象转换" frame:NSMakeRect(24, 488, 180, 26) page:-1].font = [NSFont boldSystemFontOfSize:15];
    [self label:@"首次使用配置" frame:NSMakeRect(24, 455, 180, 26) page:-1].textColor = NSColor.secondaryLabelColor;
    for (int i = 0; i < 3; i++) [self.steps addObject:[self label:@"" frame:NSMakeRect(24, 380 - i * 52, 180, 30) page:-1]];
    self.heading = [self label:@"" frame:NSMakeRect(220, 488, 536, 38) page:-1]; self.heading.font = [NSFont boldSystemFontOfSize:23];
    self.backButton = [self action:@"上一步" frame:NSMakeRect(220, 20, 98, 34) selector:@selector(back:) page:-1];
    self.exitButton = [self action:@"退出" frame:NSMakeRect(24, 20, 90, 34) selector:@selector(exit:) page:-1];
    self.nextButton = [self action:@"下一步" frame:NSMakeRect(568, 20, 188, 34) selector:@selector(next:) page:-1];
    self.nextButton.keyEquivalent = @"\r"; self.exitButton.keyEquivalent = @"\e";
    [self label:@".NET 8 + ASP.NET Core 8" frame:NSMakeRect(220, 418, 536, 28) page:0].font = [NSFont boldSystemFontOfSize:15];
    self.status = [self label:self.runtime ? @"✓ 运行环境已就绪" : @"需要安装运行时" frame:NSMakeRect(220, 326, 536, 66) page:0];
    self.runtimePath = [self label:self.runtime ?: runtimeBase frame:NSMakeRect(220, 258, 536, 60) page:0];
    self.runtimePath.textColor = NSColor.secondaryLabelColor;
    self.button = [self action:self.runtime ? @"已安装" : @"安装运行时" frame:NSMakeRect(220, 208, 144, 34) selector:@selector(install:) page:0];
    self.progress = [[NSProgressIndicator alloc] initWithFrame:NSMakeRect(220, 182, 536, 8)];
    self.progress.style = NSProgressIndicatorStyleBar; self.progress.indeterminate = YES;
    [self.window.contentView addSubview:self.progress]; [self.pages[0] addObject:self.progress];
    [self label:@"安装到当前用户，无需管理员权限。" frame:NSMakeRect(220, 134, 536, 26) page:0].textColor = NSColor.secondaryLabelColor;
    NSArray *tools = @[@"ffmpeg", @"ffprobe", @"yt-dlp"];
    for (NSInteger i = 0; i < 3; i++) {
        NSString *path = [[applicationBase stringByAppendingPathComponent:@"tools"] stringByAppendingPathComponent:tools[i]];
        NSString *text = [NSString stringWithFormat:@"%@ · %@", tools[i], [NSFileManager.defaultManager isExecutableFileAtPath:path] ? @"内置" : @"缺失"];
        [self label:text frame:NSMakeRect(220 + i * 180, 88, 174, 26) page:0];
    }
    [self label:@"内置工具缺失时，请重新安装完整版本。" frame:NSMakeRect(220, 60, 536, 24) page:0].textColor = NSColor.secondaryLabelColor;
    [self label:@"选择皮肤，放大查看界面细节。" frame:NSMakeRect(220, 446, 370, 28) page:1].textColor = NSColor.secondaryLabelColor;
    self.previewButton = [self action:@"放大预览" frame:NSMakeRect(638, 440, 118, 34) selector:@selector(zoom:) page:1];
    NSArray *skinNames = @[@"浅色", @"深色", @"Mac OS 9", @"Windows XP"];
    for (NSInteger i = 0; i < AM_SETUP_SKIN_COUNT; i++) {
        SetupSkinView *view = [[SetupSkinView alloc] initWithFrame:NSMakeRect(220 + (i % 2) * 274, 262 - (i / 2) * 184, 262, 174)];
        view.skinIndex = i; view.chosen = i == self.skin; view.title = skinNames[i];
        view.target = self; view.action = @selector(selectSkin:); view.bordered = NO;
        view.previewAction = @selector(zoom:);
        NSString *path = [[applicationBase stringByAppendingPathComponent:@"setup-previews"] stringByAppendingPathComponent:
            [[NSString stringWithUTF8String:am_setup_skins[i].key] stringByAppendingString:@".png"]];
        view.preview = [[NSImage alloc] initWithContentsOfFile:path];
        view.accessibilityLabel = skinNames[i];
        view.accessibilityValue = view.chosen ? @"已选择" : @"";
        [self.window.contentView addSubview:view]; [self.pages[1] addObject:view]; [self.skins addObject:view];
    }
    self.previewButton.enabled = self.skins[self.skin].preview != nil;
    [self label:@"默认输出目录" frame:NSMakeRect(220, 435, 536, 26) page:2];
    self.output = [[NSTextField alloc] initWithFrame:NSMakeRect(220, 398, 416, 28)];
    self.output.stringValue = [NSHomeDirectory() stringByAppendingPathComponent:@"Movies/AvaMedia"];
    [self.window.contentView addSubview:self.output]; [self.pages[2] addObject:self.output];
    [self action:@"选择…" frame:NSMakeRect(648, 395, 108, 34) selector:@selector(pickFolder:) page:2];
    self.source = [self check:@"优先输出到源文件目录" y:352 checked:NO];
    [self label:@"软件语言" frame:NSMakeRect(220, 300, 130, 26) page:2];
    self.language = [[NSPopUpButton alloc] initWithFrame:NSMakeRect(360, 298, 260, 30) pullsDown:NO];
    [self.language addItemsWithTitles:@[@"跟随系统", @"简体中文", @"English"]];
    [self.window.contentView addSubview:self.language]; [self.pages[2] addObject:self.language];
    self.notify = [self check:@"任务完成通知" y:252 checked:YES];
    self.gpu = [self check:@"自动检测 GPU 加速" y:212 checked:YES];
    self.updates = [self check:@"启动时检查更新" y:172 checked:YES];
    self.motion = [self check:@"减少界面动画" y:132 checked:NO];
    [self label:@"这些选项也可以在软件设置中调整。" frame:NSMakeRect(220, 82, 536, 26) page:2].textColor = NSColor.secondaryLabelColor;
    [self refresh]; [self.window center]; [self.window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES];
}
- (void)back:(id)sender { (void)sender; if (!self.busy && self.step > 0) { self.step--; [self refresh]; } }
- (void)exit:(id)sender { (void)sender; if (!self.busy) [NSApp stopModalWithCode:NSModalResponseCancel]; }
- (void)selectSkin:(SetupSkinView *)sender {
    self.skin = sender.skinIndex;
    for (SetupSkinView *view in self.skins) {
        view.chosen = view.skinIndex == self.skin; view.needsDisplay = YES;
        view.accessibilityValue = view.chosen ? @"已选择" : @"";
    }
    self.previewButton.enabled = self.skins[self.skin].preview != nil;
}
- (void)zoom:(id)sender {
    (void)sender;
    if (!self.skins[self.skin].preview || self.previewWindow) return;
    NSRect available = self.window.screen.visibleFrame;
    CGFloat width = MIN(1120, available.size.width - 64), height = MIN(760, available.size.height - 80);
    self.previewWindow = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, width, height)
        styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable backing:NSBackingStoreBuffered defer:NO];
    self.previewWindow.title = @"皮肤预览"; self.previewWindow.delegate = self;
    self.previewWindow.minSize = NSMakeSize(640, 440);
    self.previewImage = [[NSImageView alloc] initWithFrame:NSMakeRect(20, 66, width - 40, height - 86)];
    self.previewImage.image = self.skins[self.skin].preview;
    self.previewImage.imageScaling = NSImageScaleProportionallyUpOrDown;
    self.previewImage.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    [self.previewWindow.contentView addSubview:self.previewImage];
    self.previewSelector = [NSSegmentedControl segmentedControlWithLabels:@[@"浅色", @"深色", @"Mac OS 9", @"Windows XP"]
        trackingMode:NSSegmentSwitchTrackingSelectOne target:self action:@selector(comparePreview:)];
    self.previewSelector.frame = NSMakeRect(20, 20, 440, 30); self.previewSelector.selectedSegment = self.skin;
    [self.previewWindow.contentView addSubview:self.previewSelector];
    NSButton *close = [NSButton buttonWithTitle:@"完成" target:self action:@selector(closePreview:)];
    close.frame = NSMakeRect(width - 110, 18, 90, 34); close.autoresizingMask = NSViewMinXMargin;
    close.keyEquivalent = @"\e"; [self.previewWindow.contentView addSubview:close];
    [self.window beginSheet:self.previewWindow completionHandler:^(NSModalResponse response) {
        (void)response; self.previewWindow = nil; self.previewImage = nil; self.previewSelector = nil;
    }];
}
- (void)comparePreview:(id)sender {
    (void)sender;
    NSInteger index = self.previewSelector.selectedSegment;
    if (index < 0 || index >= AM_SETUP_SKIN_COUNT) return;
    [self selectSkin:self.skins[index]]; self.previewImage.image = self.skins[index].preview;
}
- (void)closePreview:(id)sender {
    (void)sender;
    NSWindow *preview = self.previewWindow;
    [self.window endSheet:preview]; [preview orderOut:nil];
}
- (void)pickFolder:(id)sender {
    (void)sender;
    NSOpenPanel *panel = [NSOpenPanel openPanel]; panel.canChooseDirectories = YES; panel.canChooseFiles = NO;
    panel.canCreateDirectories = YES; panel.allowsMultipleSelection = NO; panel.prompt = @"选择";
    panel.directoryURL = [NSURL fileURLWithPath:self.output.stringValue];
    [panel beginSheetModalForWindow:self.window completionHandler:^(NSModalResponse response) {
        if (response == NSModalResponseOK) self.output.stringValue = panel.URL.path;
    }];
}
- (BOOL)saveChoices:(NSError **)error {
    NSString *output = self.output.stringValue;
    if (!output.isAbsolutePath) { *error = failure(@"请选择有效的输出目录。"); return NO; }
    NSFileManager *manager = NSFileManager.defaultManager;
    if (![manager createDirectoryAtPath:output withIntermediateDirectories:YES attributes:nil error:error]) return NO;
    NSString *folder = runtimeBase.stringByDeletingLastPathComponent;
    if (![manager createDirectoryAtPath:folder withIntermediateDirectories:YES attributes:nil error:error]) return NO;
    NSDictionary *choices = @{@"Theme":[NSString stringWithUTF8String:am_setup_skins[self.skin].key],
        @"Language":@[@"system", @"zh-CN", @"en-US"][self.language.indexOfSelectedItem], @"OutputFolder":output,
        @"OutputToSource":@(self.source.state == NSControlStateValueOn), @"NotifyComplete":@(self.notify.state == NSControlStateValueOn),
        @"ReduceMotion":@(self.motion.state == NSControlStateValueOn), @"CheckForUpdates":@(self.updates.state == NSControlStateValueOn),
        @"AutoDetectGpu":@(self.gpu.state == NSControlStateValueOn)};
    NSData *data = [NSJSONSerialization dataWithJSONObject:choices options:0 error:error];
    if (!data || ![data writeToFile:[folder stringByAppendingPathComponent:@"setup-pending.json"] options:NSDataWritingAtomic error:error]) return NO;
    return [[NSData data] writeToFile:[folder stringByAppendingPathComponent:@"setup-complete"] options:NSDataWritingAtomic error:error];
}
- (void)next:(id)sender {
    (void)sender;
    if (self.busy || (self.step == 0 && !self.runtime)) return;
    if (self.step < 2) { self.step++; [self refresh]; return; }
    NSError *error = nil;
    if ([self saveChoices:&error]) [NSApp stopModalWithCode:NSModalResponseOK];
    else {
        NSAlert *alert = [NSAlert new]; alert.messageText = @"无法保存配置";
        alert.informativeText = error.localizedDescription ?: @"请检查输出目录和用户目录的写入权限。";
        [alert beginSheetModalForWindow:self.window completionHandler:nil];
    }
}
- (void)update:(NSString *)message {
    dispatch_async(dispatch_get_main_queue(), ^{ self.status.stringValue = message; });
}
- (BOOL)download:(NSURL *)url to:(NSString *)path error:(NSError **)error {
    NSURLSessionConfiguration *configuration = NSURLSessionConfiguration.ephemeralSessionConfiguration;
    configuration.timeoutIntervalForRequest = 60; configuration.timeoutIntervalForResource = 900;
    NSURLSession *session = [NSURLSession sessionWithConfiguration:configuration];
    dispatch_semaphore_t ready = dispatch_semaphore_create(0);
    __block NSError *downloadError = nil;
    NSURLSessionDownloadTask *task = [session downloadTaskWithURL:url completionHandler:^(NSURL *temporary, NSURLResponse *response, NSError *problem) {
        downloadError = problem;
        if (!downloadError && (![response isKindOfClass:NSHTTPURLResponse.class] || ((NSHTTPURLResponse *)response).statusCode != 200))
            downloadError = failure(@"运行时下载失败，请检查网络后重试。");
        if (!downloadError) [[NSFileManager defaultManager] moveItemAtURL:temporary toURL:[NSURL fileURLWithPath:path] error:&downloadError];
        dispatch_semaphore_signal(ready);
    }];
    [task resume]; dispatch_semaphore_wait(ready, DISPATCH_TIME_FOREVER); [session finishTasksAndInvalidate];
    if (error) *error = downloadError;
    return downloadError == nil;
}
- (BOOL)verify:(NSString *)path hash:(NSString *)hash error:(NSError **)error {
    NSInputStream *stream = [NSInputStream inputStreamWithFileAtPath:path]; [stream open];
    CC_SHA512_CTX context; CC_SHA512_Init(&context);
    uint8_t buffer[65536]; NSInteger count;
    while ((count = [stream read:buffer maxLength:sizeof(buffer)]) > 0) CC_SHA512_Update(&context, buffer, (CC_LONG)count);
    NSError *readError = stream.streamError; [stream close];
    if (count < 0 || readError) { if (error) *error = readError ?: failure(@"无法读取下载文件。"); return NO; }
    unsigned char digest[CC_SHA512_DIGEST_LENGTH]; CC_SHA512_Final(digest, &context);
    NSMutableString *actual = [NSMutableString string];
    for (int i = 0; i < CC_SHA512_DIGEST_LENGTH; i++) [actual appendFormat:@"%02x", digest[i]];
    if (![actual isEqualToString:hash.lowercaseString]) { if (error) *error = failure(@"运行时校验失败，请重试下载。"); return NO; }
    return YES;
}
- (NSString *)installRuntime:(NSError **)error {
    NSFileManager *manager = NSFileManager.defaultManager;
    if (![manager createDirectoryAtPath:runtimeBase withIntermediateDirectories:YES attributes:nil error:error]) return nil;
    NSString *lockPath = [runtimeBase stringByAppendingPathComponent:@".install.lock"];
    int lock = open(lockPath.fileSystemRepresentation, O_CREAT | O_RDWR, 0600);
    if (lock < 0) { *error = failure(@"无法创建运行时安装目录。"); return nil; }
    NSString *stage = nil, *result = nil;
    @try {
        NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:900];
        while (flock(lock, LOCK_EX | LOCK_NB) != 0) {
            if (deadline.timeIntervalSinceNow <= 0) { *error = failure(@"另一窗口仍在安装运行时，请稍后重试。"); return nil; }
            [self update:@"正在等待另一窗口完成安装…"]; usleep(200000);
        }
        // Another first-launch window may have completed the installation while we waited.
        result = recordedRuntime(); if (result) return result;
        result = findRuntime();
        if (result) return recordRuntime(result, error) ? result : nil;
        NSData *data = [NSData dataWithContentsOfFile:[applicationBase stringByAppendingPathComponent:@"runtime-bootstrap.json"] options:0 error:error];
        if (!data) return nil;
        NSDictionary *manifest = [NSJSONSerialization JSONObjectWithData:data options:0 error:error];
        if (![manifest isKindOfClass:NSDictionary.class] || ![manifest[@"rid"] isEqual:@"osx-arm64"] ||
            ![manifest[@"version"] isKindOfClass:NSString.class] || ![manifest[@"version"] hasPrefix:@"8.0."] ||
            ![manifest[@"packages"] isKindOfClass:NSArray.class] || [manifest[@"packages"] count] != 2) {
            *error = failure(@"运行时安装信息无效。"); return nil;
        }
        stage = [runtimeBase stringByAppendingPathComponent:[@".stage-" stringByAppendingString:NSUUID.UUID.UUIDString]];
        NSString *content = [stage stringByAppendingPathComponent:@"runtime"];
        if (![manager createDirectoryAtPath:content withIntermediateDirectories:YES attributes:nil error:error]) return nil;
        for (NSDictionary *package in manifest[@"packages"]) {
            NSURL *url = [package[@"url"] isKindOfClass:NSString.class] ? [NSURL URLWithString:package[@"url"]] : nil;
            NSString *hash = package[@"sha512"];
            if (![url.scheme isEqualToString:@"https"] || ![url.host isEqualToString:@"builds.dotnet.microsoft.com"] ||
                ![hash isKindOfClass:NSString.class] || hash.length != 128) { *error = failure(@"运行时下载信息无效。"); return nil; }
            NSString *archive = [stage stringByAppendingPathComponent:[NSUUID.UUID.UUIDString stringByAppendingString:@".tar.gz"]];
            [self update:[NSString stringWithFormat:@"正在下载 %@…", package[@"name"]]];
            if (![self download:url to:archive error:error]) return nil;
            [self update:@"正在校验并安装…"];
            if (![self verify:archive hash:hash error:error]) return nil;
            NSTask *extract = [NSTask new]; extract.executableURL = [NSURL fileURLWithPath:@"/usr/bin/tar"];
            extract.arguments = @[@"-xzf", archive, @"-C", content];
            extract.standardOutput = [NSFileHandle fileHandleWithNullDevice]; extract.standardError = [NSFileHandle fileHandleWithNullDevice];
            if (![extract launchAndReturnError:error]) return nil;
            [extract waitUntilExit];
            if (extract.terminationStatus != 0) { *error = failure(@"运行时解压失败，请重试。"); return nil; }
        }
        if (!am_runtime_usable(content.fileSystemRepresentation, runtimeConfig.fileSystemRepresentation)) {
            *error = failure(@"下载的运行时无法启动此版本软件。"); return nil;
        }
        NSString *name = [NSString stringWithFormat:@"%@-%@-%@", manifest[@"version"], manifest[@"rid"], NSUUID.UUID.UUIDString];
        result = [runtimeBase stringByAppendingPathComponent:name];
        if (![manager moveItemAtPath:content toPath:result error:error]) return nil;
        if (!recordRuntime(result, error)) return nil;
    } @finally {
        if (stage) [manager removeItemAtPath:stage error:nil];
        flock(lock, LOCK_UN); close(lock);
    }
    return result;
}
- (void)install:(id)sender {
    (void)sender;
    if (self.busy || self.runtime) return;
    self.busy = YES; [self refresh]; [self.progress startAnimation:nil]; self.status.stringValue = @"正在准备安装…";
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        NSError *error = nil;
        NSString *runtime = [self installRuntime:&error];
        dispatch_async(dispatch_get_main_queue(), ^{
            self.busy = NO; [self.progress stopAnimation:nil];
            if (runtime) {
                self.runtime = runtime; self.runtimePath.stringValue = runtime;
                self.status.stringValue = @"✓ .NET 8 和 ASP.NET Core 8 已就绪"; self.button.title = @"已安装";
            } else {
                self.status.stringValue = error.localizedDescription ?: @"安装失败，请重试。";
                self.button.title = @"重试安装";
            }
            [self refresh];
        });
    });
}

@end

int main(int argc, const char **argv) {
    @autoreleasepool {
        char executable[AM_PATH]; uint32_t size = sizeof(executable);
        if (_NSGetExecutablePath(executable, &size) != 0) return 1;
        applicationPath = [[NSString stringWithUTF8String:executable] stringByStandardizingPath];
        applicationBase = applicationPath.stringByDeletingLastPathComponent;
        runtimeConfig = [applicationBase stringByAppendingPathComponent:@"AvaMedia.Desktop.runtimeconfig.json"];
        runtimeBase = [NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/AvaMedia/runtimes"];
        if (argc == 3 && strcmp(argv[1], "--bootstrap-check-root") == 0)
            return am_runtime_usable(argv[2], runtimeConfig.fileSystemRepresentation) ? 0 : 1;
        // Only an unconfigured installation discovers/probes runtimes. Later launches load the saved root.
        NSString *runtime = recordedRuntime();
        if (!runtime) {
            runtime = findRuntime();
            if (runtime && !recordRuntime(runtime, nil)) return 1;
        }
        if (argc == 2 && strcmp(argv[1], "--bootstrap-check") == 0) return runtime ? 0 : 1;
        NSMutableArray<NSString *> *arguments = [NSMutableArray arrayWithObject:[applicationBase stringByAppendingPathComponent:@"AvaMedia.Desktop.dll"]];
        BOOL forceSetup = NO, capture = NO;
        for (int i = 1; i < argc; i++) {
            if (strcmp(argv[i], "--setup") == 0) forceSetup = YES;
            else { [arguments addObject:[NSString stringWithUTF8String:argv[i]]]; if (strcmp(argv[i], "--capture") == 0) capture = YES; }
        }
        NSString *complete = [runtimeBase.stringByDeletingLastPathComponent stringByAppendingPathComponent:@"setup-complete"];
        if (!runtime || forceSetup || (!capture && ![NSFileManager.defaultManager fileExistsAtPath:complete])) {
            [NSApplication sharedApplication]; [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
            RuntimeInstaller *installer = [RuntimeInstaller new]; installer.runtime = runtime; NSApp.delegate = installer;
            [NSApp finishLaunching]; [installer show];
            NSModalResponse response = [NSApp runModalForWindow:installer.window];
            [installer.window orderOut:nil]; NSApp.delegate = nil;
            if (response != NSModalResponseOK || !installer.runtime) return 0;
            [arguments addObjectsFromArray:installer.files];
            // Start with fresh Cocoa state so Avalonia can create its own NSApplication subclass.
            // exec preserves the process identity used by Finder and LaunchServices.
            char **relaunch = calloc(arguments.count + 1, sizeof(char *));
            if (!relaunch) return 1;
            relaunch[0] = (char *)applicationPath.fileSystemRepresentation;
            for (NSUInteger i = 1; i < arguments.count; i++) relaunch[i] = (char *)arguments[i].fileSystemRepresentation;
            execv(relaunch[0], relaunch);
            free(relaunch); return 1;
        }
        const char **managed = calloc(arguments.count, sizeof(char *));
        if (!managed) return 1;
        for (NSUInteger i = 0; i < arguments.count; i++) managed[i] = arguments[i].fileSystemRepresentation;
        int result = am_run_application(runtime.fileSystemRepresentation, applicationPath.fileSystemRepresentation, (int)arguments.count, managed);
        free(managed); return result;
    }
}
