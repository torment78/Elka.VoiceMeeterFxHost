#pragma once

#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include <juce_audio_devices/juce_audio_devices.h>

#include "engine/AudioBufferView.h"

namespace elka
{
class RealtimeEngine;

class InsertAsioHost final : private juce::AudioIODeviceCallback
{
public:
    explicit InsertAsioHost(RealtimeEngine& engine);
    ~InsertAsioHost() override;

    InsertAsioHost(const InsertAsioHost&) = delete;
    InsertAsioHost& operator=(const InsertAsioHost&) = delete;

    bool probe(int expectedChannelCount, std::wstring& status);
    bool start(int requestedSampleRate, int requestedBlockSize, int expectedChannelCount, std::wstring& status);
    void stop(std::wstring& status) noexcept;
    void status(std::wstring& status) const;
    bool isRunning() const noexcept { return running.load(std::memory_order_acquire); }
    int currentSampleRate() const noexcept { return sampleRate.load(std::memory_order_acquire); }
    int currentBlockSize() const noexcept { return blockSize.load(std::memory_order_acquire); }

private:
    void audioDeviceIOCallbackWithContext(
        const float* const* inputChannelData,
        int numInputChannels,
        float* const* outputChannelData,
        int numOutputChannels,
        int numSamples,
        const juce::AudioIODeviceCallbackContext& context) override;
    void audioDeviceAboutToStart(juce::AudioIODevice* device) override;
    void audioDeviceStopped() override;
    void audioDeviceError(const juce::String& errorMessage) override;

    static std::wstring toWide(const juce::String& text);
    static bool isVoiceMeeterInsertDriverName(const juce::String& name);
    struct DriverCandidate
    {
        juce::String name;
        int inputChannels = 0;
        int outputChannels = 0;
    };

    std::vector<DriverCandidate> findInsertDriverCandidates(std::wstring& status);
    DriverCandidate selectInsertDriverCandidate(int expectedChannelCount, std::wstring& status);

    RealtimeEngine& realtimeEngine;
    mutable std::mutex deviceMutex;
    std::unique_ptr<juce::AudioIODeviceType> asioType;
    std::unique_ptr<juce::AudioIODevice> device;
    std::atomic<bool> running { false };
    std::atomic<int> sampleRate { 0 };
    std::atomic<int> blockSize { 0 };
    std::atomic<int> inputChannels { 0 };
    std::atomic<int> outputChannels { 0 };
    std::atomic<unsigned long long> callbackCount { 0 };
    std::atomic<int> peakPercent { 0 };
    std::wstring driverName;
    std::wstring lastError;
};
}
