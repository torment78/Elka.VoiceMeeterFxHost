#include "engine/RealtimeEngine.h"
#include "native_api/PluginLevelsReader.h"
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <new>
#include <thread>

thread_local bool countAllocations = false;
thread_local uint64_t allocations = 0;
void* operator new(size_t size)
{
    if (countAllocations) ++allocations;
    if (auto* p = std::malloc(size ? size : 1)) return p;
    throw std::bad_alloc();
}
void* operator new[](size_t size) { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }
using namespace elka;
int checks = 0;
void check(bool value, const char* message)
{
    ++checks;
    if (!value) { std::cerr << "FAIL: " << message << '\n'; std::exit(1); }
}

class Gain final : public RealtimePluginProcessor
{
public:
    float gain = 2;
    int calls = 0, pins = 2;
    std::array<std::array<float, 256>, 32> scratch {};
    bool process(AudioBufferView block, const PluginRoutingView& routing) noexcept override
    {
        ++calls;
        for (int p = 0; p < pins; ++p) std::fill_n(scratch[p].data(), block.samplesPerFrame, 0.0f);
        for (int r = 0; r < routing.inputRouteCount; ++r)
        {
            auto& route = routing.inputRoutes[r];
            for (int i = 0; i < block.samplesPerFrame; ++i) scratch[route.pluginPin][i] += route.source[i] * gain;
        }
        if (routing.outputLevels)
            for (int p = 0; p < pins; ++p) routing.outputLevels->capture(p, scratch[p].data(), block.samplesPerFrame);
        for (int r = 0; r < routing.outputRouteCount; ++r)
        {
            auto& route = routing.outputRoutes[r];
            for (int i = 0; i < block.samplesPerFrame; ++i)
                route.destination[i] = (route.clearDestination ? 0 : route.destination[i]) + scratch[route.pluginPin][i];
        }
        return true;
    }
};

void collectorChecks()
{
    PluginLevels levels;
    float a[] { .25f, -.5f, .125f }, b[] { -.25f, .5f, .125f };
    PluginAudioInputRoute routes[] { {a, 0}, {b, 0}, {a, 31} };
    auto input = PluginLevels::input(0x80000001u, routes, 3, 3);
    check(input.peaks[0] == .25f && input.peaks[31] == .5f, "same-pin sums preserve cancellation; channels are independent");
    PluginLevelCapture output { 0x80000001u };
    PluginLevels::bypass(output, routes, 3, 3);
    check(output.peaks[0] == .5f && output.peaks[31] == .5f, "bypass follows the existing first matching input behavior");
    float invalid[] { NAN, INFINITY, -INFINITY, -20 };
    output.capture(0, invalid, 4);
    check(output.peaks[0] == 20, "finite over-zero samples retained; nonfinite values ignored");
    output.capture(4, invalid, 4);
    check(output.peaks[4] == 0, "unwatched output pins not scanned");
    std::array<uint64_t, 16> masks {};
    masks[15] = 0x8000000180000001ULL;
    std::array<float, PluginLevels::Values> peaks {};
    check(!levels.watches(nullptr, 16) && !levels.watches(masks.data(), 15) && !levels.read(peaks.data(), 1), "ABI capacities validated");
    levels.watches(masks.data(), 16);
    levels.publish(15, 0, input); levels.publish(15, 32, output);
    levels.read(peaks.data(), 1024);
    check(peaks[15 * 64] == .25f && peaks[15 * 64 + 32] == 20 && peaks.back() == .5f, "independent side, slot and pin 31");
    levels.read(peaks.data(), 1024);
    check(std::all_of(peaks.begin(), peaks.end(), [](float p) { return p == 0; }), "drain clears interval peaks; stopped streams fall to silence");
    levels.publish(15, 0, input); levels.reset(15); levels.read(peaks.data(), 1024);
    check(peaks[15 * 64] == 0 && levels.mask(15) == masks[15], "slot replacement clears old peaks but retains subscriptions");
    PluginLevelsReader reader;
    check(reader.read(peaks.data(), 1024) == -1, "reader never starts the host");
    reader.attach(levels);
    check(reader.watches(masks.data(), 16) == 0 && reader.read(peaks.data(), 1024) == 0, "reader attached");
    reader.detach();
    check(reader.read(peaks.data(), 1024) == -1, "reader detached");
}

void engineChecks()
{
    auto engine = std::make_unique<RealtimeEngine>();
    Gain plugin, other; other.gain = 8;
    PluginInputRoute inputs[] { {0, 0, -1, -1, 0}, {0, 1, -1, -1, 1} };
    PluginOutputRoute outputs[] { {0, 0, 0, -1, -1}, {0, 1, 1, -1, -1} };
    std::array<float, 256> source, left {}, right {};
    source.fill(.25f);
    float* read[] { source.data(), source.data() };
    float* write[] { left.data(), right.data() };
    AudioBufferView block { 48000, 256, 2, 2, read, write };
    std::array<uint64_t, 16> masks {};
    std::array<float, 1024> peaks {};
    for (int stream = 0; stream < 4; ++stream)
    for (bool power : {false, true})
    for (bool bypass : {false, true})
    {
        auto kind = stream == 3 ? CallbackStreamKind::InputInsert : static_cast<CallbackStreamKind>(stream);
        engine->setPluginSlot(0, &plugin, kind, inputs, 2, outputs, 2, true, bypass, power);
        engine->setPluginSlot(1, &other, kind, inputs, 2, outputs, 2, true, false, true);
        auto process = [&] { if (stream == 3) engine->processInsertAsio(block); else engine->process(block, kind); };
        masks.fill(0); engine->pluginLevels.watches(masks.data(), 16);
        process(); const auto expectedLeft = left, expectedRight = right;
        int calls = plugin.calls;
        masks[0] = masks[1] = 0x300000003ULL; engine->pluginLevels.watches(masks.data(), 16);
        allocations = 0; countAllocations = true; process(); countAllocations = false;
        check(allocations == 0, "metered callbacks allocate no heap memory");
        check(left == expectedLeft && right == expectedRight, "metered and unmetered audio bit-identical on all paths/states");
        check(plugin.calls == calls + (power ? 1 : 0), "no extra plugin execution");
        engine->pluginLevels.read(peaks.data(), 1024);
        check(peaks[0] == .25f && peaks[1] == .25f, "incoming node level visible even when powered off");
        check(peaks[32] == (bypass ? .25f : power ? .5f : 0), "output follows bypass/off/active node state");
        check(peaks[64 + 32] == 2, "shared destination never contaminates another node's output reading");
    }
    engine->clearPluginSlots();
    engine->pluginLevels.read(peaks.data(), 1024);
    check(std::all_of(peaks.begin(), peaks.end(), [](float p) { return p == 0; }), "removed nodes leave no stale values");
}

void concurrentChecks()
{
    PluginLevels levels;
    std::array<float, 1024> peaks {};
    std::atomic<bool> done {};
    std::thread producer([&] {
        PluginLevelCapture capture { 1 }; capture.peaks[0] = 2;
        for (int i = 0; i < 50000; ++i) levels.publish(0, 0, capture);
        done = true;
    });
    bool bad = false; float maximum = 0;
    while (!done)
    {
        levels.read(peaks.data(), 1024);
        bad |= peaks[0] != 0 && peaks[0] != 2; maximum = std::max(maximum, peaks[0]);
    }
    producer.join(); levels.read(peaks.data(), 1024); maximum = std::max(maximum, peaks[0]);
    check(!bad && maximum == 2, "concurrent drains and publications stay finite and preserve peaks");
}

void benchmark()
{
    auto engine = std::make_unique<RealtimeEngine>();
    auto plugins = std::make_unique<std::array<Gain, 16>>();
    std::array<std::array<float, 256>, 8> input {}, output {};
    std::array<float*, 8> read {}, write {};
    std::array<PluginInputRoute, 8> ins {};
    std::array<PluginOutputRoute, 8> outs {};
    for (int p = 0; p < 8; ++p)
    {
        input[p].fill(.01f); read[p] = input[p].data(); write[p] = output[p].data();
        ins[p] = {0, p, -1, -1, p}; outs[p] = {0, p, p, -1, -1};
    }
    for (int count : {1, 5, 16})
    for (int pins : {2, 8})
    {
        engine->clearPluginSlots();
        for (int s = 0; s < count; ++s)
        {
            (*plugins)[s].pins = pins;
            engine->setPluginSlot(s, &(*plugins)[s], CallbackStreamKind::InputInsert,
                ins.data(), pins, outs.data(), pins, true, false, true);
        }
        std::array<uint64_t, 16> masks {};
        std::array<std::array<double, 7>, 2> times {};
        AudioBufferView block {48000, 256, pins, pins, read.data(), write.data()};
        for (int round = 0; round < 7; ++round)
        for (int run = 0; run < 2; ++run)
        {
            int on = (run + round) % 2;
            masks.fill(0);
            for (int s = 0; s < count; ++s) if (on) masks[s] = ((uint64_t{1} << pins) - 1) * 0x100000001ULL;
            engine->pluginLevels.watches(masks.data(), 16);
            for (int i = 0; i < 100; ++i) engine->processInsertAsio(block);
            auto start = std::chrono::steady_clock::now();
            for (int i = 0; i < 3000; ++i) engine->processInsertAsio(block);
            times[on][round] = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - start).count() / 3000;
        }
        for (auto& values : times) std::sort(values.begin(), values.end());
        std::cout << count << " VSTs / " << pins << " pins / 48k / 256: hidden=" << times[0][3]
            << " us, visible=" << times[1][3] << " us, added=" << times[1][3] - times[0][3] << " us/block\n";
    }
    std::cout << "Native collector storage: " << sizeof(PluginLevels) << " bytes (fixed, no audio history).\n";
}
int main()
{
    collectorChecks(); engineChecks(); concurrentChecks(); benchmark();
    std::cout << "Widget levels: " << checks << " checks passed; no audio device opened.\n";
}
