#include "assignments.h"

char     knobLabel  [MAX_KNOBS][32]          = {};
uint16_t knobIcon   [MAX_KNOBS][ICON_PIXELS] = {};
bool     knobHasIcon[MAX_KNOBS]              = {};

static int b64Val(char c) {
  if (c >= 'A' && c <= 'Z') return c - 'A';
  if (c >= 'a' && c <= 'z') return c - 'a' + 26;
  if (c >= '0' && c <= '9') return c - '0' + 52;
  if (c == '+') return 62;
  if (c == '/') return 63;
  return -1;
}

static int base64Decode(const char* src, uint8_t* dst, int dstLen) {
  int out = 0;
  while (src[0] && src[1] && src[2] && src[3]) {
    int v0 = b64Val(src[0]), v1 = b64Val(src[1]);
    int v2 = b64Val(src[2]), v3 = b64Val(src[3]);
    if (v0 < 0 || v1 < 0) break;
    if (out < dstLen) dst[out++] = (uint8_t)((v0 << 2) | (v1 >> 4));
    if (src[2] != '=' && v2 >= 0 && out < dstLen)
      dst[out++] = (uint8_t)(((v1 & 0xF) << 4) | (v2 >> 2));
    if (src[3] != '=' && v3 >= 0 && out < dstLen)
      dst[out++] = (uint8_t)(((v2 & 0x3) << 6) | v3);
    src += 4;
  }
  return out;
}

// Parse "assign:knob1:AppName"
bool handleAssignLine(const char* line) {
  if (strncmp(line, "assign:", 7) != 0) return false;
  const char* rest = line + 7;                    // "knob1:AppName"
  const char* colon = strchr(rest, ':');
  if (!colon || strncmp(rest, "knob", 4) != 0) return false;

  int idx = atoi(rest + 4) - 1;                  // 1-based → 0-based
  if (idx < 0 || idx >= MAX_KNOBS) return false;

  strncpy(knobLabel[idx], colon + 1, 31);
  knobLabel[idx][31] = '\0';
  return true;
}

// Parse "icon:knob1:<base64>"
bool handleIconLine(const char* line) {
  if (strncmp(line, "icon:", 5) != 0) return false;
  const char* rest = line + 5;                    // "knob1:<base64>"
  const char* colon = strchr(rest, ':');
  if (!colon || strncmp(rest, "knob", 4) != 0) return false;

  int idx = atoi(rest + 4) - 1;
  if (idx < 0 || idx >= MAX_KNOBS) return false;

  int decoded = base64Decode(colon + 1, (uint8_t*)knobIcon[idx], ICON_PIXELS * 2);
  knobHasIcon[idx] = (decoded == ICON_PIXELS * 2);
  return true;
}
