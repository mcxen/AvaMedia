#import <Cocoa/Cocoa.h>
#import <CommonCrypto/CommonDigest.h>
#include <mach-o/dyld.h>
#include <sys/file.h>
#include <unistd.h>
#include "Host.h"

static NSString *applicationPath, *applicationBase, *runtimeConfig, *runtimeBase;
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
    (void)sender;
    [NSApp stopModalWithCode:NSModalResponseCancel];
    return YES;
}
- (void)show {
    self.window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 440, 210)
        styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable backing:NSBackingStoreBuffered defer:NO];
    self.window.title = @"天池万象转换"; self.window.delegate = self;
    NSTextField *heading = [NSTextField labelWithString:@"需要安装 .NET 8 运行时"];
    heading.font = [NSFont boldSystemFontOfSize:18]; heading.frame = NSMakeRect(24, 154, 392, 26);
    [self.window.contentView addSubview:heading];
    self.status = [NSTextField wrappingLabelWithString:@"安装完成后自动进入软件，无需管理员权限。"];
    self.status.frame = NSMakeRect(24, 78, 392, 64); self.status.textColor = NSColor.secondaryLabelColor;
    [self.window.contentView addSubview:self.status];
    self.progress = [[NSProgressIndicator alloc] initWithFrame:NSMakeRect(24, 63, 392, 8)];
    self.progress.style = NSProgressIndicatorStyleBar; self.progress.indeterminate = YES; self.progress.hidden = YES;
    [self.window.contentView addSubview:self.progress];
    self.button = [NSButton buttonWithTitle:@"安装运行时" target:self action:@selector(install:)];
    self.button.frame = NSMakeRect(296, 20, 120, 32); self.button.keyEquivalent = @"\r";
    [self.window.contentView addSubview:self.button];
    [self.window center]; [self.window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES];
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
        result = findRuntime(); if (result) return result;
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
    } @finally {
        if (stage) [manager removeItemAtPath:stage error:nil];
        flock(lock, LOCK_UN); close(lock);
    }
    return result;
}
- (void)install:(id)sender {
    (void)sender;
    self.button.enabled = NO; [self.window standardWindowButton:NSWindowCloseButton].enabled = NO;
    self.progress.hidden = NO; [self.progress startAnimation:nil]; self.status.stringValue = @"正在准备安装…";
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        NSError *error = nil;
        NSString *runtime = [self installRuntime:&error];
        dispatch_async(dispatch_get_main_queue(), ^{
            if (runtime) { self.runtime = runtime; [NSApp stopModalWithCode:NSModalResponseOK]; }
            else {
                self.status.stringValue = error.localizedDescription ?: @"安装失败，请重试。";
                self.button.title = @"重试安装"; self.button.enabled = YES;
                [self.window standardWindowButton:NSWindowCloseButton].enabled = YES;
                [self.progress stopAnimation:nil]; self.progress.hidden = YES;
            }
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
        NSString *runtime = findRuntime();
        if (argc == 2 && strcmp(argv[1], "--bootstrap-check") == 0) return runtime ? 0 : 1;
        NSMutableArray<NSString *> *arguments = [NSMutableArray arrayWithObject:[applicationBase stringByAppendingPathComponent:@"AvaMedia.Desktop.dll"]];
        for (int i = 1; i < argc; i++) [arguments addObject:[NSString stringWithUTF8String:argv[i]]];
        if (!runtime) {
            [NSApplication sharedApplication]; [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
            RuntimeInstaller *installer = [RuntimeInstaller new]; NSApp.delegate = installer;
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
