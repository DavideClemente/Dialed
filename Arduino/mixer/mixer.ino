#include "knobs.h"
#include "display.h"

static const unsigned long IDLE_TIMEOUT_MS = 3000;
static unsigned long lastKnobActivity = 0;
static bool isIdle = true;

void onKnobChange(const char* id, float value) {
  displayShowKnob(id, value);
  lastKnobActivity = millis();
  isIdle = false;
}

void setup() {
  displaySetup();
  knobsSetup(onKnobChange);
  lastKnobActivity = millis();
}

void loop() {
  knobsLoop();

  if (!isIdle && millis() - lastKnobActivity > IDLE_TIMEOUT_MS) {
    displayIdle();
    isIdle = true;
  }
}
