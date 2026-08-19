#pragma once
#include <Arduino.h>

void displaySetup();
void displayShowKnob(int knobIndex, float value);
void displayShowMute(int knobIndex, bool muted);
void displaySetShowPercent(bool show);
void displayEnterIdle();

// Output-switch card. pos: 0 = A, 1 = B.
// OUT_PENDING = drawn locally the moment the toggle moves, before the PC has
// confirmed. The other three are what an "out:" line reports.
enum OutState : uint8_t { OUT_PENDING = 0, OUT_OK = 1, OUT_NONE = 2, OUT_FAIL = 3 };
void displayShowOutput(int pos, uint8_t state);

void displayTick();

// GIF-upload progress screen (driven by idlegif.cpp while a new GIF flashes).
void displayUploadBegin();
void displayUploadProgress(float frac);   // 0..1
void displayUploadEnd(bool ok);
