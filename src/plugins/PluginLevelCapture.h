#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace elka
{
// Optional read-only observer of the private plugin output, before destination mixing.
struct PluginLevelCapture
{
    uint32_t mask = 0;
    std::array<float, 32> peaks {};

    void capture(int pin, const float* samples, int count) noexcept
    {
        if (pin < 0 || pin >= 32 || !(mask & (uint32_t{1} << pin)) || !samples) return;
        float peak = 0;
        for (int i = 0; i < count; ++i)
            if (std::isfinite(samples[i])) peak = std::max(peak, std::abs(samples[i]));
        peaks[pin] = peak;
    }
};
}
