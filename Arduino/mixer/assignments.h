#pragma once
#include <Arduino.h>

static const int MAX_KNOBS   = 4;
static const int ICON_W      = 64;
static const int ICON_H      = 64;
static const int ICON_PIXELS = ICON_W * ICON_H;

extern char     knobLabel  [MAX_KNOBS][32];
extern uint16_t knobIcon   [MAX_KNOBS][ICON_PIXELS];
extern bool     knobHasIcon[MAX_KNOBS];
extern uint16_t knobColor[MAX_KNOBS];   // accent color, RGB565

// ── Output switch positions ─────────────────────────────────────────────────
// Position A/B assignments pushed by the PC as "outset:<a|b>:<h|s>:<name>".
// RAM-only, like knobLabel: a device reset drops them until the PC resyncs.
static const int OUT_POSITIONS = 2;

static const uint8_t OUT_KIND_SPEAKER   = 0;
static const uint8_t OUT_KIND_HEADPHONE = 1;

extern char    outLabel[OUT_POSITIONS][32];
extern uint8_t outKind [OUT_POSITIONS];

bool handleAssignLine(const char* line);
bool handleIconLine  (const char* line);
bool handleOutSetLine(const char* line);
