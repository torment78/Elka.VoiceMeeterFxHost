#include "native_api/SignalMonitorAnalyzer.h"
#include <chrono>
#include <cstring>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <thread>

using namespace elka;
int checks = 0;
void check(bool condition, const char* name)
{
    if (!condition) throw std::runtime_error(name);
    ++checks;
}

int main()
{
    try
    {
        SignalMonitorHub hub;
        auto snapshot = std::make_unique<SignalMonitorHub::Snapshot>();
        check(!hub.hasMonitors(), "No sessions at startup");
        hub.capture(0, false, nullptr, 0, 128, 48000);
        check(hub.open(3, false, 0, 2) == 0, "Reject invalid callback");
        check(hub.open(0, false, 63, 8) == 0, "Reject invalid channel range");
        const int raw = hub.open(0, false, 6, 2);
        const int processed = hub.open(0, true, 6, 2);
        const int bus = hub.open(1, false, 0, 8);
        check(raw && processed && bus && hub.hasMonitors(), "Independent source/destination/bus taps");
        std::array<float, 4096> left{}, right{};
        left.fill(0.5f); right.fill(-0.25f);
        std::array<float*, 8> pointers{};
        pointers[6] = left.data(); pointers[7] = right.data();
        hub.capture(0, false, pointers.data(), 8, 128, 48000);
        left.fill(0.1f); right.fill(0.2f);
        hub.capture(0, true, pointers.data(), 8, 128, 48000);
        check(hub.read(raw, *snapshot) && snapshot->peaks[0] == 0.5f && snapshot->peaks[1] == 0.25f, "Source survives aliased buffer processing");
        check(snapshot->audio[0][0] == 0.5f && snapshot->audio[1][0] == -0.25f, "Correct hardware input four channel pair");
        check(hub.read(processed, *snapshot) && snapshot->peaks[0] == 0.1f, "Destination sees processed samples");
        check(hub.read(processed, *snapshot) && snapshot->peaks[0] == 0, "Meter peaks reset after polling");
        check(hub.read(bus, *snapshot) && snapshot->sequence == 0, "Input callback cannot feed bus monitor");
        hub.capture(1, false, pointers.data(), 8, 128, 96000);
        check(hub.read(bus, *snapshot) && snapshot->sampleRate == 96000 && snapshot->peaks[6] == 0.1f, "Eight-channel bus indexing");
        check(snapshot->peaks[0] == 0, "Unused channels remain dark");
        hub.capture(0, false, nullptr, 0, 64, 192000);
        hub.read(raw, *snapshot);
        check(snapshot->sampleRate == 192000 && snapshot->validSamples == 64 && snapshot->peaks[0] == 0, "Rate changes clear old history");
        left[0] = std::numeric_limits<float>::quiet_NaN();
        hub.capture(0, false, pointers.data(), 8, 128, 192000);
        hub.read(raw, *snapshot);
        check(std::isfinite(snapshot->audio[0][64]) && std::isnan(left[0]), "Sanitize display without writing audio");
        hub.close(raw); hub.close(processed); hub.close(bus);
        check(!hub.hasMonitors() && !hub.read(raw, *snapshot), "Closing all taps releases sessions");

        const int tone = hub.open(0, false, 0, 2);
        pointers[0] = left.data(); pointers[1] = right.data();
        constexpr double pi = 3.14159265358979323846;
        for (int i = 0; i < 4096; ++i) { left[i] = static_cast<float>(0.5 * std::sin(2 * pi * 1000 * i / 48000)); right[i] = -left[i]; }
        hub.capture(0, false, pointers.data(), 2, 4096, 48000);
        auto analyzer = std::make_unique<SignalMonitorAnalyzer>();
        hub.read(tone, analyzer->snapshot);
        std::array<float, SignalMonitorAnalyzer::Bins> spectrum;
        analyzer->spectrum(spectrum.data());
        auto maximum = std::max_element(spectrum.begin(), spectrum.end());
        const auto bin = std::distance(spectrum.begin(), maximum);
        const double frequency = 20 * std::pow(1000, static_cast<double>(bin) / (spectrum.size() - 1));
        check(frequency > 900 && frequency < 1100, "FFT locates 1 kHz tone");
        check(*maximum > -9 && *maximum < -5, "Opposite-phase stereo cannot cancel spectrum");
        analyzer->snapshot.validSamples = 2048;
        analyzer->spectrum(spectrum.data());
        check(*std::max_element(spectrum.begin(), spectrum.end()) > -9, "48 kHz spectrum is ready after 2048 samples");
        analyzer->snapshot.validSamples = 4096;
        for (auto& channel : analyzer->snapshot.audio) std::fill(channel.begin() + 2048, channel.end(), 0.0f);
        analyzer->spectrum(spectrum.data());
        check(*std::max_element(spectrum.begin(), spectrum.end()) == -90, "Short FFT uses newest half of history, not stale audio");
        analyzer->snapshot.sampleRate = 192000;
        analyzer->snapshot.validSamples = 2048;
        analyzer->spectrum(spectrum.data());
        check(*std::max_element(spectrum.begin(), spectrum.end()) == -90, "High-rate FFT waits for 4096 samples");
        analyzer->snapshot.validSamples = 4096;
        for (int i = 0; i < 4096; ++i)
            analyzer->snapshot.audio[0][i] = analyzer->snapshot.audio[1][i] = static_cast<float>(0.5 * std::sin(2 * pi * 1000 * i / 192000));
        analyzer->spectrum(spectrum.data());
        maximum = std::max_element(spectrum.begin(), spectrum.end());
        const double highRateFrequency = 20 * std::pow(1000, static_cast<double>(std::distance(spectrum.begin(), maximum)) / (spectrum.size() - 1));
        check(highRateFrequency > 900 && highRateFrequency < 1100 && *maximum > -9, "High-rate FFT preserves tone frequency and level");
        for (int i = 0; i < 4096; ++i) left[i] = static_cast<float>(i);
        hub.capture(0, false, pointers.data(), 2, 4096, 48000);
        hub.capture(0, false, pointers.data(), 2, 128, 48000);
        hub.read(tone, *snapshot);
        check(snapshot->audio[0][0] == 128 && snapshot->audio[0][4095] == 127, "Ring snapshot is chronological after wrap");
        hub.close(tone);

        const auto originalLeft = left;
        const auto originalRight = right;
        const int untouched = hub.open(0, false, 0, 2);
        for (int i = 0; i < 1000; ++i)
        {
            hub.capture(0, false, pointers.data(), 2, 128, 192000);
            hub.read(untouched, analyzer->snapshot);
            analyzer->spectrum(spectrum.data());
        }
        check(std::memcmp(left.data(), originalLeft.data(), sizeof(left)) == 0 &&
              std::memcmp(right.data(), originalRight.data(), sizeof(right)) == 0,
              "1000 capture/read/FFT cycles leave source audio bit-for-bit unchanged");
        hub.close(untouched);

        std::atomic<bool> running{true};
        std::thread audio([&] { while (running.load()) hub.capture(0, false, pointers.data(), 2, 128, 192000); });
        for (int i = 0; i < 1000; ++i)
        {
            int id = hub.open(0, false, 0, 2);
            hub.read(id, *snapshot);
            hub.close(id);
        }
        running.store(false); audio.join();
        check(!hub.hasMonitors(), "1000 concurrent open/read/close cycles");
        std::array<int, SignalMonitorHub::Capacity> handles{};
        for (auto& id : handles) id = hub.open(0, false, 0, 8);
        check(hub.open(0, false, 0, 2) == 0, "Monitor count is bounded");
        for (int id : handles) hub.close(id);
        check(!hub.hasMonitors(), "Capacity test releases every session");
        std::cout << checks << " signal monitor checks passed. No VoiceMeeter connection was opened.\n";
        return 0;
    }
    catch (const std::exception& ex) { std::cerr << ex.what() << '\n'; return 1; }
}
