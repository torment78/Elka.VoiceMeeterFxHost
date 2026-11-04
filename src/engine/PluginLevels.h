#pragma once

#include "plugins/RealtimePluginProcessor.h"
#include "plugins/PluginCapacity.h"
#include <atomic>
#include <bit>

namespace elka
{
// One widget reader drains interval maxima. No audio buffers are retained or modified.
class PluginLevels
{
public:
    static constexpr int Slots = HostedPluginCapacity;
    static constexpr int Pins = 32;
    static constexpr int Values = Slots * Pins * 2;
    static_assert(std::atomic<float>::is_always_lock_free);
    static_assert(std::atomic<uint64_t>::is_always_lock_free);

    uint64_t mask(int slot) const noexcept { return masks_[slot].load(std::memory_order_relaxed); }
    void reset(int slot) noexcept
    {
        for (auto& peak : peaks_[slot]) peak.store(0, std::memory_order_relaxed);
    }
    bool watches(const uint64_t* masks, int count) noexcept
    {
        if (!masks || count != Slots) return false;
        for (int s = 0; s < Slots; ++s)
        {
            if (masks_[s].exchange(masks[s], std::memory_order_relaxed) == masks[s]) continue;
            reset(s);
        }
        return true;
    }
    bool read(float* values, int count) noexcept
    {
        if (!values || count != Values) return false;
        for (int s = 0; s < Slots; ++s)
            for (int p = 0; p < Pins * 2; ++p)
                values[s * Pins * 2 + p] = peaks_[s][p].exchange(0, std::memory_order_relaxed);
        return true;
    }
    void publish(int slot, int offset, const PluginLevelCapture& capture) noexcept
    {
        auto mask = capture.mask;
        while (mask)
        {
            int pin = std::countr_zero(mask); mask &= mask - 1;
            auto& target = peaks_[slot][offset + pin];
            float previous = target.load(std::memory_order_relaxed);
            while (capture.peaks[pin] > previous && !target.compare_exchange_weak(previous, capture.peaks[pin],
                std::memory_order_relaxed, std::memory_order_relaxed)) {}
        }
    }

    static PluginLevelCapture input(uint32_t mask, const PluginAudioInputRoute* routes, int count, int frames) noexcept
    {
        PluginLevelCapture result { mask };
        while (mask)
        {
            int pin = std::countr_zero(mask); mask &= mask - 1;
            std::array<const float*, 64> sources {};
            int n = 0;
            for (int r = 0; r < count && n < 64; ++r)
                if (routes[r].pluginPin == pin && routes[r].source) sources[n++] = routes[r].source;
            if (n == 1) { result.capture(pin, sources[0], frames); continue; }
            if (!n) continue;
            for (int i = 0; i < frames; ++i)
            {
                float sum = 0;
                for (int r = 0; r < n; ++r)
                {
                    sum += sources[r][i];
                    if (!std::isfinite(sum)) sum = 0;
                }
                result.peaks[pin] = std::max(result.peaks[pin], std::abs(sum));
            }
        }
        return result;
    }
    static void bypass(PluginLevelCapture& output, const PluginAudioInputRoute* routes, int count, int frames) noexcept
    {
        auto mask = output.mask;
        while (mask)
        {
            int pin = std::countr_zero(mask); mask &= mask - 1;
            for (int r = 0; r < count; ++r)
                if (routes[r].pluginPin == pin && routes[r].source)
                { output.capture(pin, routes[r].source, frames); break; }
        }
    }
private:
    std::array<std::atomic<uint64_t>, Slots> masks_ {};
    std::array<std::array<std::atomic<float>, Pins * 2>, Slots> peaks_ {};
};
}
