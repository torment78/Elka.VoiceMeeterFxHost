#pragma once

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstdint>
#include <memory>
#include <thread>

namespace elka
{
// Display-only taps. Audio never waits for the reader, allocates, or runs an FFT.
class SignalMonitorHub
{
public:
    static constexpr int Channels = 8;
    static constexpr int Samples = 4096;
    static constexpr int Capacity = 32;
    struct Snapshot
    {
        int sampleRate = 0;
        int channelCount = 0;
        int validSamples = 0;
        uint64_t sequence = 0;
        std::array<float, Channels> peaks{};
        std::array<std::array<float, Samples>, Channels> audio{};
    };

    // Open/read/close are serialized by the control API, never called by audio.
    int open(int stream, bool output, int first, int count)
    {
        if (stream < 0 || stream > 2 || first < 0 || first >= 64 || count < 1 || count > Channels || first + count > 64)
            return 0;
        for (int i = 0; i < Capacity; ++i)
        {
            auto& slot = slots[static_cast<size_t>(i)];
            if (slot.session) continue;
            auto session = std::make_unique<Session>();
            session->stream = stream;
            session->output = output;
            session->first = first;
            session->data.channelCount = count;
            session->id = nextId++;
            if (nextId == 0x7fffffff) nextId = 1;
            const int id = session->id;
            acquire(slot);
            slot.session = std::move(session);
            slot.busy.clear(std::memory_order_release);
            active.fetch_or(uint32_t{1} << i, std::memory_order_release);
            return id;
        }
        return 0;
    }

    void close(int id) noexcept
    {
        for (int i = 0; i < Capacity; ++i)
        {
            auto& slot = slots[static_cast<size_t>(i)];
            if (!slot.session || slot.session->id != id) continue;
            active.fetch_and(~(uint32_t{1} << i), std::memory_order_release);
            acquire(slot);
            slot.session.reset();
            slot.busy.clear(std::memory_order_release);
            return;
        }
    }

    bool read(int id, Snapshot& result) noexcept
    {
        for (auto& slot : slots)
        {
            if (!slot.session || slot.session->id != id) continue;
            acquire(slot);
            auto& session = *slot.session;
            const auto& data = session.data;
            result.sampleRate = data.sampleRate;
            result.channelCount = data.channelCount;
            result.validSamples = data.validSamples;
            result.sequence = data.sequence;
            result.peaks = data.peaks;
            session.data.peaks.fill(0);
            for (int c = 0; c < data.channelCount; ++c)
            {
                const auto& source = data.audio[static_cast<size_t>(c)];
                auto& destination = result.audio[static_cast<size_t>(c)];
                const int start = (session.position + Samples - data.validSamples) % Samples;
                const int firstPart = std::min(data.validSamples, Samples - start);
                std::copy_n(source.data() + start, firstPart, destination.data());
                std::copy_n(source.data(), data.validSamples - firstPart, destination.data() + firstPart);
                std::fill(destination.begin() + data.validSamples, destination.end(), 0.0f);
            }
            slot.busy.clear(std::memory_order_release);
            return true;
        }
        return false;
    }

    void capture(int stream, bool output, float* const* channels, int channelCount, int frames, int rate) noexcept
    {
        uint32_t mask = active.load(std::memory_order_acquire);
        if (!mask || frames <= 0 || rate <= 0) return;
        while (mask)
        {
            const int index = std::countr_zero(mask);
            mask &= mask - 1;
            auto& slot = slots[static_cast<size_t>(index)];
            if (slot.busy.test_and_set(std::memory_order_acquire)) continue;
            auto* session = slot.session.get();
            if (session && session->stream == stream && session->output == output)
            {
                auto& data = session->data;
                if (data.sampleRate != rate)
                {
                    data.sampleRate = rate;
                    data.validSamples = 0;
                    data.peaks.fill(0);
                    session->position = 0;
                }
                for (int c = 0; c < data.channelCount; ++c)
                {
                    const int sourceIndex = session->first + c;
                    const float* source = channels && sourceIndex < channelCount ? channels[sourceIndex] : nullptr;
                    float peak = data.peaks[static_cast<size_t>(c)];
                    auto& ring = data.audio[static_cast<size_t>(c)];
                    for (int n = 0; n < frames; ++n)
                    {
                        const float value = source && std::isfinite(source[n]) ? source[n] : 0.0f;
                        peak = std::max(peak, std::abs(value));
                        ring[static_cast<size_t>((session->position + n) % Samples)] = value;
                    }
                    data.peaks[static_cast<size_t>(c)] = peak;
                }
                session->position = (session->position + frames) % Samples;
                data.validSamples = std::min(Samples, data.validSamples + frames);
                ++data.sequence;
            }
            slot.busy.clear(std::memory_order_release);
        }
    }

    bool hasMonitors() const noexcept { return active.load(std::memory_order_relaxed) != 0; }

private:
    struct Session
    {
        int id = 0, stream = 0, first = 0, position = 0;
        bool output = false;
        Snapshot data;
    };
    struct Slot
    {
        std::atomic_flag busy = ATOMIC_FLAG_INIT;
        std::unique_ptr<Session> session;
    };
    static void acquire(Slot& slot) noexcept
    {
        while (slot.busy.test_and_set(std::memory_order_acquire)) std::this_thread::yield();
    }
    std::array<Slot, Capacity> slots;
    std::atomic<uint32_t> active{0};
    int nextId = 1;
};
}
