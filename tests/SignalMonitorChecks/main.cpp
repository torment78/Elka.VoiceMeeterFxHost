#include "native_api/SignalMonitorReaders.h"
#include <chrono>
#include <cstring>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <thread>

using namespace elka;
namespace elka
{
struct SignalMonitorTestAccess
{
    static void captureWhileReaderBusy(SignalMonitorHub& hub, int id, int stream, bool output,
        float* const* channels, int count, int frames, int rate)
    {
        for (auto& slot : hub.slots)
        {
            if (!slot.session || slot.session->id != id) continue;
            SignalMonitorHub::acquire(slot);
            hub.capture(stream, output, channels, count, frames, rate);
            slot.busy.clear(std::memory_order_release);
            return;
        }
        throw std::runtime_error("Test tap not found");
    }
};
}
int checks = 0;
void check(bool condition, const char* name)
{
    if (!condition) throw std::runtime_error(name);
    ++checks;
}

void readerAccessChecks()
{
    SignalMonitorReaders readers;
    SignalMonitorHub hub;
    std::array<float, 4096> left{}, right{};
    left.fill(0.25f); right.fill(-0.125f);
    std::array<float*, 2> pointers{left.data(), right.data()};
    std::array<float, 8> peaks{};
    std::array<float, SignalMonitorAnalyzer::Bins> spectrum{};
    int rate = 0;
    unsigned long long sequence = 0;
    const int source = readers.open(hub, 0, false, 0, 2);
    const int destination = readers.open(hub, 0, true, 0, 2);
    const int meter = readers.open(hub, 0, false, 0, 2);
    auto read = [&](int id, bool fft = false) {
        return readers.read(id, peaks.data(), 8, fft ? spectrum.data() : nullptr,
            fft ? SignalMonitorAnalyzer::Bins : 0, &rate, &sequence);
    };
    check(source > 0 && destination > 0 && meter > 0, "Reader registry opens independent views");
    check(read(source) == 1, "No capture is pending data, not silence");
    check(readers.read(source, nullptr, 8, nullptr, 0, &rate, &sequence) == -1,
        "Reader validates buffers");
    check(readers.read(source, peaks.data(), 8, spectrum.data(), 1, &rate, &sequence) == -1,
        "Reader validates spectrum size");
    for (int sampleRate : {48000, 96000, 192000})
    {
        hub.capture(0, false, pointers.data(), 2, 4096, sampleRate);
        check(read(source, true) == 0 && peaks[0] == 0.25f && peaks[1] == 0.125f && rate == sampleRate,
            "Spectrum reader preserves levels and format");
        check(read(source) == 1 && peaks[0] == 0.25f, "No-new-block read leaves caller levels intact");
        check(read(meter) == 0 && peaks[0] == 0.25f && peaks[1] == 0.125f,
            "Spectrum polling does not consume node meter peaks");
        check(read(destination) == 1, "Source polling cannot generate destination data");
        hub.capture(0, true, pointers.data(), 2, 128, sampleRate);
        check(read(destination) == 0 && peaks[0] == 0.25f, "Destination uses its own capture");
    }
    left.fill(0); right.fill(0);
    hub.capture(0, false, pointers.data(), 2, 128, 48000);
    check(read(source) == 0 && peaks[0] == 0 && peaks[1] == 0,
        "Real silence is reported, not replaced with held values");
    readers.close(source);
    check(read(source) == -1, "Closed handle cannot read");
    readers.detach(hub);
    check(!hub.hasMonitors() && read(destination) == -1 && read(meter) == -1,
        "Detach releases every tap before engine destruction");

    SignalMonitorHub replacement;
    const int newHandle = readers.open(replacement, 0, false, 0, 2);
    check(newHandle != source && newHandle != meter && newHandle != destination,
        "Public handles do not alias after engine recreation");
    readers.close(source);
    replacement.capture(0, false, pointers.data(), 2, 128, 48000);
    check(read(newHandle) == 0, "Late close of old handle cannot close replacement tap");
    check(readers.open(hub, 3, false, 0, 2) == 0 && !hub.hasMonitors(),
        "Invalid registry open does not leave an active tap");
    std::array<int, SignalMonitorHub::Capacity> handles{};
    for (auto& id : handles) id = readers.open(hub, 0, false, 0, 8);
    check(std::all_of(handles.begin(), handles.end(), [](int id) { return id > 0; }) &&
        readers.open(hub, 0, false, 0, 2) == 0, "Reader registry respects tap capacity");
    readers.detach(hub);
    replacement.capture(0, false, pointers.data(), 2, 128, 48000);
    check(!hub.hasMonitors() && read(newHandle) == 0, "Detach does not affect another hub");
    readers.detach(replacement);

    left.fill(0.25f); right.fill(-0.25f);
    const auto originalLeft = left;
    const auto originalRight = right;
    std::jthread audio([&](std::stop_token stop) {
        while (!stop.stop_requested())
        {
            hub.capture(0, false, pointers.data(), 2, 128, 48000);
            std::this_thread::yield();
        }
    });
    for (int cycle = 0; cycle < 100; ++cycle)
    {
        const int id = readers.open(hub, 0, false, 0, 2);
        check(id > 0, "Concurrent lifecycle test opens its tap");
        std::atomic<int> attempts{0}, failures{0};
        auto poll = [&](bool fft) {
            std::array<float, 8> values{};
            std::array<float, SignalMonitorAnalyzer::Bins> bins{};
            int sampleRate = 0;
            unsigned long long block = 0;
            for (;;)
            {
                int result = readers.read(id, values.data(), 8, fft ? bins.data() : nullptr,
                    fft ? SignalMonitorAnalyzer::Bins : 0, &sampleRate, &block);
                if (result < -1 || result > 1) failures.fetch_add(1);
                if (result == 0 && (values[0] != 0.25f || values[1] != 0.25f)) failures.fetch_add(1);
                attempts.fetch_add(1);
                if (result == -1) break;
            }
        };
        std::jthread first([&] { poll(true); });
        std::jthread second([&] { poll(false); });
        while (attempts.load() < 5) std::this_thread::yield();
        if (cycle % 2 == 0) readers.close(id); else readers.detach(hub);
        first.join(); second.join();
        check(failures.load() == 0 && !hub.hasMonitors(), "Concurrent read/FFT/close/detach is safe");
    }
    audio.request_stop(); audio.join();
    check(left == originalLeft && right == originalRight, "Reader concurrency never writes source audio");

    // Destroy the owning hub while another reader may still be processing its copied FFT snapshot.
    for (int cycle = 0; cycle < 50; ++cycle)
    {
        auto temporaryHub = std::make_unique<SignalMonitorHub>();
        const int id = readers.open(*temporaryHub, 0, false, 0, 2);
        temporaryHub->capture(0, false, pointers.data(), 2, 4096, 48000);
        std::atomic<int> result{2};
        std::jthread poll([&] { result.store(read(id, true)); });
        readers.detach(*temporaryHub);
        temporaryHub.reset();
        poll.join();
        check(result.load() == 0 || result.load() == -1, "Copied FFT survives host teardown or reports closed");
    }
}

void spectrumContinuityChecks()
{
    auto analyzer = std::make_unique<SignalMonitorAnalyzer>();
    analyzer->snapshot.sampleRate = 48000;
    analyzer->snapshot.channelCount = 2;
    analyzer->snapshot.validSamples = 2048;
    std::array<float, SignalMonitorAnalyzer::Bins> clean{}, discontinuous{};
    constexpr double pi = 3.14159265358979323846;
    auto fill = [&](int skippedSamples) {
        for (int i = 0; i < 2048; ++i)
        {
            int position = i + (i >= 1024 ? skippedSamples : 0);
            float value = static_cast<float>(0.25 * std::sin(2 * pi * 401 * position / 48000));
            analyzer->snapshot.audio[0][i] = analyzer->snapshot.audio[1][i] = value;
        }
    };
    fill(0);
    analyzer->spectrum(clean.data());
    fill(512);
    analyzer->spectrum(discontinuous.data());
    float cleanSide = -90, brokenSide = -90;
    for (int i = 0; i < SignalMonitorAnalyzer::Bins; ++i)
    {
        double frequency = 20 * std::pow(1000, i / double(SignalMonitorAnalyzer::Bins - 1));
        if (frequency < 650 || frequency > 10000) continue;
        cleanSide = std::max(cleanSide, clean[i]);
        brokenSide = std::max(brokenSide, discontinuous[i]);
    }
    check(brokenSide > cleanSide + 20, "FFT must reveal real waveform discontinuities, not suppress transients");
    std::cout << "401 Hz: uninterrupted side-band " << cleanSide << " dBFS; joined across 512 missing samples "
        << brokenSide << " dBFS. Signal peak is unchanged.\n";
}

void skippedSpectrumCopyChecks()
{
    SignalMonitorHub hub;
    const int id = hub.open(0, false, 0, 2);
    auto analyzer = std::make_unique<SignalMonitorAnalyzer>();
    std::array<float, 512> tone{};
    std::array<float*, 2> pointers{tone.data(), tone.data()};
    std::array<float, SignalMonitorAnalyzer::Bins> spectrum{};
    constexpr double pi = 3.14159265358979323846;
    float maximumSide = -90;
    for (int block = 0; block < 40; ++block)
    {
        for (int i = 0; i < 512; ++i)
            tone[i] = static_cast<float>(0.25 * std::sin(2 * pi * 401 * (block * 512 + i) / 48000));
        if (block == 20)
            SignalMonitorTestAccess::captureWhileReaderBusy(hub, id, 0, false, pointers.data(), 2, 512, 48000);
        else
            hub.capture(0, false, pointers.data(), 2, 512, 48000);
        hub.read(id, analyzer->snapshot);
        analyzer->spectrum(spectrum.data());
        if (block < 8) continue;
        for (int i = 0; i < SignalMonitorAnalyzer::Bins; ++i)
        {
            double frequency = 20 * std::pow(1000, i / double(SignalMonitorAnalyzer::Bins - 1));
            if (frequency >= 650 && frequency <= 10000)
                maximumSide = std::max(maximumSide, spectrum[i]);
        }
    }
    hub.close(id);
    std::cout << "Busy monitor-copy test, maximum 401 Hz side-band: " << maximumSide << " dBFS\n";
    check(maximumSide < -75, "Missed display copy must not create a false spectrum transient");
}

void spectrumRecoveryChecks()
{
    for (int rate : {48000, 96000, 192000})
    for (int frames : {128, 256, 512})
    for (bool output : {false, true})
    {
        SignalMonitorHub hub;
        const int id = hub.open(0, output, 0, 2);
        const int other = hub.open(0, output, 0, 2);
        auto analyzer = std::make_unique<SignalMonitorAnalyzer>();
        auto otherSnapshot = std::make_unique<SignalMonitorHub::Snapshot>();
        std::array<float, 512> tone{};
        std::array<float*, 2> pointers{tone.data(), tone.data()};
        std::array<float, SignalMonitorAnalyzer::Bins> spectrum{};
        uint64_t position = 0;
        auto capture = [&](bool missed = false, bool silent = false) {
            for (int i = 0; i < frames; ++i)
                tone[i] = silent ? 0 : static_cast<float>(0.25 * std::sin(
                    2 * 3.14159265358979323846 * 401 * (position + i) / rate));
            const auto unchanged = tone;
            if (missed)
                SignalMonitorTestAccess::captureWhileReaderBusy(hub, id, 0, output, pointers.data(), 2, frames, rate);
            else
                hub.capture(0, output, pointers.data(), 2, frames, rate);
            position += frames;
            check(tone == unchanged, "Continuity tracking never writes input samples");
        };
        auto read = [&] {
            check(hub.read(id, analyzer->snapshot), "Spectrum recovery tap exists");
            analyzer->spectrum(spectrum.data());
        };
        for (int block = 0; block < 4096 / frames; ++block) capture();
        read();
        check(*std::max_element(spectrum.begin(), spectrum.end()) > -20, "Initial spectrum becomes available");
        const auto lastValid = spectrum;
        capture(true);
        capture();
        read();
        check(analyzer->snapshot.validSamples == frames && analyzer->snapshot.endFrame == position,
            "Missed tap block resets only its contiguous FFT window");
        float expectedPeak = 0;
        for (int sample = 0; sample < frames; ++sample) expectedPeak = std::max(expectedPeak, std::abs(tone[sample]));
        check(analyzer->snapshot.peaks[0] == expectedPeak && spectrum == lastValid,
            "Gap refilling preserves fresh peaks and briefly retains last valid spectrum");
        check(hub.read(other, *otherSnapshot) && otherSnapshot->validSamples == 4096,
            "Busy source/destination tap cannot invalidate another tap");
        for (int block = 0; block < rate / frames / 5 + 4; ++block)
        {
            capture(true);
            capture();
            read();
        }
        check(*std::max_element(spectrum.begin(), spectrum.end()) == -90,
            "Repeated lost copies cannot freeze an old spectrum indefinitely");
        for (int block = 0; block < 4096 / frames; ++block) capture();
        read();
        check(*std::max_element(spectrum.begin(), spectrum.end()) > -20,
            "Spectrum resumes after contiguous capture recovers");
        for (int block = 0; block < 4096 / frames; ++block) capture(false, true);
        read();
        check(*std::max_element(spectrum.begin(), spectrum.end()) == -90 && analyzer->snapshot.peaks[0] == 0,
            "Actual silence is reported by both spectrum and peaks after recovery");
        hub.close(id); hub.close(other);
        check(!hub.hasMonitors(), "Spectrum recovery test releases all taps");
    }
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
        readerAccessChecks();
        spectrumContinuityChecks();
        skippedSpectrumCopyChecks();
        spectrumRecoveryChecks();
        std::cout << checks << " signal monitor checks passed. No VoiceMeeter connection was opened.\n";
        return 0;
    }
    catch (const std::exception& ex) { std::cerr << ex.what() << '\n'; return 1; }
}
