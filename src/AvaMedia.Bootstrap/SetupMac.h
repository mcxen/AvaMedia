#include "Setup.h"

@interface SetupSkinView : NSButton
@property NSInteger skinIndex;
@property BOOL chosen;
@property BOOL hovered;
@property BOOL pressed;
@property NSImage *preview;
@property NSTrackingArea *hoverArea;
@property SEL previewAction;
@end
@implementation SetupSkinView
- (BOOL)isFlipped { return YES; }
- (void)updateTrackingAreas {
    [super updateTrackingAreas];
    if (self.hoverArea) [self removeTrackingArea:self.hoverArea];
    self.hoverArea = [[NSTrackingArea alloc] initWithRect:NSZeroRect
        options:NSTrackingMouseEnteredAndExited | NSTrackingActiveInKeyWindow | NSTrackingInVisibleRect owner:self userInfo:nil];
    [self addTrackingArea:self.hoverArea];
}
- (void)mouseEntered:(NSEvent *)event { (void)event; self.hovered = YES; self.needsDisplay = YES; }
- (void)mouseExited:(NSEvent *)event { (void)event; self.hovered = NO; self.needsDisplay = YES; }
- (void)mouseDown:(NSEvent *)event {
    self.pressed = YES; self.needsDisplay = YES;
    [super mouseDown:event];
    self.pressed = NO; self.needsDisplay = YES;
    if (event.clickCount == 2 && self.previewAction) [NSApp sendAction:self.previewAction to:self.target from:self];
}
- (void)drawRect:(NSRect)dirty {
    (void)dirty;
    NSRect bounds = NSInsetRect(self.bounds, 2, 2);
    BOOL focused = self.window.firstResponder == self;
    NSBezierPath *frame = [NSBezierPath bezierPathWithRoundedRect:bounds xRadius:8 yRadius:8];
    [(self.pressed ? [NSColor colorWithWhite:0.97 alpha:1] : NSColor.whiteColor) setFill]; [frame fill];
    [(self.chosen || focused ? NSColor.controlAccentColor : [NSColor colorWithWhite:self.hovered ? 0.65 : 0.84 alpha:1]) setStroke];
    frame.lineWidth = self.chosen || focused ? 2 : 1; [frame stroke];
    NSRect artwork = NSMakeRect(12, 12, self.bounds.size.width - 24, self.bounds.size.height - 52);
    if (self.preview) {
        NSSize size = self.preview.size;
        CGFloat scale = MIN(artwork.size.width / size.width, artwork.size.height / size.height);
        NSRect destination = NSMakeRect(artwork.origin.x + (artwork.size.width - size.width * scale) / 2,
            artwork.origin.y + (artwork.size.height - size.height * scale) / 2, size.width * scale, size.height * scale);
        NSGraphicsContext.currentContext.imageInterpolation = NSImageInterpolationHigh;
        [self.preview drawInRect:destination fromRect:NSZeroRect operation:NSCompositingOperationSourceOver fraction:1 respectFlipped:YES hints:nil];
    } else {
        [@"预览不可用" drawInRect:artwork withAttributes:@{NSFontAttributeName:[NSFont systemFontOfSize:13], NSForegroundColorAttributeName:NSColor.secondaryLabelColor}];
    }
    NSArray *names = @[@"浅色", @"深色", @"Mac OS 9 · Platinum", @"Windows XP · Luna"];
    [names[self.skinIndex] drawInRect:NSMakeRect(14, self.bounds.size.height - 32, self.bounds.size.width - 48, 22)
        withAttributes:@{NSFontAttributeName:[NSFont systemFontOfSize:13 weight:NSFontWeightSemibold], NSForegroundColorAttributeName:NSColor.blackColor}];
    if (self.chosen) {
        NSRect badge = NSMakeRect(self.bounds.size.width - 31, self.bounds.size.height - 32, 17, 17);
        [NSColor.controlAccentColor setFill]; [[NSBezierPath bezierPathWithOvalInRect:badge] fill];
        [@"✓" drawInRect:NSInsetRect(badge, 3, 0) withAttributes:@{NSFontAttributeName:[NSFont boldSystemFontOfSize:12], NSForegroundColorAttributeName:NSColor.whiteColor}];
    }
}
@end
