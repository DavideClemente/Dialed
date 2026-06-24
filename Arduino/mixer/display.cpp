#include "display.h"
#include "assignments.h"
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
  tft.setTextColor(0x8410, TFT_BLACK);
  tft.setTextSize(1);
  tft.drawString("AudioMixer", CX, CY);
}

void displayShowKnob(int knobIndex, float value) {
  float v = constrain(value, 0.0f, 1.0f);

  tft.fillScreen(TFT_BLACK);
  tft.drawCircle(CX, CY, 115, TFT_WHITE);

  tft.setTextDatum(MC_DATUM);

  if (knobIndex >= 0 && knobIndex < MAX_KNOBS && knobHasIcon[knobIndex]) {
    // Icon: 64x64 centred at (120, 72) — top-left = (88, 40)
    tft.pushImage(88, 40, ICON_W, ICON_H, knobIcon[knobIndex]);

    // Label below icon
    tft.setTextColor(TFT_WHITE, TFT_BLACK);
    tft.setTextSize(2);
    tft.drawString(knobLabel[knobIndex], CX, 122);
  } else {
    // No icon — label higher up
    const char* label = (knobIndex >= 0 && knobIndex < MAX_KNOBS && knobLabel[knobIndex][0])
                        ? knobLabel[knobIndex] : "---";
    tft.setTextColor(TFT_WHITE, TFT_BLACK);
    tft.setTextSize(2);
    tft.drawString(label, CX, 100);
  }

  // Volume percentage
  char pct[8];
  snprintf(pct, sizeof(pct), "%d%%", (int)(v * 100));
  tft.setTextSize(3);
  tft.setTextColor(TFT_CYAN, TFT_BLACK);
  tft.drawString(pct, CX, 158);

  // Volume bar — 160x8 centred horizontally at y=190
  const int BAR_W = 160, BAR_H = 8;
  int bx = CX - BAR_W / 2;
  tft.drawRect(bx, 190, BAR_W, BAR_H, TFT_WHITE);
  tft.fillRect(bx + 1, 191, (int)((BAR_W - 2) * v), BAR_H - 2, TFT_CYAN);
}
