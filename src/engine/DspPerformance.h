#pragma once

#include <Windows.h>
#include <array>
#include <atomic>
#include <algorithm>
#include <cstdint>

namespace elka
{
// Fixed, blittable records shared with the managed diagnostics reader. Slot -1 is
// the complete callback; node time includes its host wrapper and any worker wait.
struct DspTimingSnapshot
{
    int stream = 0, slot = -1, sampleRate = 0, blockSize = 0;
    uint64_t token = 0, calls = 0, elapsedTicks = 0;
    double budgetTicks = 0;
    uint64_t pluginTicks = 0, peakTicks = 0, overruns = 0, lastTick = 0;
    int flags = 0, reserved = 0;
};

struct DspOverrunEvent
{
    int stream = 0, slot = -1, sampleRate = 0, blockSize = 0;
    uint64_t token = 0, timestamp = 0, elapsedTicks = 0;
    double budgetTicks = 0;
    int flags = 0, reserved = 0;
};

static_assert(sizeof(DspTimingSnapshot) == 88);
static_assert(sizeof(DspOverrunEvent) == 56);
static_assert(std::atomic<uint64_t>::is_always_lock_free);
static_assert(std::atomic<double>::is_always_lock_free);

class DspPerformance
{
public:
    static constexpr int Streams = 4;
    static constexpr int Slots = 16;
    static constexpr int Rows = Streams * (Slots + 1);
    static constexpr unsigned EventCapacity = 256;
    static constexpr uint64_t PeakWindowSeconds = 10;

    struct NodeTiming
    {
        uint64_t token = 0, elapsedTicks = 0, endTick = 0;
        int flags = 0;
    };

    struct Block
    {
        int stream = -1, sampleRate = 0, samples = 0;
        uint64_t start = 0;
        std::array<NodeTiming, Slots> nodes {};
    };

    DspPerformance() noexcept
    {
        LARGE_INTEGER value {};
        QueryPerformanceFrequency(&value);
        frequency = static_cast<uint64_t>(value.QuadPart);
    }

    static uint64_t clock() noexcept
    {
        LARGE_INTEGER value {};
        QueryPerformanceCounter(&value);
        return static_cast<uint64_t>(value.QuadPart);
    }

    uint64_t ticksPerSecond() const noexcept { return frequency; }
    bool isEnabled() const noexcept { return enabled.load(std::memory_order_relaxed); }
    void setEnabled(bool value) noexcept { enabled.store(value, std::memory_order_release); }

    bool begin(Block& block, int stream, int sampleRate, int samples, uint64_t now) noexcept
    {
        if (!enabled.load(std::memory_order_relaxed) || stream < 0 || stream >= Streams || sampleRate <= 0 || samples <= 0)
            return false;
        auto& state = streams[static_cast<size_t>(stream)];
        // Overlapping calls skip telemetry only. The audio never waits for a reader
        // or another callback, and only one producer owns each stream's event ring.
        if (state.writer.test_and_set(std::memory_order_acquire))
        {
            state.skipped.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
        block.stream = stream;
        block.sampleRate = sampleRate;
        block.samples = samples;
        block.start = now;
        return true;
    }

    void finish(Block& block, uint64_t now) noexcept
    {
        if (block.stream < 0) return;
        auto& state = streams[static_cast<size_t>(block.stream)];
        const double budget = static_cast<double>(frequency) * block.samples / block.sampleRate;
        const bool formatChanged = state.values[0].sampleRate != block.sampleRate || state.values[0].blockSize != block.samples;
        if (formatChanged)
            state.values = {};
        // Reset peaks only. Lifetime counters and queued overrun events are preserved.
        if (formatChanged || state.peakWindowStart == 0 || now < state.peakWindowStart ||
            now - state.peakWindowStart >= frequency * PeakWindowSeconds)
        {
            for (auto& value : state.values) value.peakTicks = 0;
            state.peakWindowStart = now;
        }
        uint64_t plugins = 0;
        for (int slot = 0; slot < Slots; ++slot)
        {
            const auto& node = block.nodes[static_cast<size_t>(slot)];
            if (node.token == 0) continue;
            plugins += node.elapsedTicks;
            auto& value = state.values[static_cast<size_t>(slot + 1)];
            if (value.token != node.token) value = {};
            accumulate(value, block, slot, node.token, node.elapsedTicks, budget, node.endTick, node.flags);
            if (static_cast<double>(node.elapsedTicks) > budget)
                push(state, { block.stream, slot, block.sampleRate, block.samples, node.token,
                              node.endTick, node.elapsedTicks, budget, node.flags });
        }
        const auto elapsed = now >= block.start ? now - block.start : 0;
        auto& total = state.values[0];
        accumulate(total, block, -1, 0, elapsed, budget, now, 0);
        total.pluginTicks += plugins;
        if (static_cast<double>(elapsed) > budget)
            push(state, { block.stream, -1, block.sampleRate, block.samples, 0, now, elapsed, budget });

        // Every block contributes to counters and overrun events. Publishing the
        // display snapshot at 60 Hz avoids bouncing dozens of cache lines per block.
        if (state.lastPublish == 0 || now - state.lastPublish >= frequency / 60)
        {
            state.sequence.fetch_add(1, std::memory_order_acq_rel);
            for (int row = 0; row <= Slots; ++row)
                state.published[static_cast<size_t>(row)].store(state.values[static_cast<size_t>(row)]);
            state.sequence.fetch_add(1, std::memory_order_release);
            state.lastPublish = now;
        }
        state.writer.clear(std::memory_order_release);
        block.stream = -1;
    }

    // Single non-audio consumer. A bounded retry returns no snapshot for a stream
    // being published; neither side ever waits for the other.
    int read(DspTimingSnapshot* rows, int capacity, DspOverrunEvent* events, int eventCapacity,
             int& eventCount, uint64_t& dropped, uint64_t& skipped) noexcept
    {
        int count = 0;
        eventCount = 0;
        dropped = skipped = 0;
        for (int stream = 0; stream < Streams; ++stream)
        {
            auto& state = streams[static_cast<size_t>(stream)];
            std::array<DspTimingSnapshot, Slots + 1> copy {};
            for (int attempt = 0; attempt < 3 && capacity - count >= Slots + 1; ++attempt)
            {
                const auto before = state.sequence.load(std::memory_order_acquire);
                if ((before & 1) != 0) continue;
                for (int row = 0; row <= Slots; ++row)
                    copy[static_cast<size_t>(row)] = state.published[static_cast<size_t>(row)].load();
                std::atomic_thread_fence(std::memory_order_acquire);
                if (before != state.sequence.load(std::memory_order_acquire)) continue;
                if (copy[0].calls != 0)
                    for (const auto& value : copy) rows[count++] = value;
                break;
            }
            auto read = state.readIndex.load(std::memory_order_relaxed);
            const auto write = state.writeIndex.load(std::memory_order_acquire);
            while (read != write && eventCount < eventCapacity)
            {
                events[eventCount++] = state.events[read % EventCapacity];
                ++read;
            }
            state.readIndex.store(read, std::memory_order_release);
            dropped += state.dropped.load(std::memory_order_relaxed);
            skipped += state.skipped.load(std::memory_order_relaxed);
        }
        return count;
    }

private:
    struct AtomicSnapshot
    {
        std::atomic<int> stream { 0 }, slot { -1 }, sampleRate { 0 }, blockSize { 0 }, flags { 0 };
        std::atomic<uint64_t> token { 0 }, calls { 0 }, elapsedTicks { 0 }, pluginTicks { 0 },
            peakTicks { 0 }, overruns { 0 }, lastTick { 0 };
        std::atomic<double> budgetTicks { 0 };
        void store(const DspTimingSnapshot& value) noexcept
        {
#define ELKA_DSP_STORE(field) field.store(value.field, std::memory_order_relaxed)
            ELKA_DSP_STORE(stream); ELKA_DSP_STORE(slot); ELKA_DSP_STORE(sampleRate); ELKA_DSP_STORE(blockSize);
            ELKA_DSP_STORE(flags); ELKA_DSP_STORE(token); ELKA_DSP_STORE(calls); ELKA_DSP_STORE(elapsedTicks);
            ELKA_DSP_STORE(pluginTicks); ELKA_DSP_STORE(peakTicks); ELKA_DSP_STORE(overruns);
            ELKA_DSP_STORE(lastTick); ELKA_DSP_STORE(budgetTicks);
#undef ELKA_DSP_STORE
        }
        DspTimingSnapshot load() const noexcept
        {
            DspTimingSnapshot value;
#define ELKA_DSP_LOAD(field) value.field = field.load(std::memory_order_relaxed)
            ELKA_DSP_LOAD(stream); ELKA_DSP_LOAD(slot); ELKA_DSP_LOAD(sampleRate); ELKA_DSP_LOAD(blockSize);
            ELKA_DSP_LOAD(flags); ELKA_DSP_LOAD(token); ELKA_DSP_LOAD(calls); ELKA_DSP_LOAD(elapsedTicks);
            ELKA_DSP_LOAD(pluginTicks); ELKA_DSP_LOAD(peakTicks); ELKA_DSP_LOAD(overruns);
            ELKA_DSP_LOAD(lastTick); ELKA_DSP_LOAD(budgetTicks);
#undef ELKA_DSP_LOAD
            return value;
        }
    };

#if defined(_MSC_VER)
#pragma warning(push)
#pragma warning(disable: 4324) // Intentional cache-line padding between producers.
#endif
    struct alignas(64) Stream
    {
        std::atomic_flag writer = ATOMIC_FLAG_INIT;
        std::array<DspTimingSnapshot, Slots + 1> values {};
        uint64_t lastPublish = 0;
        uint64_t peakWindowStart = 0;
        std::atomic<uint64_t> sequence { 0 }, dropped { 0 }, skipped { 0 };
        std::array<AtomicSnapshot, Slots + 1> published {};
        std::array<DspOverrunEvent, EventCapacity> events {};
        std::atomic<unsigned> readIndex { 0 }, writeIndex { 0 };
    };
#if defined(_MSC_VER)
#pragma warning(pop)
#endif

    static void accumulate(DspTimingSnapshot& value, const Block& block, int slot, uint64_t token,
                           uint64_t ticks, double budget, uint64_t now, int flags) noexcept
    {
        value.stream = block.stream;
        value.slot = slot;
        value.sampleRate = block.sampleRate;
        value.blockSize = block.samples;
        value.token = token;
        value.flags = flags;
        ++value.calls;
        value.elapsedTicks += ticks;
        value.budgetTicks += budget;
        value.peakTicks = std::max(value.peakTicks, ticks);
        value.overruns += static_cast<double>(ticks) > budget ? 1 : 0;
        value.lastTick = now;
    }

    static void push(Stream& state, const DspOverrunEvent& event) noexcept
    {
        const auto write = state.writeIndex.load(std::memory_order_relaxed);
        if (write - state.readIndex.load(std::memory_order_acquire) >= EventCapacity)
        {
            state.dropped.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        state.events[write % EventCapacity] = event;
        state.writeIndex.store(write + 1, std::memory_order_release);
    }

    std::array<Stream, Streams> streams;
    std::atomic<bool> enabled { false };
    uint64_t frequency = 0;
};

class DspPerformanceScope
{
public:
    DspPerformanceScope(DspPerformance& owner, int stream, int rate, int samples) noexcept : owner(owner)
    {
        if (owner.isEnabled()) owner.begin(block, stream, rate, samples, DspPerformance::clock());
    }
    ~DspPerformanceScope() { if (active()) owner.finish(block, DspPerformance::clock()); }
    bool active() const noexcept { return block.stream >= 0; }
    void record(int slot, uint64_t token, uint64_t start, uint64_t end, int flags) noexcept
    {
        if (slot >= 0 && slot < DspPerformance::Slots)
            block.nodes[static_cast<size_t>(slot)] = { token, end >= start ? end - start : 0, end, flags };
    }
private:
    DspPerformance& owner;
    DspPerformance::Block block;
};
}
