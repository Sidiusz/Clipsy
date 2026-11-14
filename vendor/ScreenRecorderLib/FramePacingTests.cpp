#include "FramePacing.h"
#include <cstdio>

static bool KeepsCadence(int fps) {
    const long long duration = 10000000 / fps;
    long long now = 0, lastFrame = 0, copyTime = 0;
    for (int frame = 1; frame <= 600; ++frame) {
        copyTime = duration / 4 + (frame % 4 + 1) * 1000;
        now += FramePacing::TimeUntilNextFrame(now, lastFrame, duration);
        now += copyTime;
        lastFrame = now;
        now += 1000;
    }
    return lastFrame == 600 * duration + copyTime;
}

int main() {
    const int frameRates[] = { 30, 60, 120, 240 };
    for (int fps : frameRates) {
        if (!KeepsCadence(fps)) {
            std::printf("Capture and render overhead changed the %d fps cadence\n", fps);
            return 1;
        }
    }
    const long long duration = 10000000 / 60;
    if (FramePacing::TimeUntilNextFrame(10 * duration, duration, duration) != 0) return 2;
    const long long recovered = 10 * duration + 1000;
    const long long wait = FramePacing::TimeUntilNextFrame(recovered, recovered, duration);
    if (wait <= 0 || recovered + wait != 11 * duration) return 3;
    if (FramePacing::TimeUntilNextFrame(0, 0, 0) != 0) return 4;
    std::puts("Frame pacing tests passed");
    return 0;
}
