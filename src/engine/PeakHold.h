#pragma once

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstdint>
#include <limits>

namespace elka
{
// Session-only metering. Watch reference counts are serialized by the control-side owner.
class PeakHold
{
public:
    static constexpr int Streams = 3;
    static constexpr int Channels = 128;
    static_assert(std::atomic<float>::is_always_lock_free);
    static_assert(std::atomic<uint32_t>::is_always_lock_free);
    static_assert(std::atomic<uint64_t>::is_always_lock_free);

    static bool valid(int stream, int first, int count) noexcept
    {
        return stream >= 0 && stream < Streams && first >= 0 && first < Channels &&
            count > 0 && count <= Channels - first;
    }

    bool watch(int stream, bool output, int first, int count, bool on) noexcept
    {
        if (!valid(stream, first, count)) return false;
        auto& side = sides_[stream][output ? 1 : 0];
        for (int c = first; c < first + count; ++c)
        {
            auto& refs = side.references[c];
            if (on && refs != std::numeric_limits<uint32_t>::max()) ++refs;
            else if (!on && refs != 0) --refs;
        }
        for (int word = 0; word < 2; ++word)
        {
            uint64_t mask = 0;
            for (int bit = 0; bit < 64; ++bit)
                if (side.references[word * 64 + bit] != 0) mask |= uint64_t{1} << bit;
            side.mask[word].store(mask, std::memory_order_relaxed);
        }
        return true;
    }

    void capture(int stream, bool output, float* const* channels, int count, int frames) noexcept
    {
        if (stream < 0 || stream >= Streams || !channels || count <= 0 || frames <= 0) return;
        auto& side = sides_[stream][output ? 1 : 0];
        count = std::min(count, Channels);
        for (int word = 0; word < 2 && word * 64 < count; ++word)
        {
            auto mask = side.mask[word].load(std::memory_order_relaxed);
            while (mask != 0)
            {
                const int c = word * 64 + std::countr_zero(mask);
                mask &= mask - 1;
                if (c >= count || !channels[c]) continue;
                float maximum = 0;
                for (int sample = 0; sample < frames; ++sample)
                {
                    const float value = channels[c][sample];
                    if (std::isfinite(value)) maximum = std::max(maximum, std::abs(value));
                }
                auto& held = side.maximum[c];
                float previous = held.load(std::memory_order_relaxed);
                while (maximum > previous && !held.compare_exchange_weak(previous, maximum,
                    std::memory_order_relaxed, std::memory_order_relaxed)) {}
                if (maximum >= 1.0f) side.overs[c].fetch_add(1, std::memory_order_relaxed);
            }
        }
    }

    bool read(int stream, bool output, int first, int count, float* maximum, uint32_t* overs) const noexcept
    {
        if (!valid(stream, first, count) || !maximum || !overs) return false;
        const auto& side = sides_[stream][output ? 1 : 0];
        for (int i = 0; i < count; ++i)
        {
            maximum[i] = side.maximum[first + i].load(std::memory_order_relaxed);
            overs[i] = side.overs[first + i].load(std::memory_order_relaxed);
        }
        return true;
    }

    bool reset(int stream, bool output, int first, int count) noexcept
    {
        if (!valid(stream, first, count)) return false;
        auto& side = sides_[stream][output ? 1 : 0];
        for (int c = first; c < first + count; ++c)
        {
            side.maximum[c].store(0, std::memory_order_relaxed);
            side.overs[c].store(0, std::memory_order_relaxed);
        }
        return true;
    }

private:
    struct Side
    {
        std::array<std::atomic<float>, Channels> maximum {};
        std::array<std::atomic<uint32_t>, Channels> overs {};
        std::array<std::atomic<uint64_t>, 2> mask {};
        std::array<uint32_t, Channels> references {};
    };
    std::array<std::array<Side, 2>, Streams> sides_ {};
};
}
