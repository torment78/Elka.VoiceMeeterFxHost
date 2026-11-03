#include "engine/RealtimeEngine.h"
#include "native_api/PeakHoldReader.h"
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

void holdChecks()
{
    PeakHold hold;
    std::array<float, 4> samples { -.5f, -2, .25f, 1 };
    std::array<float*, 128> channels;
    channels.fill(samples.data());
    std::array<float, 128> maximum {};
    std::array<uint32_t, 128> overs {};
    hold.capture(0, false, channels.data(), 128, 4);
    hold.read(0, false, 0, 128, maximum.data(), overs.data());
    check(maximum[0] == 0 && overs[0] == 0, "unwatched data is not scanned");
    check(hold.watch(0, false, 0, 128, true), "watch both mask words");
    hold.watch(0, false, 63, 2, true);
    hold.watch(0, false, 0, 128, false);
    countAllocations = true;
    hold.capture(0, false, channels.data(), 128, 4);
    countAllocations = false;
    check(allocations == 0, "capture allocates no heap memory");
    hold.read(0, false, 0, 128, maximum.data(), overs.data());
    for (int c = 0; c < 128; ++c)
        check(maximum[c] == ((c == 63 || c == 64) ? 2 : 0) && overs[c] == ((c == 63 || c == 64) ? 1u : 0u),
            "overlapping watches retain only their channels across the word boundary");
    samples = { 1, 1, 1, 1 };
    hold.capture(0, false, channels.data(), 128, 4);
    hold.read(0, false, 63, 2, maximum.data(), overs.data());
    check(maximum[0] == 2 && overs[0] == 2, "max never falls; overs counts blocks not samples; equality included");
    samples = { NAN, INFINITY, -INFINITY, -.75f };
    hold.capture(0, false, channels.data(), 128, 4);
    hold.read(0, false, 63, 2, maximum.data(), overs.data());
    check(maximum[0] == 2 && overs[0] == 2, "nonfinite values ignored");
    hold.reset(0, false, 63, 1);
    hold.read(0, false, 63, 2, maximum.data(), overs.data());
    check(maximum[0] == 0 && overs[0] == 0 && maximum[1] == 2, "reset clears only requested channels");
    hold.capture(0, false, channels.data(), 128, 4);
    hold.read(0, false, 63, 1, maximum.data(), overs.data());
    check(maximum[0] == .75f && overs[0] == 0, "finite sample retained beside invalid values");
    for (int stream = 0; stream < 3; ++stream)
    for (bool output : { false, true })
    {
        hold.watch(stream, output, 127, 1, true);
        samples[3] = 3 + float(stream) + (output ? .5f : 0);
        hold.capture(stream, output, channels.data(), 128, 4);
        hold.read(stream, output, 127, 1, maximum.data(), overs.data());
        check(maximum[0] == samples[3] && overs[0] == 1, "independent stream and side at channel 127");
    }
    channels[127] = nullptr;
    hold.reset(2, true, 127, 1);
    hold.capture(2, true, channels.data(), 128, 4);
    hold.capture(2, true, nullptr, 128, 4);
    hold.capture(2, true, channels.data(), 128, 0);
    hold.capture(-1, true, channels.data(), 128, 4);
    hold.read(2, true, 127, 1, maximum.data(), overs.data());
    check(maximum[0] == 0, "null data and invalid capture do not touch storage");
    check(!hold.watch(3, false, 0, 1, true) && !hold.watch(0, false, 127, 2, true) &&
        !hold.reset(0, false, -1, 1) && !hold.read(0, false, 0, 0, maximum.data(), overs.data()), "range validation");
    hold.watch(0, false, 63, 2, false);
    hold.watch(0, false, 63, 2, false);
    hold.reset(0, false, 63, 2);
    hold.capture(0, false, channels.data(), 128, 4);
    hold.read(0, false, 63, 2, maximum.data(), overs.data());
    check(maximum[0] == 0 && maximum[1] == 0, "final unwatch removes work and underflow is harmless");
}

void concurrentChecks()
{
    auto hold = std::make_unique<PeakHold>();
    hold->watch(0, false, 0, 1, true);
    std::atomic<bool> start {};
    auto produce = [&](float sign)
    {
        float value = 0;
        float* channels[] { &value };
        while (!start.load()) std::this_thread::yield();
        for (int i = 1; i <= 50000; ++i)
        {
            value = sign * i;
            hold->capture(0, false, channels, 1, 1);
        }
    };
    std::thread first(produce, 1.0f), second(produce, -2.0f);
    start = true;
    first.join(); second.join();
    float maximum = 0;
    uint32_t overs = 0;
    hold->read(0, false, 0, 1, &maximum, &overs);
    check(maximum == 100000 && overs == 100000, "two concurrent writers keep true maximum and all block counts");

    PeakHoldReader reader;
    check(reader.read(0, false, 0, 1, &maximum, &overs) == -1, "no host returns unavailable");
    reader.attach(*hold);
    std::atomic<bool> stop {};
    std::atomic<bool> invalid {};
    std::thread consumer([&]
    {
        float peak = 0; uint32_t count = 0;
        while (!stop.load())
        {
            int result = reader.read(0, false, 0, 1, &peak, &count);
            if (result != -1 && (result != 0 || peak != 100000)) invalid = true;
        }
    });
    for (int i = 0; i < 1000; ++i) { reader.detach(); reader.attach(*hold); }
    reader.detach();
    hold.reset();
    stop = true;
    consumer.join();
    check(!invalid, "read/detach never dereferences a destroyed host or waits on plugin loading");
}

void engineChecks()
{
    auto engine = std::make_unique<RealtimeEngine>();
    std::array<std::array<float, 256>, 40> input {}, output {};
    std::array<float*, 40> read {}, write {};
    for (int c = 0; c < 40; ++c)
    {
        input[c].fill(c == 0 ? -2.0f : .25f);
        read[c] = input[c].data(); write[c] = output[c].data();
    }
    AudioBufferView buffer { 48000, 256, 40, 40, read.data(), write.data() };
    for (int stream = 0; stream < 4; ++stream)
    {
        auto process = [&] { if (stream == 3) engine->processInsertAsio(buffer); else engine->process(buffer, static_cast<CallbackStreamKind>(stream)); };
        process();
        auto expected = output;
        int holdStream = stream == 3 ? 0 : stream;
        for (bool side : { false, true }) engine->peakHold.watch(holdStream, side, 0, 40, true);
        countAllocations = true; allocations = 0;
        process();
        countAllocations = false;
        check(output == expected && allocations == 0, "all callback and ASIO audio stays bit-identical, with no allocations");
        for (bool side : { false, true })
        {
            float maximum; uint32_t overs;
            engine->peakHold.read(holdStream, side, 0, 1, &maximum, &overs);
            check(maximum == 2 && overs > 0, "capture integrated on both sides of each processing entry point");
            engine->peakHold.watch(holdStream, side, 0, 40, false);
        }
    }
    std::array<std::array<double, 7>, 2> timings {};
    for (int round = 0; round < 7; ++round)
    for (int pass = 0; pass < 2; ++pass)
    {
        int enabled = (pass + round) % 2;
        if (enabled)
            for (bool side : { false, true })
                for (int endpoint = 0; endpoint < 5; ++endpoint) engine->peakHold.watch(0, side, endpoint * 8, 8, true);
        for (int warm = 0; warm < 200; ++warm) engine->process(buffer, CallbackStreamKind::InputInsert);
        auto start = std::chrono::steady_clock::now();
        for (int i = 0; i < 5000; ++i) engine->process(buffer, CallbackStreamKind::InputInsert);
        timings[enabled][round] = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - start).count() / 5000;
        if (enabled)
            for (bool side : { false, true })
                for (int endpoint = 0; endpoint < 5; ++endpoint) engine->peakHold.watch(0, side, endpoint * 8, 8, false);
    }
    for (auto& times : timings) std::sort(times.begin(), times.end());
    std::cout << "Offline callback 48000 Hz/256, 10 x 8ch endpoints (5 source + 5 destination): median mean without watches "
        << timings[0][3] << " us, watched " << timings[1][3] << " us, delta " << timings[1][3] - timings[0][3] << " us\n";
}

int main()
{
    holdChecks(); concurrentChecks(); engineChecks();
    std::cout << "Peak hold: " << checks << " checks passed. No audio device opened.\n";
}
