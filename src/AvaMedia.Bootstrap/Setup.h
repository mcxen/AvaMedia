#ifndef AVAMEDIA_SETUP_H
#define AVAMEDIA_SETUP_H

// Preview colors follow the application theme resources. Both native launchers use this palette.
typedef struct {
    const char *key;
    unsigned int canvas, surface, text, accent, title, sidebar;
} am_setup_skin;

static const am_setup_skin am_setup_skins[] = {
    {"Light",     0xFAFAFA, 0xFFFFFF, 0x202428, 0x0078D4, 0xF4F4F4, 0xF4F4F4},
    {"Dark",      0x202020, 0x282828, 0xF3F3F3, 0x60CDFF, 0x303030, 0x303030},
    {"MacOS9",    0xCCCCCC, 0xFFFFFF, 0x111111, 0x3D4E80, 0xCCCCCC, 0xBBBBBB},
    {"WindowsXP", 0xECE9D8, 0xFFFFFF, 0x000000, 0x215DC6, 0x0058EE, 0xD6DFF7}
};
#define AM_SETUP_SKIN_COUNT 4

#endif
