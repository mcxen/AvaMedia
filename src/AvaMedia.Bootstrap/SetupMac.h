#include "Setup.h"

static NSColor *setupColor(unsigned int hex) {
    return [NSColor colorWithSRGBRed:((hex >> 16) & 255) / 255.0 green:((hex >> 8) & 255) / 255.0 blue:(hex & 255) / 255.0 alpha:1];
}
static void setupFill(NSRect rect, unsigned int hex) { [setupColor(hex) setFill]; NSRectFill(rect); }
static void setupText(NSString *text, NSRect rect, unsigned int hex, CGFloat size, BOOL centered) {
    NSMutableParagraphStyle *paragraph = [NSMutableParagraphStyle new];
    paragraph.alignment = centered ? NSTextAlignmentCenter : NSTextAlignmentLeft;
    [text drawInRect:rect withAttributes:@{NSFontAttributeName:[NSFont systemFontOfSize:size],
        NSForegroundColorAttributeName:setupColor(hex), NSParagraphStyleAttributeName:paragraph}];
}

@interface SetupSkinView : NSButton
@property NSInteger skinIndex;
@property BOOL chosen;
@end
@implementation SetupSkinView
- (BOOL)isFlipped { return YES; }
- (void)drawRect:(NSRect)dirty {
    (void)dirty;
    const am_setup_skin *skin = &am_setup_skins[self.skinIndex];
    NSRect bounds = NSInsetRect(self.bounds, 2, 2);
    [NSColor.whiteColor setFill];
    NSBezierPath *frame = [NSBezierPath bezierPathWithRoundedRect:bounds xRadius:7 yRadius:7]; [frame fill];
    [(self.chosen ? NSColor.controlAccentColor : NSColor.separatorColor) setStroke]; frame.lineWidth = self.chosen ? 2 : 1; [frame stroke];
    CGFloat width = self.bounds.size.width - 24, x = 12, y = 12, height = self.bounds.size.height - 54;
    setupFill(NSMakeRect(x, y, width, height), skin->canvas);
    NSRect title = NSMakeRect(x, y, width, 23);
    setupFill(title, skin->title);
    if (self.skinIndex == 3) {
        NSGradient *gradient = [[NSGradient alloc] initWithStartingColor:setupColor(0x3593FF) endingColor:setupColor(0x003DAA)];
        [gradient drawInRect:title angle:90];
        setupFill(NSMakeRect(x + width - 21, y + 4, 15, 15), 0xDB4B33);
        setupText(@"×", NSMakeRect(x + width - 21, y + 1, 15, 19), 0xFFFFFF, 14, YES);
    } else if (self.skinIndex == 2) {
        for (int row = 4; row < 21; row += 3) setupFill(NSMakeRect(x + 4, y + row, width - 8, 1), 0x999999);
        setupFill(NSMakeRect(x + 51, y + 2, width - 102, 19), skin->title);
        setupFill(NSMakeRect(x + 7, y + 5, 12, 12), 0xFFFFFF);
    }
    setupText(@"天池万象转换", NSMakeRect(x + 22, y + 4, width - 44, 17), self.skinIndex == 3 ? 0xFFFFFF : skin->text, 11, YES);
    CGFloat top = y + 27, left = x + 65;
    setupFill(NSMakeRect(x, top, 58, height - 27), skin->sidebar);
    setupText(@"转换", NSMakeRect(x + 8, top + 7, 48, 20), skin->accent, 11, NO);
    setupText(@"工具集", NSMakeRect(x + 8, top + 32, 48, 20), skin->text, 11, NO);
    for (int row = 0; row < 2; row++) {
        CGFloat rowY = top + row * 33;
        setupFill(NSMakeRect(left, rowY, width - 70, 28), skin->surface);
        setupFill(NSMakeRect(left + 5, rowY + 6, 16, 16), skin->accent);
        setupText(row ? @"音频.wav" : @"视频.mp4", NSMakeRect(left + 27, rowY + 6, width - 100, 20), skin->text, 11, NO);
    }
    setupFill(NSMakeRect(left, top + 71, width - 70, 4), skin->sidebar);
    setupFill(NSMakeRect(left, top + 71, (width - 70) * 0.65, 4), skin->accent);
    NSArray *names = @[@"浅色", @"深色", @"Mac OS 9", @"Windows XP"];
    setupText(self.chosen ? [@"✓ " stringByAppendingString:names[self.skinIndex]] : names[self.skinIndex],
        NSMakeRect(12, self.bounds.size.height - 32, width, 22), self.chosen ? 0x0078D4 : 0x202428, 13, YES);
}
@end
