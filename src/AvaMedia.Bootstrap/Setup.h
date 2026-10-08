#ifndef AVAMEDIA_SETUP_H
#define AVAMEDIA_SETUP_H

// Artwork is exported from the actual client during packaging; never duplicate skin styling here.
typedef struct { const char *key; } am_setup_skin;
static const am_setup_skin am_setup_skins[] = {{"Light"}, {"Dark"}, {"MacOS9"}, {"WindowsXP"}};
#define AM_SETUP_SKIN_COUNT 4

#endif
