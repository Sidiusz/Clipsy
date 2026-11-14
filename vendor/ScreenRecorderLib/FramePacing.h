#pragma once

namespace FramePacing {
    inline long long TimeUntilNextFrame(long long now, long long lastFrame, long long duration) {
        if (duration <= 0) return 0;
        const long long nextFrame = (lastFrame / duration + 1) * duration;
        return nextFrame > now ? nextFrame - now : 0;
    }
}
