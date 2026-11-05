#include "engine/RealtimeEngine.h"
#include <cmath>
#include <cstring>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <new>

thread_local bool watchAllocations = false;
thread_local int allocations = 0;
void* operator new(size_t size)
{
    if (watchAllocations) ++allocations;
    if (auto p = std::malloc(size ? size : 1)) return p;
    throw std::bad_alloc();
}
void* operator new[](size_t size) { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }

using namespace elka;
namespace elka
{
struct OutputBoostTestAccess
{
    static bool work(const RealtimeEngine& e, CallbackStreamKind kind, int stream)
    { return e.hasProcessingWork(kind, false, stream); }
};
}
int checks = 0;
void check(bool result, const char* message)
{
    ++checks;
    if (!result) { std::cerr << "FAIL: " << message << '\n'; std::exit(1); }
}
void checkNear(float actual, float expected, const char* message)
{ check(std::abs(actual - expected) < 0.0001f * std::max(1.0f, std::abs(expected)), message); }

int main()
{
    constexpr int frames = 64;
    for (int stream : {0, 1, 3})
    {
        auto engine = std::make_unique<RealtimeEngine>();
        auto kind = stream == 1 ? CallbackStreamKind::OutputInsert : CallbackStreamKind::InputInsert;
        std::array<float, frames> left, right, outLeft{}, outRight{};
        left.fill(0.25f); right.fill(-0.125f);
        float* read[]{left.data(), right.data()}, *write[]{outLeft.data(), outRight.data()};
        AudioBufferView buffer{48000, frames, 2, 2, read, write};
        auto process = [&]
        {
            watchAllocations = true;
            if (stream == 3) engine->processInsertAsio(buffer); else engine->process(buffer, kind);
            watchAllocations = false;
        };
        check(!OutputBoostTestAccess::work(*engine, kind, stream), "zero boost has no processing work");
        process();
        check(std::memcmp(left.data(), outLeft.data(), sizeof(left)) == 0 &&
            std::memcmp(right.data(), outRight.data(), sizeof(right)) == 0, "zero is bit-exact passthrough");

        // Milli-dB API rounds +6.0206 dB to +6.021 dB (within 0.005% of doubling).
        engine->setOutputBoostMilliDb(kind, 0, 6021);
        check(OutputBoostTestAccess::work(*engine, kind, stream), "boost alone wakes the processing path");
        const float doubled = std::pow(10.0f, 6021.0f / 20000.0f);
        process();
        for (int i = 0; i < frames; ++i)
            checkNear(outLeft[i], 0.25f * (1.0f + (doubled - 1.0f) * (i + 1) / frames), "gain ramps throughout one block");
        process();
        for (float sample : outLeft) checkNear(sample, 0.5f, "approximately double after ramp");
        check(outRight == right, "other channels remain bit exact");

        engine->setOutputBoostMilliDb(kind, 0, 0);
        check(OutputBoostTestAccess::work(*engine, kind, stream), "reset retains the last unity ramp");
        process();
        for (int i = 0; i < frames; ++i)
            checkNear(outLeft[i], 0.25f * (doubled + (1 - doubled) * (i + 1) / frames), "zero reset also ramps");
        check(!OutputBoostTestAccess::work(*engine, kind, stream), "unity ramp ends processing work");
        process(); check(outLeft == left, "zero restored exactly after ramp");

        engine->setChannelEnabled(kind, 0, false);
        engine->setOutputBoostMilliDb(kind, 0, 99000);
        check(engine->getOutputBoostMilliDb(kind, 0) == 24000, "positive settings clamp at +24 dB");
        process(); process();
        checkNear(outLeft[0], 0.25f * std::pow(10.0f, 1.2f), "boost works independently of disabled Advanced controls");
        check(outLeft[0] > 1, "samples above full scale are not clipped");
        left.fill(2.0f); process();
        check(outLeft[0] > 30, "no hidden +18 dBFS or +24 dBFS sample clamp");

        engine->setOutputBoostMilliDb(kind, 0, -99000);
        check(engine->getOutputBoostMilliDb(kind, 0) == -24000, "negative settings clamp at -24 dB");
        process(); process();
        checkNear(outLeft[0], 2 * std::pow(10.0f, -1.2f), "attenuation");

        engine->setChannelEnabled(kind, 0, true);
        engine->setChannelGainPercent(kind, 0, 50);
        engine->setOutputBoostMilliDb(kind, 0, 6021);
        process(); process();
        checkNear(outLeft[0], doubled, "Advanced volume and boost multiply");
        engine->setOutputBoostMilliDb(kind, -1, 1000);
        engine->setOutputBoostMilliDb(kind, 64, 1000);
        check(engine->getOutputBoostMilliDb(kind, 64) == 0, "invalid channels ignored");
    }
    {
        auto engine = std::make_unique<RealtimeEngine>();
        engine->setOutputBoostMilliDb(CallbackStreamKind::InputInsert, 0, 12000);
        engine->setOutputBoostMilliDb(CallbackStreamKind::OutputInsert, 0, 24000);
        engine->setOutputBoostMilliDb(CallbackStreamKind::Main, 0, 24000);
        check(engine->getOutputBoostMilliDb(CallbackStreamKind::Main, 0) == 0 &&
            !OutputBoostTestAccess::work(*engine, CallbackStreamKind::Main, 2), "Main is never boosted or activated");
        std::array<float, frames> input, bus, output{};
        input.fill(0.75f); bus.fill(0.125f);
        float* read[]{input.data(), bus.data()}, *write[]{output.data()};
        engine->process({48000, frames, 2, 1, read, write}, CallbackStreamKind::Main);
        check(output == bus, "Main output unaffected by both insert banks");
        engine->setInputCallbackSuppressedChannel(0, true);
        output.fill(-0.5f);
        engine->process({48000, frames, 1, 1, read, write}, CallbackStreamKind::InputInsert);
        check(output[0] == 0 && output.back() == 0, "suppressed Input channel retains existing silence");
    }
    {
        auto engine = std::make_unique<RealtimeEngine>();
        float samples[]{0.5f, -0.5f}, output[2]{};
        float* read[]{samples}, *write[]{output};
        AudioBufferView block{48000, 2, 1, 1, read, write};
        engine->peakHold.watch(0, false, 0, 1, true);
        engine->peakHold.watch(0, true, 0, 1, true);
        engine->setOutputBoostMilliDb(CallbackStreamKind::InputInsert, 0, 12000);
        engine->process(block, CallbackStreamKind::InputInsert);
        engine->process(block, CallbackStreamKind::InputInsert);
        float source{}, destination{}; uint32_t sourceOvers{}, destinationOvers{};
        engine->peakHold.read(0, false, 0, 1, &source, &sourceOvers);
        engine->peakHold.read(0, true, 0, 1, &destination, &destinationOvers);
        checkNear(20 * std::log10(destination / source), 12, "destination monitor peak rises by exactly 12 dB");
        check(sourceOvers == 0 && destinationOvers > 0, "only output peak warning latches");
        engine->setChannelPluginGraphEnabled(CallbackStreamKind::InputInsert, 0, true);
        engine->process(block, CallbackStreamKind::InputInsert);
        check(output[0] == 0 && output[1] == 0, "boost never bypasses the graph gate");
    }
    check(allocations == 0, "boost processing does not allocate");
    std::cout << "PASS: " << checks << " output boost checks. No live audio connected.\n";
}
