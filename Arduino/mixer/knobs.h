#pragma once

// value: absolute 0.0–1.0 for pots, +1.0/-1.0 delta for encoders
typedef void (*KnobCallback)(const char* id, float value);

void knobsSetup(KnobCallback cb);
void knobsLoop();

// Fired when the output toggle actually moves. NOT fired for the boot-time
// sample that merely announces the toggle's resting position — see switchLoop.
typedef void (*SwitchCallback)(int pos);
void knobsSetSwitchCallback(SwitchCallback cb);

// How many knobs the PC reports are actually wired (via "cfg:knobs:<N>"). Knobs
// beyond this count are never sampled, so unconnected/floating pins can't emit
// phantom events. Clamped to the firmware's physical maximum.
void knobsSetActiveCount(int n);
