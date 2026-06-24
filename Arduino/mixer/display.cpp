#include "display.h"
#include <TFT_eSPI.h>
#include <SPI.h>

static TFT_eSPI tft;

static const int CX = 120;
static const int CY = 120;

void displaySetup() {
  tft.init();
  tft.setRotation(0);
  displayIdle();
}

void displayIdle() {
  tft.fillScreen(TFT_BLACK);
  tft.drawCircle(CX, CY, 115, 0x4208);
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(0x8410, TFT_BLACK); // muted grey
  tft.setTextSize(1);
  tft.drawString("AudioMixer", CX, CY);
}

void displayShowKnob(const char* label, float value) {
  float v = value < 0.0f ? 0.0f : (value > 1.0f ? 1.0f : value);

  tft.fillScreen(TFT_BLACK);
  tft.drawCircle(CX, CY, 115, TFT_WHITE);

  // Label (app name — knob ID for now, replaced by app name later)
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(TFT_WHITE, TFT_BLACK);
  tft.setTextSize(2);
  tft.drawString(label, CX, CY - 25);

  // Volume percentage
  char pct[8];
  snprintf(pct, sizeof(pct), "%d%%", (int)(v * 100));
  tft.setTextSize(3);
  tft.setTextColor(TFT_CYAN, TFT_BLACK);
  tft.drawString(pct, CX, CY + 15);

  // Volume bar
  static const int BAR_W = 160, BAR_H = 8;
  int bx = CX - BAR_W / 2;
  int by = CY + 50;
  tft.drawRect(bx, by, BAR_W, BAR_H, TFT_WHITE);
  tft.fillRect(bx + 1, by + 1, (int)((BAR_W - 2) * v), BAR_H - 2, TFT_CYAN);
}
