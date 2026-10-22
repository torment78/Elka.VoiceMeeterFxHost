#include "engine/RealtimeEngine.h"
#include <iostream>
#include <memory>
#include <thread>
#include <cmath>
#include <cstdlib>
#include <new>

thread_local bool countAllocations = false;
thread_local uint64_t allocationCount = 0;
void* operator new(size_t size)
{
    if (countAllocations) ++allocationCount;
    if (auto* value = std::malloc(size == 0 ? 1 : size)) return value;
    throw std::bad_alloc();
}
void* operator new[](size_t size) { return ::operator new(size); }
void operator delete(void* value) noexcept { std::free(value); }
void operator delete[](void* value) noexcept { std::free(value); }
void operator delete(void* value, size_t) noexcept { std::free(value); }
void operator delete[](void* value, size_t) noexcept { std::free(value); }

using namespace elka;
int checks = 0;
void check(bool value, const char* message)
{
    ++checks;
    if (!value) { std::cerr << "FAIL: " << message << '\n'; std::exit(1); }
}

struct Readback
{
    std::array<DspTimingSnapshot, DspPerformance::Rows> rows {};
    std::array<DspOverrunEvent, 1024> events {};
    int count = 0, eventCount = 0;
    uint64_t dropped = 0, skipped = 0;
    void read(DspPerformance& monitor)
    {
        count = monitor.read(rows.data(), static_cast<int>(rows.size()), events.data(),
            static_cast<int>(events.size()), eventCount, dropped, skipped);
    }
};

void collectorChecks()
{
    auto monitor = std::make_unique<DspPerformance>();
    DspPerformance::Block block;
    check(!monitor->begin(block, 0, 48000, 256, 1), "disabled telemetry");
    monitor->setEnabled(true);
    check(!monitor->begin(block, 4, 48000, 256, 1), "invalid stream");
    check(!monitor->begin(block, 0, 0, 256, 1), "invalid sample rate");
    check(!monitor->begin(block, 0, 48000, 0, 1), "invalid block");
    const auto second = monitor->ticksPerSecond();
    const auto ms = second / 1000;
    // Use a 5 ms budget so exact 100% is representable in timer ticks.
    check(monitor->begin(block, 0, 48000, 240, second), "begin input");
    block.nodes[0] = { 41, 5 * ms, second + 5 * ms, 0 };
    monitor->finish(block, second + 5 * ms);
    Readback result;
    result.read(*monitor);
    check(result.count == 17, "one stream only");
    check(result.eventCount == 0, "exactly 100% is not an overrun");
    check(result.rows[0].pluginTicks == 5 * ms, "callback plugin sum");
    check(result.rows[1].token == 41 && result.rows[1].calls == 1, "node identity and call count");

    block = {};
    monitor->begin(block, 0, 48000, 240, second * 2);
    block.nodes[0] = { 41, 7 * ms, second * 2 + 7 * ms, 1 };
    monitor->finish(block, second * 2 + 8 * ms);
    result.read(*monitor);
    check(result.eventCount == 2, "plugin and callback overrun distinguished");
    check(result.events[0].slot == 0 && result.events[1].slot == -1, "event source");
    check(result.events[0].flags == 1, "worker wait label");
    check(result.rows[1].overruns == 1 && result.rows[1].calls == 2, "all blocks counted");
    check(result.rows[1].peakTicks == 7 * ms, "retained peak");

    block = {};
    monitor->begin(block, 0, 96000, 128, second * 3);
    block.nodes[0] = { 42, ms, second * 3 + ms, 0 };
    monitor->finish(block, second * 3 + ms);
    result.read(*monitor);
    check(result.rows[0].sampleRate == 96000 && result.rows[0].blockSize == 128, "format update");
    check(result.rows[1].token == 42 && result.rows[1].calls == 1 && result.rows[1].overruns == 0,
          "format and reused slot reset measurement totals");
    check(std::abs(result.rows[0].budgetTicks - second * 128.0 / 96000.0) < .001, "fractional cycle budget");

    block = {};
    monitor->begin(block, 1, 48000, 240, second * 4);
    DspPerformance::Block overlapping;
    check(!monitor->begin(overlapping, 1, 48000, 240, second * 4), "overlap skips telemetry without waiting");
    monitor->finish(block, second * 4 + ms);
    result.read(*monitor);
    check(result.skipped == 1, "skipped measurements visible");

    for (int i = 0; i < 400; ++i)
    {
        block = {};
        monitor->begin(block, 3, 48000, 240, second * 5 + i * 10 * ms);
        monitor->finish(block, second * 5 + i * 10 * ms + 8 * ms);
    }
    result.read(*monitor);
    check(result.eventCount == DspPerformance::EventCapacity, "bounded event ring");
    check(result.dropped == 400 - DspPerformance::EventCapacity, "dropped events explicitly counted");
    for (int i = 1; i < result.eventCount; ++i)
        check(result.events[i].timestamp > result.events[i - 1].timestamp, "event ordering");
    result.read(*monitor);
    check(result.eventCount == 0, "event drain does not repeat");
}

void publicationRateChecks()
{
    auto monitor = std::make_unique<DspPerformance>();
    monitor->setEnabled(true);
    const auto second = monitor->ticksPerSecond();
    const auto ms = second / 1000;
    const auto publish = [&](uint64_t start) {
        DspPerformance::Block block;
        monitor->begin(block, 0, 48000, 240, start);
        block.nodes[0] = { 41, ms, start + ms, 0 };
        monitor->finish(block, start + ms);
    };
    Readback result;
    publish(second);
    result.read(*monitor);
    check(result.rows[0].calls == 1, "first timing snapshot published immediately");
    publish(second + second / 60 - 1);
    result.read(*monitor);
    check(result.rows[0].calls == 1, "publication remains bounded before one sixtieth second");
    publish(second + second / 60);
    result.read(*monitor);
    check(result.rows[0].calls == 3 && result.rows[1].calls == 3, "60 Hz publication includes every measured block");
    check(result.rows[0].elapsedTicks == 3 * ms && result.rows[1].peakTicks == ms,
          "publication frequency does not change measurements");
}

class GainProcessor final : public RealtimePluginProcessor
{
public:
    int calls = 0;
    bool process(AudioBufferView block, const PluginRoutingView& routing) noexcept override
    {
        ++calls;
        for (int out = 0; out < routing.outputRouteCount; ++out)
        {
            const auto& target = routing.outputRoutes[out];
            for (int sample = 0; sample < block.samplesPerFrame; ++sample)
            {
                float value = 0;
                for (int in = 0; in < routing.inputRouteCount; ++in)
                    if (routing.inputRoutes[in].pluginPin == target.pluginPin)
                        value += routing.inputRoutes[in].source[sample] * .5f;
                if (target.clearDestination) target.destination[sample] = value;
                else target.destination[sample] += value;
            }
        }
        return true;
    }
};

void engineChecks()
{
    auto engine = std::make_unique<RealtimeEngine>();
    GainProcessor plugin;
    PluginInputRoute inputs[2] { { 0, 0, -1, -1, 0 }, { 0, 1, -1, -1, 1 } };
    PluginOutputRoute outputs[2] { { 0, 0, 0, -1, -1 }, { 0, 1, 1, -1, -1 } };
    std::array<float, 256> left {}, right {}, outLeft {}, outRight {};
    for (int i = 0; i < 256; ++i) { left[i] = .4f * std::sin(i * .1f); right[i] = -.3f * std::cos(i * .1f); }
    float* read[2] { left.data(), right.data() };
    float* write[2] { outLeft.data(), outRight.data() };
    AudioBufferView buffer { 48000, 256, 2, 2, read, write };
    for (const auto kind : { CallbackStreamKind::InputInsert, CallbackStreamKind::OutputInsert, CallbackStreamKind::Main })
    {
        engine->setPluginSlot(0, &plugin, kind, inputs, 2, outputs, 2, true, false, true);
        engine->dspPerformance.setEnabled(false);
        engine->process(buffer, kind);
        const auto expectedLeft = outLeft, expectedRight = outRight;
        engine->dspPerformance.setEnabled(true);
        const int before = plugin.calls;
        engine->process(buffer, kind);
        check(plugin.calls == before + 1, "timing does not execute plugin twice");
        check(outLeft == expectedLeft && outRight == expectedRight, "callback audio is bit-identical with telemetry");
    }
    engine->setPluginSlot(0, &plugin, CallbackStreamKind::InputInsert, inputs, 2, outputs, 2, true, false, true);
    engine->dspPerformance.setEnabled(false);
    engine->processInsertAsio(buffer);
    const auto expectedLeft = outLeft, expectedRight = outRight;
    engine->dspPerformance.setEnabled(true);
    engine->processInsertAsio(buffer);
    check(outLeft == expectedLeft && outRight == expectedRight, "ASIO audio is bit-identical with telemetry");
    Readback result;
    result.read(engine->dspPerformance);
    check(result.count == DspPerformance::Rows, "all four processing paths measured");
    check(engine->pluginPerformanceToken(0) == plugin.performanceToken, "native identity mapping");
    engine->clearPluginSlot(0);
    check(engine->pluginPerformanceToken(0) == 0, "removed node has no live token");
}

void concurrentChecks()
{
    auto monitor = std::make_unique<DspPerformance>();
    monitor->setEnabled(true);
    std::atomic<int> finished { 0 };
    std::array<std::thread, 4> producers;
    const auto second = monitor->ticksPerSecond();
    for (int stream = 0; stream < 4; ++stream)
        producers[stream] = std::thread([&, stream] {
            for (int i = 1; i <= 10000; ++i)
            {
                DspPerformance::Block block;
                const auto start = second * i;
                monitor->begin(block, stream, 48000, 256, start);
                block.nodes[0] = { static_cast<uint64_t>(stream + 1), 10, start + 10, 0 };
                monitor->finish(block, start + 20);
            }
            ++finished;
        });
    Readback result;
    while (finished != 4)
    {
        result.read(*monitor);
        for (int i = 0; i < result.count; ++i)
        {
            const auto& row = result.rows[i];
            if (row.calls == 0) continue;
            check(row.elapsedTicks == row.calls * (row.slot < 0 ? 20 : 10), "coherent concurrent snapshot");
        }
    }
    for (auto& thread : producers) thread.join();
    result.read(*monitor);
    check(result.count == 68 && result.skipped == 0, "independent stream telemetry");
    for (int stream = 0; stream < 4; ++stream)
        check(result.rows[stream * 17].calls == 10000, "no lost processed blocks");
}

void overheadChecks()
{
    auto engine = std::make_unique<RealtimeEngine>();
    std::array<GainProcessor, 16> plugins;
    PluginInputRoute inputs[2] { { 0, 0, -1, -1, 0 }, { 0, 1, -1, -1, 1 } };
    PluginOutputRoute outputs[2] { { 0, 0, 0, -1, -1 }, { 0, 1, 1, -1, -1 } };
    for (int slot = 0; slot < 16; ++slot)
        engine->setPluginSlot(slot, &plugins[slot], CallbackStreamKind::InputInsert,
                              inputs, 2, outputs, 2, true, false, true);
    std::array<float, 256> source {}, destination {};
    source.fill(.01f);
    float* read[2] { source.data(), source.data() };
    std::array<float, 256> right {};
    float* write[2] { destination.data(), right.data() };
    const auto frequency = engine->dspPerformance.ticksPerSecond();
    for (int rate : { 48000, 96000, 192000 })
    {
        AudioBufferView buffer { rate, 128, 2, 2, read, write };
        std::array<double, 2> best { 1.0e9, 1.0e9 };
        for (int round = 0; round < 4; ++round)
            for (int enabled = 0; enabled <= 1; ++enabled)
            {
                engine->dspPerformance.setEnabled(enabled != 0);
                for (int warm = 0; warm < 100; ++warm) engine->processInsertAsio(buffer);
                allocationCount = 0;
                countAllocations = true;
                const auto start = DspPerformance::clock();
                for (int i = 0; i < 5000; ++i) engine->processInsertAsio(buffer);
                const auto elapsed = DspPerformance::clock() - start;
                countAllocations = false;
                check(allocationCount == 0, "no C++ heap allocation in instrumented audio processing");
                best[enabled] = std::min(best[enabled], elapsed * 1000000.0 / frequency / 5000);
            }
        std::cout << rate << " Hz / 128, 16 synthetic VSTs: disabled " << best[0]
                  << " us, measured " << best[1] << " us, delta " << best[1] - best[0] << " us/block\n";
    }
    engine->clearPluginSlots();
}

int main()
{
    collectorChecks();
    publicationRateChecks();
    engineChecks();
    concurrentChecks();
    overheadChecks();
    std::cout << "DSP performance: " << checks << " checks passed. No audio device opened.\n";
}
