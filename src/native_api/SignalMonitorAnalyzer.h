#pragma once

#include "engine/SignalMonitor.h"
#include <juce_dsp/juce_dsp.h>
#include <mutex>

namespace elka
{
// Owned and called only by the non-realtime monitor reader.
class SignalMonitorAnalyzer
{
public:
    static constexpr int Bins = 120;
    SignalMonitorHub::Snapshot snapshot;
    std::mutex readerMutex;
    uint64_t previousSequence = 0;

    void spectrum(float* destination)
    {
        std::fill_n(destination, Bins, -90.0f);
        const int samples = snapshot.sampleRate <= 96000 ? 2048 : SignalMonitorHub::Samples;
        if (snapshot.validSamples < samples || snapshot.sampleRate <= 0 || snapshot.channelCount <= 0) return;
        auto& fft = samples == 2048 ? shortFft : longFft;
        auto& window = samples == 2048 ? shortWindow : longWindow;
        power.fill(0);
        for (int c = 0; c < snapshot.channelCount; ++c)
        {
            fftData.fill(0);
            const auto& channel = snapshot.audio[static_cast<size_t>(c)];
            std::copy_n(channel.begin() + snapshot.validSamples - samples, samples, fftData.begin());
            window.multiplyWithWindowingTable(fftData.data(), samples);
            fft.performFrequencyOnlyForwardTransform(fftData.data());
            for (int i = 0; i <= samples / 2; ++i) power[static_cast<size_t>(i)] += fftData[static_cast<size_t>(i)] * fftData[static_cast<size_t>(i)];
        }
        const double highest = std::min(20000.0, snapshot.sampleRate * 0.5);
        for (int i = 0; i < Bins; ++i)
        {
            const double low = 20.0 * std::pow(1000.0, (i - 0.5) / (Bins - 1));
            const double high = 20.0 * std::pow(1000.0, (i + 0.5) / (Bins - 1));
            if (low > highest) continue;
            const int a = std::clamp(static_cast<int>(std::floor(low * samples / snapshot.sampleRate)), 1, samples / 2);
            const int b = std::clamp(static_cast<int>(std::ceil(high * samples / snapshot.sampleRate)), a, samples / 2);
            float maximum = 0;
            for (int bin = a; bin <= b; ++bin) maximum = std::max(maximum, power[static_cast<size_t>(bin)]);
            // JUCE's normalized Hann has unity average; average channel POWER, not samples (no phase cancellation).
            const float amplitude = 2.0f * std::sqrt(maximum / snapshot.channelCount) / samples;
            destination[i] = std::clamp(20.0f * std::log10(std::max(amplitude, 0.000001f)), -90.0f, 6.0f);
        }
    }

private:
    juce::dsp::FFT shortFft{11}, longFft{12};
    juce::dsp::WindowingFunction<float> shortWindow{2048, juce::dsp::WindowingFunction<float>::hann};
    juce::dsp::WindowingFunction<float> longWindow{SignalMonitorHub::Samples, juce::dsp::WindowingFunction<float>::hann};
    std::array<float, SignalMonitorHub::Samples * 2> fftData{};
    std::array<float, SignalMonitorHub::Samples / 2 + 1> power{};
};
}
