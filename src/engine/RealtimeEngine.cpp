#include "engine/RealtimeEngine.h"

#include <algorithm>
#include <cstring>
#include <memory>
#include <xmmintrin.h>
#include <windows.h>
#include <avrt.h>

#ifndef _MM_DENORMALS_ZERO_ON
#define _MM_DENORMALS_ZERO_ON 0x0040
#endif

namespace elka
{
namespace
{
double queryPerformanceFrequency() noexcept
{
    static const double frequency = []() noexcept {
        LARGE_INTEGER value {};
        QueryPerformanceFrequency(&value);
        return static_cast<double>(value.QuadPart);
    }();

    return frequency;
}

double elapsedMicroseconds(LARGE_INTEGER start, LARGE_INTEGER end) noexcept
{
    static const double frequency = queryPerformanceFrequency();
    return (static_cast<double>(end.QuadPart - start.QuadPart) * 1000000.0) / frequency;
}

constexpr int MaxCallbackChannels = 64;
constexpr int MaxCallbackReadChannels = 128;
constexpr int PingStatusIdle = 0;
constexpr int PingStatusArmed = 1;
constexpr int PingStatusWaiting = 2;
constexpr int PingStatusDetected = 3;
constexpr int PingStatusTimeout = 4;
constexpr int PingPulseSamples = 8;
constexpr float PingPulseAmplitude = 0.80f;
constexpr float PingDetectThreshold = 0.18f;
constexpr int DelaySmoothingMilliseconds = 8;
constexpr bool DiagnosticSkipAudioWrites = false;
constexpr bool DiagnosticZeroReturnAfterRawProbe = false;
constexpr bool RealtimeJitterDiagnosticsEnabled = false;
constexpr bool RealtimeAudioProbeDiagnosticsEnabled = false;
constexpr int PopProbeRawInput = 0;
constexpr int PopProbePostCopy = 1;
constexpr int PopProbePrePlugin = 2;
constexpr int PopProbePositions = 3;
constexpr int PopProbeChannelPair = 2;
constexpr float PopProbeDeltaThreshold = 0.025f;
constexpr float PopProbeRelativeDeltaRatio = 0.18f;
constexpr float PopProbeInnerSlopeMultiplier = 3.0f;
constexpr float PopProbePredictionThreshold = 0.0015f;
constexpr float PopProbePredictionRelativeRatio = 0.015f;
constexpr float PopProbeAverageSlopeMultiplier = 10.0f;
constexpr float PopProbeMinimumPeak = 0.003f;
constexpr int ResidualProbePostCopy = 0;
constexpr int ResidualProbePrePlugin = 1;
constexpr int ResidualProbeFinal = 2;
constexpr int ResidualProbePositions = 3;
constexpr float ResidualProbeThreshold = 0.0005f;

const float* sourceChannelPointer(AudioBufferView buffer, int readOffset, int channel) noexcept
{
    if (channel < 0 || channel >= MaxCallbackChannels)
        return nullptr;

    const int readChannel = readOffset + channel;
    if (buffer.read != nullptr &&
        readChannel >= 0 &&
        readChannel < buffer.inputChannels &&
        buffer.read[readChannel] != nullptr)
    {
        return buffer.read[readChannel];
    }

    if (buffer.write != nullptr &&
        channel >= 0 &&
        channel < buffer.outputChannels)
    {
        return buffer.write[channel];
    }

    return nullptr;
}

const float* currentChannelPointer(AudioBufferView buffer, int readOffset, int channel) noexcept
{
    if (channel < 0 || channel >= MaxCallbackChannels)
        return nullptr;

    if (buffer.write != nullptr &&
        channel < buffer.outputChannels &&
        buffer.write[channel] != nullptr)
    {
        return buffer.write[channel];
    }

    return sourceChannelPointer(buffer, readOffset, channel);
}

void markStereoPair(std::array<bool, MaxCallbackChannels>& active, int channel) noexcept
{
    if (channel < 0 || channel >= MaxCallbackChannels)
        return;

    const int base = channel - (channel % 2);
    active[static_cast<size_t>(base)] = true;
    if (base + 1 < MaxCallbackChannels)
        active[static_cast<size_t>(base + 1)] = true;
}

const float* readChannelPointer(AudioBufferView buffer, int channel) noexcept
{
    if (buffer.read == nullptr ||
        channel < 0 ||
        channel >= buffer.inputChannels ||
        channel >= MaxCallbackReadChannels)
    {
        return nullptr;
    }

    return buffer.read[channel];
}

const float* writeChannelPointer(AudioBufferView buffer, int channel) noexcept
{
    if (buffer.write == nullptr ||
        channel < 0 ||
        channel >= buffer.outputChannels ||
        channel >= MaxCallbackChannels)
    {
        return nullptr;
    }

    return buffer.write[channel];
}

int peakPercent(const float* samples, int sampleCount) noexcept
{
    if (samples == nullptr || sampleCount <= 0)
        return 0;

    float peak = 0.0f;
    for (int sample = 0; sample < sampleCount; ++sample)
    {
        const float value = samples[sample] < 0.0f ? -samples[sample] : samples[sample];
        if (value > peak)
            peak = value;
    }

    return std::clamp(static_cast<int>((peak * 100.0f) + 0.5f), 0, 1000);
}

struct ProbePeak
{
    int channel = -1;
    int percent = 0;
};

ProbePeak maxReadPeak(AudioBufferView buffer, int startChannel, int channelCount, int reportOffset) noexcept
{
    ProbePeak result {};
    const int first = std::clamp(startChannel, 0, MaxCallbackReadChannels);
    const int last = std::clamp(first + std::max(0, channelCount), first, std::min(buffer.inputChannels, MaxCallbackReadChannels));

    for (int channel = first; channel < last; ++channel)
    {
        const int peak = peakPercent(readChannelPointer(buffer, channel), buffer.samplesPerFrame);
        if (peak <= result.percent)
            continue;

        result.percent = peak;
        result.channel = channel - reportOffset;
    }

    return result;
}

ProbePeak maxWritePeak(AudioBufferView buffer, int channelCount) noexcept
{
    ProbePeak result {};
    const int last = std::clamp(channelCount, 0, std::min(buffer.outputChannels, MaxCallbackChannels));

    for (int channel = 0; channel < last; ++channel)
    {
        const int peak = peakPercent(writeChannelPointer(buffer, channel), buffer.samplesPerFrame);
        if (peak <= result.percent)
            continue;

        result.percent = peak;
        result.channel = channel;
    }

    return result;
}

int delayStreamIndex(CallbackStreamKind kind) noexcept
{
    if (kind == CallbackStreamKind::InputInsert)
        return 0;

    if (kind == CallbackStreamKind::OutputInsert)
        return 1;

    return 2;
}

float gainFromPercent(int percent) noexcept
{
    return static_cast<float>(std::clamp(percent, 0, 800)) / 100.0f;
}

int capturedInputRouteSafetySamples(AudioBufferView buffer) noexcept
{
    const int blockSamples = std::max(0, buffer.samplesPerFrame);
    if (blockSamples <= 0)
        return 0;

    const int sampleRate = std::max(0, buffer.sampleRate);
    int safetyBlocks = 0;
    if (sampleRate >= 176400 || blockSamples <= 128)
        safetyBlocks = 8;
    else if (sampleRate >= 96000 || blockSamples <= 256)
        safetyBlocks = 6;

    return blockSamples * safetyBlocks;
}

int moveToward(int current, int target, int maximumStep) noexcept
{
    const int safeStep = std::max(1, maximumStep);
    if (current < target)
        return std::min(target, current + safeStep);

    if (current > target)
        return std::max(target, current - safeStep);

    return current;
}

int delaySmoothingStepSamples(int sampleRate, int blockSize) noexcept
{
    const int fromMilliseconds = sampleRate > 0
        ? (sampleRate * DelaySmoothingMilliseconds) / 1000
        : 0;
    return std::max(1, std::max(blockSize, fromMilliseconds));
}

size_t lineBufferOffset(int lineIndex, int length) noexcept
{
    return static_cast<size_t>(std::max(0, lineIndex)) *
        static_cast<size_t>(std::max(0, length));
}

int advanceRingPosition(int position, int amount, int length) noexcept
{
    if (length <= 0)
        return 0;

    int next = position + (amount % length);
    while (next >= length)
        next -= length;
    while (next < 0)
        next += length;
    return next;
}

size_t pluginBusFlatIndex(int slot, int pin) noexcept
{
    return static_cast<size_t>((slot * RealtimeEngine::MaxPluginPins) + pin);
}

void ensureDenormalFlush() noexcept
{
    thread_local bool configured = []() noexcept {
        const unsigned int control = _mm_getcsr();
        _mm_setcsr(control | _MM_FLUSH_ZERO_ON | _MM_DENORMALS_ZERO_ON);
        return true;
    }();
    (void)configured;
}

void ensureRealtimeThreadPriority() noexcept
{
    thread_local bool configured = []() noexcept {
        DWORD taskIndex = 0;
        HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
        if (mmcss == nullptr)
            mmcss = AvSetMmThreadCharacteristicsW(L"Audio", &taskIndex);

        if (mmcss != nullptr)
            AvSetMmThreadPriority(mmcss, AVRT_PRIORITY_HIGH);

        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
        return true;
    }();
    (void)configured;
}
}

RealtimeEngine::RealtimeEngine() noexcept
{
    for (int channel = 0; channel < MaxChannels; ++channel)
    {
        inputGainPercent[channel].store(100, std::memory_order_relaxed);
        outputGainPercent[channel].store(100, std::memory_order_relaxed);
        inputDelayMilliseconds[channel].store(0, std::memory_order_relaxed);
        outputDelayMilliseconds[channel].store(0, std::memory_order_relaxed);
        inputEnabled[channel].store(true, std::memory_order_relaxed);
        outputEnabled[channel].store(true, std::memory_order_relaxed);
        inputPluginGraphEnabled[channel].store(false, std::memory_order_relaxed);
        outputPluginGraphEnabled[channel].store(false, std::memory_order_relaxed);
        mainPluginGraphEnabled[channel].store(false, std::memory_order_relaxed);
        for (auto& positions : delayWritePositions)
            positions[channel] = 0;
        for (auto& gains : smoothedChannelGains)
            gains[channel] = 1.0f;
        for (auto& delays : smoothedDelaySamples)
            delays[channel] = 0;
        for (auto& initialized : smoothedDelayInitialized)
            initialized[channel] = false;
    }

    for (int route = 0; route < MaxDirectRoutes; ++route)
    {
        inputDirectRoutes.gainPercent[route].store(100, std::memory_order_relaxed);
        outputDirectRoutes.gainPercent[route].store(100, std::memory_order_relaxed);
        mainDirectRoutes.gainPercent[route].store(100, std::memory_order_relaxed);
        routeReadPositions[route].store(0, std::memory_order_relaxed);
        for (auto& positions : routeWritePositions)
            positions[route].store(0, std::memory_order_relaxed);
        for (auto& primedSamples : routePrimedSamples)
            primedSamples[route].store(0, std::memory_order_relaxed);
        routeCounts[route] = 0;
        for (auto& gains : smoothedRouteGains)
            gains[route] = 1.0f;
    }

    rebuildDynamicPluginScratchBuffers();
    refreshRealtimeActivityCounters();
}

void RealtimeEngine::setEnabled(bool shouldEnable) noexcept
{
    const auto kind = static_cast<CallbackStreamKind>(selectedKind.load(std::memory_order_acquire));
    auto& enabledBank = enableBankFor(kind);
    const int start = targetStart.load(std::memory_order_acquire);
    const int count = getSelectedChannelCount();

    for (int channel = start; channel < start + count; ++channel)
        enabledBank[channel].store(shouldEnable, std::memory_order_release);

    refreshRealtimeActivityCounters();
    refreshDynamicBuffersForCurrentSampleRate();
}

bool RealtimeEngine::isEnabled() const noexcept
{
    const auto kind = static_cast<CallbackStreamKind>(selectedKind.load(std::memory_order_acquire));
    const auto& enabledBank = enableBankFor(kind);
    const int start = targetStart.load(std::memory_order_acquire);
    const int count = getSelectedChannelCount();

    for (int channel = start; channel < start + count; ++channel)
    {
        if (!enabledBank[channel].load(std::memory_order_acquire))
            return false;
    }

    return true;
}

void RealtimeEngine::setGainPercent(int percent) noexcept
{
    const int clamped = std::clamp(percent, 0, 200);
    const auto kind = static_cast<CallbackStreamKind>(selectedKind.load(std::memory_order_acquire));
    auto& gainBank = gainBankFor(kind);
    const int start = targetStart.load(std::memory_order_acquire);
    const int count = getSelectedChannelCount();
    bool changed = false;

    for (int channel = start; channel < start + count; ++channel)
    {
        auto& gain = gainBank[channel];
        if (gain.load(std::memory_order_acquire) != clamped)
        {
            gain.store(clamped, std::memory_order_release);
            changed = true;
        }
    }

    if (changed)
        refreshRealtimeActivityCounters();
}

int RealtimeEngine::getGainPercent() const noexcept
{
    const auto kind = static_cast<CallbackStreamKind>(selectedKind.load(std::memory_order_acquire));
    const auto& gainBank = gainBankFor(kind);
    const int start = targetStart.load(std::memory_order_acquire);
    return gainBank[start].load(std::memory_order_acquire);
}

void RealtimeEngine::setChannelEnabled(CallbackStreamKind kind, int channel, bool shouldEnable) noexcept
{
    if (channel < 0 || channel >= MaxChannels)
        return;

    auto& enabled = enableBankFor(kind)[channel];
    if (enabled.load(std::memory_order_acquire) == shouldEnable)
        return;

    enabled.store(shouldEnable, std::memory_order_release);
    refreshRealtimeActivityCounters();
    refreshDynamicBuffersForCurrentSampleRate();
}

bool RealtimeEngine::isChannelEnabled(CallbackStreamKind kind, int channel) const noexcept
{
    if (channel < 0 || channel >= MaxChannels)
        return false;

    return enableBankFor(kind)[channel].load(std::memory_order_acquire);
}

void RealtimeEngine::setChannelGainPercent(CallbackStreamKind kind, int channel, int percent) noexcept
{
    if (channel < 0 || channel >= MaxChannels)
        return;

    const int clamped = std::clamp(percent, 0, 200);
    auto& gain = gainBankFor(kind)[channel];
    if (gain.load(std::memory_order_acquire) == clamped)
        return;

    gain.store(clamped, std::memory_order_release);
    refreshRealtimeActivityCounters();
}

int RealtimeEngine::getChannelGainPercent(CallbackStreamKind kind, int channel) const noexcept
{
    if (channel < 0 || channel >= MaxChannels)
        return 100;

    return gainBankFor(kind)[channel].load(std::memory_order_acquire);
}

void RealtimeEngine::setChannelDelayMilliseconds(CallbackStreamKind kind, int channel, int milliseconds) noexcept
{
    if (channel < 0 || channel >= MaxChannels)
        return;

    const int clamped = std::clamp(milliseconds, 0, MaxDelayMilliseconds);
    auto& delay = delayBankFor(kind)[channel];
    if (delay.load(std::memory_order_acquire) == clamped)
        return;

    delay.store(clamped, std::memory_order_release);
    refreshRealtimeActivityCounters();
    refreshDynamicBuffersForCurrentSampleRate();
}

int RealtimeEngine::getChannelDelayMilliseconds(CallbackStreamKind kind, int channel) const noexcept
{
    if (channel < 0 || channel >= MaxChannels)
        return 0;

    return delayBankFor(kind)[channel].load(std::memory_order_acquire);
}

void RealtimeEngine::setChannelPluginGraphEnabled(CallbackStreamKind kind, int channel, bool shouldEnable) noexcept
{
    if (channel < 0 || channel >= MaxChannels)
        return;

    auto& enabled = pluginGraphBankFor(kind)[channel];
    if (enabled.load(std::memory_order_acquire) == shouldEnable)
        return;

    enabled.store(shouldEnable, std::memory_order_release);
    refreshRealtimeActivityCounters();
}

bool RealtimeEngine::isChannelPluginGraphEnabled(CallbackStreamKind kind, int channel) const noexcept
{
    if (channel < 0 || channel >= MaxChannels)
        return false;

    return pluginGraphBankFor(kind)[channel].load(std::memory_order_acquire);
}

void RealtimeEngine::setInputCallbackSuppressedChannel(int channel, bool shouldSuppress) noexcept
{
    if (channel < 0 || channel >= MaxChannels)
        return;

    auto& suppressed = inputCallbackSuppressed[static_cast<size_t>(channel)];
    const bool wasSuppressed = suppressed.exchange(shouldSuppress, std::memory_order_acq_rel);
    if (wasSuppressed != shouldSuppress)
        suppressedInputChannelCount.fetch_add(shouldSuppress ? 1 : -1, std::memory_order_acq_rel);
}

bool RealtimeEngine::prepareDelayBuffers(int requestedSampleRate) noexcept
{
    return rebuildDynamicBuffers(requestedSampleRate);
}

bool RealtimeEngine::rebuildDynamicBuffers(int requestedSampleRate) noexcept
{
    const int preparedSampleRate = std::clamp(requestedSampleRate, 8000, 192000);
    const int requiredLength = static_cast<int>(
        ((static_cast<int64_t>(preparedSampleRate) * MaxDelayMilliseconds) / 1000) + 1);

    std::shared_ptr<DynamicAudioBuffers> next;
    try
    {
        next = std::make_shared<DynamicAudioBuffers>();
    }
    catch (...)
    {
        return false;
    }

    next->sampleRate = preparedSampleRate;

    for (auto& indexes : next->delayLineIndexes)
        indexes.fill(-1);
    for (auto& indexes : next->routeLineIndexes)
        indexes.fill(-1);

    const auto addDelayLine = [&](int stream, int channel) noexcept {
        if (stream < 0 || stream >= DelayStreamCount || channel < 0 || channel >= MaxChannels)
            return;

        next->delayLineIndexes[static_cast<size_t>(stream)][static_cast<size_t>(channel)] =
            next->delayLineCount++;
    };

    for (int channel = 0; channel < MaxChannels; ++channel)
    {
        const bool inputDelayActive =
            inputEnabled[static_cast<size_t>(channel)].load(std::memory_order_acquire) &&
            inputDelayMilliseconds[static_cast<size_t>(channel)].load(std::memory_order_acquire) > 0;
        if (inputDelayActive)
        {
            addDelayLine(0, channel);
            addDelayLine(3, channel);
        }

        const bool outputDelayActive =
            outputEnabled[static_cast<size_t>(channel)].load(std::memory_order_acquire) &&
            outputDelayMilliseconds[static_cast<size_t>(channel)].load(std::memory_order_acquire) > 0;
        if (outputDelayActive)
        {
            addDelayLine(1, channel);
            addDelayLine(2, channel);
        }
    }

    bool routeDelayActive = false;
    const auto addRouteLine = [&](int stream, int route, bool needsDelayLine) noexcept {
        if (stream < 0 || stream >= DelayStreamCount || route < 0 || route >= MaxDirectRoutes)
            return;

        next->routeLineIndexes[static_cast<size_t>(stream)][static_cast<size_t>(route)] =
            next->routeLineCount++;
        routeDelayActive = routeDelayActive || needsDelayLine;
    };

    const int inputRouteCount = std::clamp(inputDirectRoutes.routeCount.load(std::memory_order_acquire), 0, MaxDirectRoutes);
    for (int route = 0; route < inputRouteCount; ++route)
    {
        const int source = inputDirectRoutes.sourceChannels[static_cast<size_t>(route)].load(std::memory_order_acquire);
        const int destination = inputDirectRoutes.destinationChannels[static_cast<size_t>(route)].load(std::memory_order_acquire);
        if (source < 0 || source >= MaxChannels || destination < 0 || destination >= MaxChannels)
            continue;

        const int delayMs = inputDirectRoutes.delayMilliseconds[static_cast<size_t>(route)].load(std::memory_order_acquire);
        addRouteLine(0, route, delayMs > 0);
    }

    const int outputRouteCount = std::clamp(outputDirectRoutes.routeCount.load(std::memory_order_acquire), 0, MaxDirectRoutes);
    for (int route = 0; route < outputRouteCount; ++route)
    {
        const int source = outputDirectRoutes.sourceChannels[static_cast<size_t>(route)].load(std::memory_order_acquire);
        const int destination = outputDirectRoutes.destinationChannels[static_cast<size_t>(route)].load(std::memory_order_acquire);
        const int delayMs = outputDirectRoutes.delayMilliseconds[static_cast<size_t>(route)].load(std::memory_order_acquire);
        if (delayMs <= 0 || source < 0 || source >= MaxChannels || destination < 0 || destination >= MaxChannels)
            continue;

        addRouteLine(1, route, true);
    }

    const int mainRouteCount = std::clamp(mainDirectRoutes.routeCount.load(std::memory_order_acquire), 0, MaxDirectRoutes);
    for (int route = 0; route < mainRouteCount; ++route)
    {
        const int source = mainDirectRoutes.sourceChannels[static_cast<size_t>(route)].load(std::memory_order_acquire);
        const int destination = mainDirectRoutes.destinationChannels[static_cast<size_t>(route)].load(std::memory_order_acquire);
        const int delayMs = mainDirectRoutes.delayMilliseconds[static_cast<size_t>(route)].load(std::memory_order_acquire);
        if (delayMs <= 0 || source < 0 || source >= MaxChannels || destination < 0 || destination >= MaxChannels)
            continue;

        addRouteLine(2, route, true);
    }

    next->delayLength = next->delayLineCount > 0 ? requiredLength : 0;
    next->routeLength = next->routeLineCount > 0
        ? (routeDelayActive ? std::max(requiredLength, MaxPluginScratchSamples) : MaxPluginScratchSamples)
        : 0;

    const auto current = dynamicBuffers.load(std::memory_order_acquire);
    if (current != nullptr &&
        current->sampleRate == next->sampleRate &&
        current->delayLength == next->delayLength &&
        current->routeLength == next->routeLength &&
        current->delayLineCount == next->delayLineCount &&
        current->routeLineCount == next->routeLineCount &&
        current->delayLineIndexes == next->delayLineIndexes &&
        current->routeLineIndexes == next->routeLineIndexes)
    {
        delayBufferSampleRate.store(preparedSampleRate, std::memory_order_release);
        delayBufferLength.store(next->delayLength, std::memory_order_release);
        routeBufferLength.store(next->routeLength, std::memory_order_release);
        delayBufferLineCount.store(next->delayLineCount, std::memory_order_release);
        routeBufferLineCount.store(next->routeLineCount, std::memory_order_release);
        return true;
    }

    try
    {
        if (next->delayLineCount > 0 && next->delayLength > 0)
        {
            next->delayBuffer.assign(
                static_cast<size_t>(next->delayLineCount) *
                    static_cast<size_t>(next->delayLength),
                0.0f);
        }

        if (next->routeLineCount > 0 && next->routeLength > 0)
        {
            next->routeBuffer.assign(
                static_cast<size_t>(next->routeLineCount) *
                    static_cast<size_t>(next->routeLength),
                0.0f);
        }
    }
    catch (...)
    {
        return false;
    }

    for (auto& positions : delayWritePositions)
    {
        for (auto& writePosition : positions)
            writePosition = 0;
    }

    for (auto& delays : smoothedDelaySamples)
    {
        for (auto& delay : delays)
            delay = 0;
    }

    for (auto& initialized : smoothedDelayInitialized)
    {
        for (auto&& state : initialized)
            state = false;
    }

    for (int route = 0; route < MaxDirectRoutes; ++route)
    {
        routeReadPositions[route].store(0, std::memory_order_relaxed);
        for (auto& positions : routeWritePositions)
            positions[route].store(0, std::memory_order_relaxed);
        for (auto& primedSamples : routePrimedSamples)
            primedSamples[route].store(0, std::memory_order_relaxed);
        routeCounts[route] = 0;
    }

    dynamicBuffers.store(next, std::memory_order_release);
    delayBufferLength.store(next->delayLength, std::memory_order_release);
    delayBufferSampleRate.store(preparedSampleRate, std::memory_order_release);
    routeBufferLength.store(next->routeLength, std::memory_order_release);
    delayBufferLineCount.store(next->delayLineCount, std::memory_order_release);
    routeBufferLineCount.store(next->routeLineCount, std::memory_order_release);
    return true;
}

void RealtimeEngine::refreshDynamicBuffersForCurrentSampleRate() noexcept
{
    int preparedSampleRate = delayBufferSampleRate.load(std::memory_order_acquire);
    if (preparedSampleRate <= 0)
        preparedSampleRate = sampleRate.load(std::memory_order_acquire);

    if (preparedSampleRate > 0)
        rebuildDynamicBuffers(preparedSampleRate);
}

bool RealtimeEngine::rebuildDynamicPluginScratchBuffers() noexcept
{
    std::shared_ptr<DynamicPluginScratchBuffers> next;
    try
    {
        next = std::make_shared<DynamicPluginScratchBuffers>();
    }
    catch (...)
    {
        return false;
    }

    next->pluginBusLineIndexes.fill(-1);
    next->passthroughRouteCapacities.fill(0);
    next->passthroughRouteStartLines.fill(-1);

    const auto markPluginBusLine = [&](int slot, int pin) noexcept {
        if (slot < 0 || slot >= MaxPluginSlots || pin < 0 || pin >= MaxPluginPins)
            return;

        const auto index = pluginBusFlatIndex(slot, pin);
        if (index >= next->pluginBusLineIndexes.size() ||
            next->pluginBusLineIndexes[index] >= 0)
        {
            return;
        }

        next->pluginBusLineIndexes[index] = next->pluginBusLineCount++;
    };

    for (int slot = 0; slot < MaxPluginSlots; ++slot)
    {
        const auto& pluginSlot = pluginSlots[static_cast<size_t>(slot)];
        const bool active =
            pluginSlot.enabled.load(std::memory_order_acquire) &&
            pluginSlot.processor.load(std::memory_order_acquire) != nullptr;
        if (!active)
            continue;

        const int inputRouteCount = std::clamp(pluginSlot.inputRouteCount.load(std::memory_order_acquire), 0, MaxPluginRoutes);
        for (int route = 0; route < inputRouteCount; ++route)
        {
            const int sourceKind = pluginSlot.inputSourceKinds[static_cast<size_t>(route)].load(std::memory_order_acquire);
            if (sourceKind != static_cast<int>(PluginRouteEndpointKind::PluginPin))
                continue;

            const int sourceSlot = pluginSlot.inputSourceSlots[static_cast<size_t>(route)].load(std::memory_order_acquire);
            const int sourcePin = pluginSlot.inputSourcePins[static_cast<size_t>(route)].load(std::memory_order_acquire);
            markPluginBusLine(sourceSlot, sourcePin);
        }

        const int outputRouteCount = std::clamp(pluginSlot.outputRouteCount.load(std::memory_order_acquire), 0, MaxPluginRoutes);
        for (int route = 0; route < outputRouteCount; ++route)
        {
            const int destinationKind = pluginSlot.outputDestinationKinds[static_cast<size_t>(route)].load(std::memory_order_acquire);
            if (destinationKind != static_cast<int>(PluginRouteEndpointKind::PluginPin))
                continue;

            const int pin = pluginSlot.outputPluginPins[static_cast<size_t>(route)].load(std::memory_order_acquire);
            markPluginBusLine(slot, pin);
        }
    }

    const auto passthroughRouteCapacity = [](const DirectRouteBank& bank) noexcept {
        return std::clamp(bank.routeCount.load(std::memory_order_acquire), 0, MaxDirectRoutes);
    };

    next->passthroughRouteCapacities[0] = passthroughRouteCapacity(inputPluginPassthroughRoutes);
    next->passthroughRouteCapacities[1] = passthroughRouteCapacity(outputPluginPassthroughRoutes);
    next->passthroughRouteCapacities[2] = passthroughRouteCapacity(mainPluginPassthroughRoutes);
    next->passthroughRouteCapacities[3] = next->passthroughRouteCapacities[0];

    for (int stream = 0; stream < DelayStreamCount; ++stream)
    {
        const int capacity = next->passthroughRouteCapacities[static_cast<size_t>(stream)];
        if (capacity <= 0)
            continue;

        next->passthroughRouteStartLines[static_cast<size_t>(stream)] = next->passthroughLineCount;
        next->passthroughLineCount += capacity;
    }

    const auto current = dynamicPluginScratchBuffers.load(std::memory_order_acquire);
    if (current != nullptr &&
        current->pluginBusLineCount == next->pluginBusLineCount &&
        current->passthroughLineCount == next->passthroughLineCount &&
        current->pluginBusLineIndexes == next->pluginBusLineIndexes &&
        current->passthroughRouteCapacities == next->passthroughRouteCapacities &&
        current->passthroughRouteStartLines == next->passthroughRouteStartLines)
    {
        return true;
    }

    try
    {
        if (next->pluginBusLineCount > 0)
        {
            next->pluginBusBuffer.assign(
                static_cast<size_t>(next->pluginBusLineCount) *
                    static_cast<size_t>(MaxPluginScratchSamples),
                0.0f);
        }

        if (next->passthroughLineCount > 0)
        {
            next->pluginPassthroughScratchBuffer.assign(
                static_cast<size_t>(next->passthroughLineCount) *
                    static_cast<size_t>(MaxPluginScratchSamples),
                0.0f);
        }
    }
    catch (...)
    {
        return false;
    }

    dynamicPluginScratchBuffers.store(next, std::memory_order_release);
    return true;
}

void RealtimeEngine::refreshDynamicPluginScratchBuffers() noexcept
{
    rebuildDynamicPluginScratchBuffers();
}

int RealtimeEngine::getDelayBufferSampleRate() const noexcept
{
    return delayBufferSampleRate.load(std::memory_order_acquire);
}

void RealtimeEngine::setTargetRange(CallbackStreamKind kind, int startChannel, int channelCount) noexcept
{
    const int start = std::clamp(startChannel, 0, MaxChannels - 1);
    selectedKind.store(static_cast<int>(kind), std::memory_order_release);
    targetStart.store(start, std::memory_order_release);
    targetCount.store(clampChannelCount(start, channelCount), std::memory_order_release);
}

void RealtimeEngine::setDirectRoutes(CallbackStreamKind kind, const DirectAudioRoute* routes, int routeCount) noexcept
{
    auto& bank = directRouteBankFor(kind);
    const int previousCount = std::clamp(bank.routeCount.load(std::memory_order_acquire), 0, MaxDirectRoutes);
    const int safeCount = routes != nullptr ? std::clamp(routeCount, 0, MaxDirectRoutes) : 0;
    const int streamIndex = delayStreamIndex(kind);

    bool unchanged = previousCount == safeCount;
    for (int route = 0; unchanged && route < safeCount; ++route)
    {
        const int newDelay = std::clamp(routes[route].delayMilliseconds, 0, MaxDelayMilliseconds);
        const int newGain = std::clamp(routes[route].gainPercent, 0, 800);
        unchanged =
            bank.sourceChannels[static_cast<size_t>(route)].load(std::memory_order_acquire) == routes[route].sourceChannel &&
            bank.destinationChannels[static_cast<size_t>(route)].load(std::memory_order_acquire) == routes[route].destinationChannel &&
            bank.delayMilliseconds[static_cast<size_t>(route)].load(std::memory_order_acquire) == newDelay &&
            bank.gainPercent[static_cast<size_t>(route)].load(std::memory_order_acquire) == newGain &&
            bank.muteSource[static_cast<size_t>(route)].load(std::memory_order_acquire) == routes[route].muteSource;
    }

    if (unchanged)
        return;

    for (int route = 0; route < safeCount; ++route)
    {
        const int oldSource = route < previousCount
            ? bank.sourceChannels[static_cast<size_t>(route)].load(std::memory_order_relaxed)
            : -1;
        const int oldDestination = route < previousCount
            ? bank.destinationChannels[static_cast<size_t>(route)].load(std::memory_order_relaxed)
            : -1;
        const int oldDelay = route < previousCount
            ? bank.delayMilliseconds[static_cast<size_t>(route)].load(std::memory_order_relaxed)
            : 0;
        const int newDelay = std::clamp(routes[route].delayMilliseconds, 0, MaxDelayMilliseconds);
        const int newGain = std::clamp(routes[route].gainPercent, 0, 800);
        const bool routePathChanged =
            route >= previousCount ||
            oldSource != routes[route].sourceChannel ||
            oldDestination != routes[route].destinationChannel;
        const bool delayLineBecameActive = oldDelay <= 0 && newDelay > 0;

        if (routePathChanged || delayLineBecameActive)
        {
            routeReadPositions[static_cast<size_t>(route)].store(0, std::memory_order_relaxed);
            routeCounts[route] = 0;
            routeWritePositions[static_cast<size_t>(streamIndex)][static_cast<size_t>(route)].store(0, std::memory_order_relaxed);
            routePrimedSamples[static_cast<size_t>(streamIndex)][static_cast<size_t>(route)].store(0, std::memory_order_relaxed);
            smoothedRouteGains[static_cast<size_t>(streamIndex)][static_cast<size_t>(route)] = gainFromPercent(newGain);
        }

        bank.sourceChannels[static_cast<size_t>(route)].store(routes[route].sourceChannel, std::memory_order_relaxed);
        bank.destinationChannels[static_cast<size_t>(route)].store(routes[route].destinationChannel, std::memory_order_relaxed);
        bank.delayMilliseconds[static_cast<size_t>(route)].store(newDelay, std::memory_order_relaxed);
        bank.gainPercent[static_cast<size_t>(route)].store(newGain, std::memory_order_relaxed);
        bank.muteSource[static_cast<size_t>(route)].store(routes[route].muteSource, std::memory_order_relaxed);
    }

    bank.routeCount.store(safeCount, std::memory_order_release);
    refreshRealtimeActivityCounters();
    refreshDynamicBuffersForCurrentSampleRate();
}

void RealtimeEngine::setPluginPassthroughRoutes(CallbackStreamKind kind, const DirectAudioRoute* routes, int routeCount) noexcept
{
    auto& bank = pluginPassthroughBankFor(kind);
    const int previousCount = std::clamp(bank.routeCount.load(std::memory_order_acquire), 0, MaxDirectRoutes);
    const int safeCount = routes != nullptr ? std::clamp(routeCount, 0, MaxDirectRoutes) : 0;

    bool unchanged = previousCount == safeCount;
    for (int route = 0; unchanged && route < safeCount; ++route)
    {
        unchanged =
            bank.sourceChannels[static_cast<size_t>(route)].load(std::memory_order_acquire) == routes[route].sourceChannel &&
            bank.destinationChannels[static_cast<size_t>(route)].load(std::memory_order_acquire) == routes[route].destinationChannel;
    }

    if (unchanged)
        return;

    for (int route = 0; route < safeCount; ++route)
    {
        bank.sourceChannels[static_cast<size_t>(route)].store(routes[route].sourceChannel, std::memory_order_relaxed);
        bank.destinationChannels[static_cast<size_t>(route)].store(routes[route].destinationChannel, std::memory_order_relaxed);
    }

    bank.routeCount.store(safeCount, std::memory_order_release);
    refreshDynamicPluginScratchBuffers();
}

bool RealtimeEngine::startSinglePing(int inputChannel, int outputChannel, int timeoutMilliseconds) noexcept
{
    if (inputChannel < 0 ||
        inputChannel >= MaxChannels ||
        outputChannel < 0 ||
        outputChannel >= MaxChannels)
    {
        return false;
    }

    const int safeTimeoutMilliseconds = std::clamp(timeoutMilliseconds, 100, MaxDelayMilliseconds + 2000);
    const int currentSampleRate = std::max(8000, sampleRate.load(std::memory_order_acquire));
    singlePingInputChannel.store(inputChannel, std::memory_order_relaxed);
    singlePingOutputChannel.store(outputChannel, std::memory_order_relaxed);
    singlePingSampleRate.store(currentSampleRate, std::memory_order_relaxed);
    singlePingLatencySamples.store(-1, std::memory_order_relaxed);
    singlePingElapsedSamples.store(0, std::memory_order_relaxed);
    singlePingPeakPercent.store(0, std::memory_order_relaxed);
    singlePingTimeoutMilliseconds.store(safeTimeoutMilliseconds, std::memory_order_relaxed);
    singlePingTimeoutSamples.store(
        static_cast<int>((static_cast<int64_t>(safeTimeoutMilliseconds) * currentSampleRate) / 1000),
        std::memory_order_relaxed);
    singlePingPulsePosition.store(0, std::memory_order_relaxed);
    singlePingStatus.store(PingStatusArmed, std::memory_order_release);
    return true;
}

SinglePingResult RealtimeEngine::singlePingResult() const noexcept
{
    return SinglePingResult {
        singlePingStatus.load(std::memory_order_acquire),
        singlePingInputChannel.load(std::memory_order_relaxed),
        singlePingOutputChannel.load(std::memory_order_relaxed),
        singlePingSampleRate.load(std::memory_order_relaxed),
        singlePingLatencySamples.load(std::memory_order_relaxed),
        singlePingElapsedSamples.load(std::memory_order_relaxed),
        singlePingPeakPercent.load(std::memory_order_relaxed),
        singlePingTimeoutMilliseconds.load(std::memory_order_relaxed)
    };
}

void RealtimeEngine::clearPluginSlots() noexcept
{
    for (int slot = 0; slot < MaxPluginSlots; ++slot)
        clearPluginSlot(slot);
}

void RealtimeEngine::setPluginSlot(
    int slot,
    RealtimePluginProcessor* processor,
    CallbackStreamKind kind,
    const PluginInputRoute* inputRoutes,
    int inputRouteCount,
    const PluginOutputRoute* outputRoutes,
    int outputRouteCount,
    bool enabled,
    bool bypassed) noexcept
{
    if (slot < 0 || slot >= MaxPluginSlots)
        return;

    auto& target = pluginSlots[static_cast<size_t>(slot)];
    target.enabled.store(false, std::memory_order_release);
    target.kind.store(static_cast<int>(kind), std::memory_order_release);
    target.processor.store(processor, std::memory_order_release);
    target.bypassed.store(bypassed, std::memory_order_release);
    setPluginSlotRoutes(slot, inputRoutes, inputRouteCount, outputRoutes, outputRouteCount);
    target.enabled.store(processor != nullptr && enabled, std::memory_order_release);
    refreshDynamicPluginScratchBuffers();
    refreshRealtimeActivityCounters();
}

void RealtimeEngine::clearPluginSlot(int slot) noexcept
{
    if (slot < 0 || slot >= MaxPluginSlots)
        return;

    auto& target = pluginSlots[static_cast<size_t>(slot)];
    target.enabled.store(false, std::memory_order_release);
    target.processor.store(nullptr, std::memory_order_release);
    target.bypassed.store(false, std::memory_order_release);
    target.inputRouteCount.store(0, std::memory_order_release);
    target.outputRouteCount.store(0, std::memory_order_release);
    refreshDynamicPluginScratchBuffers();
    refreshRealtimeActivityCounters();
}

void RealtimeEngine::setPluginSlotRoutes(
    int slot,
    const PluginInputRoute* inputRoutes,
    int inputRouteCount,
    const PluginOutputRoute* outputRoutes,
    int outputRouteCount) noexcept
{
    if (slot < 0 || slot >= MaxPluginSlots)
        return;

    auto& target = pluginSlots[static_cast<size_t>(slot)];
    const int safeInputCount = inputRoutes != nullptr ? std::clamp(inputRouteCount, 0, MaxPluginRoutes) : 0;
    const int safeOutputCount = outputRoutes != nullptr ? std::clamp(outputRouteCount, 0, MaxPluginRoutes) : 0;

    bool unchanged =
        target.inputRouteCount.load(std::memory_order_acquire) == safeInputCount &&
        target.outputRouteCount.load(std::memory_order_acquire) == safeOutputCount;

    for (int i = 0; unchanged && i < safeInputCount; ++i)
    {
        unchanged =
            target.inputSourceKinds[static_cast<size_t>(i)].load(std::memory_order_acquire) == inputRoutes[i].sourceKind &&
            target.inputSourceChannels[static_cast<size_t>(i)].load(std::memory_order_acquire) == inputRoutes[i].sourceChannel &&
            target.inputSourceSlots[static_cast<size_t>(i)].load(std::memory_order_acquire) == inputRoutes[i].sourceSlot &&
            target.inputSourcePins[static_cast<size_t>(i)].load(std::memory_order_acquire) == inputRoutes[i].sourcePin &&
            target.inputPluginPins[static_cast<size_t>(i)].load(std::memory_order_acquire) == inputRoutes[i].pluginPin;
    }

    for (int i = 0; unchanged && i < safeOutputCount; ++i)
    {
        unchanged =
            target.outputDestinationKinds[static_cast<size_t>(i)].load(std::memory_order_acquire) == outputRoutes[i].destinationKind &&
            target.outputPluginPins[static_cast<size_t>(i)].load(std::memory_order_acquire) == outputRoutes[i].pluginPin &&
            target.outputDestinationChannels[static_cast<size_t>(i)].load(std::memory_order_acquire) == outputRoutes[i].destinationChannel &&
            target.outputDestinationSlots[static_cast<size_t>(i)].load(std::memory_order_acquire) == outputRoutes[i].destinationSlot &&
            target.outputDestinationPins[static_cast<size_t>(i)].load(std::memory_order_acquire) == outputRoutes[i].destinationPin;
    }

    if (unchanged)
        return;

    const bool wasEnabled = target.enabled.load(std::memory_order_acquire);
    target.enabled.store(false, std::memory_order_release);

    for (int i = 0; i < safeInputCount; ++i)
    {
        target.inputSourceKinds[static_cast<size_t>(i)].store(inputRoutes[i].sourceKind, std::memory_order_relaxed);
        target.inputSourceChannels[static_cast<size_t>(i)].store(inputRoutes[i].sourceChannel, std::memory_order_relaxed);
        target.inputSourceSlots[static_cast<size_t>(i)].store(inputRoutes[i].sourceSlot, std::memory_order_relaxed);
        target.inputSourcePins[static_cast<size_t>(i)].store(inputRoutes[i].sourcePin, std::memory_order_relaxed);
        target.inputPluginPins[static_cast<size_t>(i)].store(inputRoutes[i].pluginPin, std::memory_order_relaxed);
    }

    for (int i = 0; i < safeOutputCount; ++i)
    {
        target.outputDestinationKinds[static_cast<size_t>(i)].store(outputRoutes[i].destinationKind, std::memory_order_relaxed);
        target.outputPluginPins[static_cast<size_t>(i)].store(outputRoutes[i].pluginPin, std::memory_order_relaxed);
        target.outputDestinationChannels[static_cast<size_t>(i)].store(outputRoutes[i].destinationChannel, std::memory_order_relaxed);
        target.outputDestinationSlots[static_cast<size_t>(i)].store(outputRoutes[i].destinationSlot, std::memory_order_relaxed);
        target.outputDestinationPins[static_cast<size_t>(i)].store(outputRoutes[i].destinationPin, std::memory_order_relaxed);
    }

    target.inputRouteCount.store(safeInputCount, std::memory_order_release);
    target.outputRouteCount.store(safeOutputCount, std::memory_order_release);
    target.enabled.store(wasEnabled, std::memory_order_release);
    refreshDynamicPluginScratchBuffers();
    refreshRealtimeActivityCounters();
}

void RealtimeEngine::setPluginSlotEnabled(int slot, bool shouldEnable) noexcept
{
    if (slot < 0 || slot >= MaxPluginSlots)
        return;

    auto& target = pluginSlots[static_cast<size_t>(slot)];
    const bool enabled = target.processor.load(std::memory_order_acquire) != nullptr && shouldEnable;
    if (target.enabled.load(std::memory_order_acquire) == enabled)
        return;

    target.enabled.store(enabled, std::memory_order_release);
    refreshDynamicPluginScratchBuffers();
    refreshRealtimeActivityCounters();
}

bool RealtimeEngine::isPluginSlotEnabled(int slot) const noexcept
{
    if (slot < 0 || slot >= MaxPluginSlots)
        return false;

    return pluginSlots[static_cast<size_t>(slot)].enabled.load(std::memory_order_acquire);
}

void RealtimeEngine::setProbeChannels(int inputChannel, int outputChannel) noexcept
{
    const int safeInputChannel = inputChannel >= 0 ? std::clamp(inputChannel, 0, MaxChannels - 1) : -1;
    const int safeOutputChannel = outputChannel >= 0 ? std::clamp(outputChannel, 0, MaxChannels - 1) : -1;
    probeInputChannel.store(safeInputChannel, std::memory_order_release);
    probeOutputChannel.store(safeOutputChannel, std::memory_order_release);
    probeGeneration.fetch_add(1, std::memory_order_release);
    const int displayProbeChannel = safeInputChannel >= 0 ? safeInputChannel : safeOutputChannel;
    residualProbeStartChannel.store(displayProbeChannel, std::memory_order_relaxed);
    residualProbeReadChannel.store(displayProbeChannel, std::memory_order_relaxed);
    rawInputPopCount.store(0, std::memory_order_relaxed);
    postCopyPopCount.store(0, std::memory_order_relaxed);
    prePluginPopCount.store(0, std::memory_order_relaxed);
    callbackJitterOver100Count.store(0, std::memory_order_relaxed);
    callbackJitterMaxUsec.store(0, std::memory_order_relaxed);
    auto resetCounterArray = [](auto& counters) noexcept {
        for (auto& counter : counters)
            counter.store(0, std::memory_order_relaxed);
    };
    resetCounterArray(callbackJitterOver100ByStream);
    resetCounterArray(callbackJitterMaxUsecByStream);
    resetCounterArray(rawInputPopCountByStream);
    resetCounterArray(postCopyPopCountByStream);
    resetCounterArray(prePluginPopCountByStream);
    for (auto& entry : callbackLastEntryQpc)
        entry.store(0, std::memory_order_relaxed);
    rawInputDeltaPeakPercent.store(0, std::memory_order_relaxed);
    postCopyDeltaPeakPercent.store(0, std::memory_order_relaxed);
    prePluginDeltaPeakPercent.store(0, std::memory_order_relaxed);
    rawInputLivePeakPpm.store(0, std::memory_order_relaxed);
    postCopyLivePeakPpm.store(0, std::memory_order_relaxed);
    prePluginLivePeakPpm.store(0, std::memory_order_relaxed);
    rawInputBoundaryDeltaPpm.store(0, std::memory_order_relaxed);
    postCopyBoundaryDeltaPpm.store(0, std::memory_order_relaxed);
    prePluginBoundaryDeltaPpm.store(0, std::memory_order_relaxed);
    postCopyResidualCount.store(0, std::memory_order_relaxed);
    prePluginResidualCount.store(0, std::memory_order_relaxed);
    finalResidualCount.store(0, std::memory_order_relaxed);
    postCopyResidualPeakPpm.store(0, std::memory_order_relaxed);
    prePluginResidualPeakPpm.store(0, std::memory_order_relaxed);
    finalResidualPeakPpm.store(0, std::memory_order_relaxed);
    inputInsertReadPeakPercent.store(0, std::memory_order_relaxed);
    inputInsertWritePeakPercent.store(0, std::memory_order_relaxed);
    mainInputReadPeakPercent.store(0, std::memory_order_relaxed);
    mainOutputReadPeakPercent.store(0, std::memory_order_relaxed);
    mainWritePeakPercent.store(0, std::memory_order_relaxed);
    outputInsertReadPeakPercent.store(0, std::memory_order_relaxed);
    outputInsertWritePeakPercent.store(0, std::memory_order_relaxed);
    inputInsertMaxReadPeakPercent.store(0, std::memory_order_relaxed);
    inputInsertMaxReadChannel.store(-1, std::memory_order_relaxed);
    inputInsertMaxWritePeakPercent.store(0, std::memory_order_relaxed);
    inputInsertMaxWriteChannel.store(-1, std::memory_order_relaxed);
    mainSourceMaxReadPeakPercent.store(0, std::memory_order_relaxed);
    mainSourceMaxReadChannel.store(-1, std::memory_order_relaxed);
    mainBusMaxReadPeakPercent.store(0, std::memory_order_relaxed);
    mainBusMaxReadChannel.store(-1, std::memory_order_relaxed);
    mainMaxWritePeakPercent.store(0, std::memory_order_relaxed);
    mainMaxWriteChannel.store(-1, std::memory_order_relaxed);
    outputInsertMaxReadPeakPercent.store(0, std::memory_order_relaxed);
    outputInsertMaxReadChannel.store(-1, std::memory_order_relaxed);
    outputInsertMaxWritePeakPercent.store(0, std::memory_order_relaxed);
    outputInsertMaxWriteChannel.store(-1, std::memory_order_relaxed);
}

void RealtimeEngine::updateFormat(int newSampleRate, int newBlockSize) noexcept
{
    sampleRate.store(newSampleRate, std::memory_order_release);
    blockSize.store(newBlockSize, std::memory_order_release);
}

void RealtimeEngine::recordCallbackArrivalJitter(AudioBufferView buffer, int streamIndex) noexcept
{
    const int safeStream = std::clamp(streamIndex, 0, DelayStreamCount - 1);
    const int rate = buffer.sampleRate > 0 ? buffer.sampleRate : sampleRate.load(std::memory_order_relaxed);
    const int samples = buffer.samplesPerFrame > 0 ? buffer.samplesPerFrame : blockSize.load(std::memory_order_relaxed);
    if (rate <= 0 || samples <= 0)
        return;

    LARGE_INTEGER now {};
    QueryPerformanceCounter(&now);
    const auto currentQpc = static_cast<int64_t>(now.QuadPart);
    auto& previousQpc = callbackLastEntryQpc[static_cast<size_t>(safeStream)];
    const auto previous = previousQpc.exchange(currentQpc, std::memory_order_acq_rel);
    if (previous <= 0 || currentQpc <= previous)
        return;

    const double gapUsec = (static_cast<double>(currentQpc - previous) * 1000000.0) / queryPerformanceFrequency();
    const double expectedUsec = (static_cast<double>(samples) * 1000000.0) / static_cast<double>(rate);
    if (expectedUsec <= 0.0)
        return;

    const int gapInt = std::clamp(static_cast<int>(gapUsec + 0.5), 0, 60000000);
    int currentMax = callbackJitterMaxUsec.load(std::memory_order_relaxed);
    while (gapInt > currentMax &&
           !callbackJitterMaxUsec.compare_exchange_weak(currentMax, gapInt, std::memory_order_relaxed, std::memory_order_relaxed))
    {
    }

    auto& streamMax = callbackJitterMaxUsecByStream[static_cast<size_t>(safeStream)];
    int currentStreamMax = streamMax.load(std::memory_order_relaxed);
    while (gapInt > currentStreamMax &&
           !streamMax.compare_exchange_weak(currentStreamMax, gapInt, std::memory_order_relaxed, std::memory_order_relaxed))
    {
    }

    if (gapUsec > expectedUsec * 2.0)
    {
        callbackJitterOver100Count.fetch_add(1, std::memory_order_relaxed);
        callbackJitterOver100ByStream[static_cast<size_t>(safeStream)].fetch_add(1, std::memory_order_relaxed);
    }
    if (gapUsec > expectedUsec * 1.5)
        callbackJitterOver50Count.fetch_add(1, std::memory_order_relaxed);
    if (gapUsec > expectedUsec * 1.25)
        callbackJitterOver25Count.fetch_add(1, std::memory_order_relaxed);
}
void RealtimeEngine::process(AudioBufferView buffer, CallbackStreamKind kind) noexcept
{
    processInternal(buffer, kind, true, delayStreamIndex(kind));
}

void RealtimeEngine::processInsertAsio(AudioBufferView buffer) noexcept
{
    processInternal(buffer, CallbackStreamKind::InputInsert, false, 3);
}

void RealtimeEngine::processInternal(
    AudioBufferView buffer,
    CallbackStreamKind kind,
    bool enableInputOutputRoutes,
    int delayStream) noexcept
{
    ensureDenormalFlush();
    ensureRealtimeThreadPriority();
    if (RealtimeJitterDiagnosticsEnabled)
        recordCallbackArrivalJitter(buffer, delayStream);

    const int timingSamples = std::max(1, buffer.samplesPerFrame);
    const int timingSampleRate = buffer.sampleRate > 0
        ? buffer.sampleRate
        : sampleRate.load(std::memory_order_relaxed);
    const int timingCaptureHz = timingSampleRate >= 176400 || timingSamples <= 128
        ? 1
        : (timingSampleRate >= 96000 || timingSamples <= 256 ? 2 : 4);
    const int timingIntervalSamples = timingSampleRate > 0
        ? std::max(timingSamples, timingSampleRate / timingCaptureHz)
        : std::max(timingSamples, 2400);
    thread_local int timingSamplesRemaining = 0;
    thread_local uint64_t pendingUntimedCallbacks = 0;
    const bool captureTiming = timingSamplesRemaining <= 0;
    if (captureTiming)
        timingSamplesRemaining = timingIntervalSamples;
    else
        timingSamplesRemaining -= timingSamples;

    auto flushPendingCallbacks = [this]() noexcept {
        if (pendingUntimedCallbacks > 0)
        {
            callbackCount.fetch_add(pendingUntimedCallbacks, std::memory_order_relaxed);
            pendingUntimedCallbacks = 0;
        }
    };

    auto publishUntimedCallback = [this]() noexcept {
        ++pendingUntimedCallbacks;
        if (pendingUntimedCallbacks >= 64)
        {
            callbackCount.fetch_add(pendingUntimedCallbacks, std::memory_order_relaxed);
            pendingUntimedCallbacks = 0;
        }
    };

    LARGE_INTEGER start {};
    LARGE_INTEGER end {};
    if (captureTiming)
    {
        sampleRate.store(buffer.sampleRate, std::memory_order_relaxed);
        blockSize.store(buffer.samplesPerFrame, std::memory_order_relaxed);
        inputChannels.store(buffer.inputChannels, std::memory_order_relaxed);
        outputChannels.store(buffer.outputChannels, std::memory_order_relaxed);
        QueryPerformanceCounter(&start);
    }

    if (DiagnosticSkipAudioWrites)
    {
        if (captureTiming)
        {
            QueryPerformanceCounter(&end);
            flushPendingCallbacks();
            publishTiming(elapsedMicroseconds(start, end), buffer);
        }
        else
        {
            publishUntimedCallback();
        }

        return;
    }
    const int readOffset = getReadOffset(buffer, kind);
    const bool suppressInputCallbackChannels =
        kind == CallbackStreamKind::InputInsert &&
        enableInputOutputRoutes &&
        suppressedInputChannelCount.load(std::memory_order_relaxed) > 0;

    if (RealtimeAudioProbeDiagnosticsEnabled)
        probeAudioDiscontinuity(buffer, kind, delayStream, readOffset, PopProbeRawInput, false);
    if (DiagnosticZeroReturnAfterRawProbe)
    {
        const int zeroSamples = std::max(0, buffer.samplesPerFrame);
        const int zeroChannels = std::max(0, std::min(buffer.outputChannels, MaxCallbackChannels));
        for (int channel = 0; channel < zeroChannels; ++channel)
        {
            float* output = buffer.write[channel];
            if (output != nullptr)
                std::fill_n(output, zeroSamples, 0.0f);
        }

        if (captureTiming)
        {
            QueryPerformanceCounter(&end);
            flushPendingCallbacks();
            publishTiming(elapsedMicroseconds(start, end), buffer);
        }
        else
        {
            publishUntimedCallback();
        }

        return;
    }


    const bool processingWork = hasProcessingWork(kind, enableInputOutputRoutes, delayStream);
    if (!processingWork)
    {
        copyPassthrough(buffer, readOffset, suppressInputCallbackChannels);
        if (RealtimeAudioProbeDiagnosticsEnabled)
            probeAudioDiscontinuity(buffer, kind, delayStream, readOffset, PopProbePostCopy, true);
        if (RealtimeAudioProbeDiagnosticsEnabled)
            probePassthroughResidual(buffer, kind, readOffset, ResidualProbePostCopy);

        if (captureTiming)
        {
            QueryPerformanceCounter(&end);
            flushPendingCallbacks();
            publishTiming(elapsedMicroseconds(start, end), buffer);
        }
        else
        {
            publishUntimedCallback();
        }

        return;
    }

    copyPassthrough(buffer, readOffset, suppressInputCallbackChannels);
    if (RealtimeAudioProbeDiagnosticsEnabled)
        probeAudioDiscontinuity(buffer, kind, delayStream, readOffset, PopProbePostCopy, true);
    if (RealtimeAudioProbeDiagnosticsEnabled)
        probePassthroughResidual(buffer, kind, readOffset, ResidualProbePostCopy);

    std::array<bool, MaxChannels> pluginOutputWritten {};
    if (kind == CallbackStreamKind::InputInsert)
        processSinglePingInput(buffer);

    applyConfiguredDelays(buffer, kind, readOffset, delayStream, suppressInputCallbackChannels);
    if (RealtimeAudioProbeDiagnosticsEnabled)
        probeAudioDiscontinuity(buffer, kind, delayStream, readOffset, PopProbePrePlugin, true);
    if (RealtimeAudioProbeDiagnosticsEnabled)
        probePassthroughResidual(buffer, kind, readOffset, ResidualProbePrePlugin);
    const int sourceReadOffset = kind == CallbackStreamKind::Main ? 0 : readOffset;
    const int pluginStreamIndex = std::clamp(delayStreamIndex(kind), 0, DelayStreamCount - 1);
    const bool hasPluginWork =
        activePluginSlotCounts[static_cast<size_t>(pluginStreamIndex)].load(std::memory_order_relaxed) > 0;
    bool pluginProcessingSkipped = false;
    if (hasPluginWork)
    {
        auto& pluginStreamActive = pluginProcessingActive[static_cast<size_t>(pluginStreamIndex)];
        const bool pluginsProcessed = !pluginStreamActive.exchange(true, std::memory_order_acquire);
        if (pluginsProcessed)
        {
            struct PluginProcessingGuard
            {
                std::atomic<bool>& active;

                ~PluginProcessingGuard()
                {
                    active.store(false, std::memory_order_release);
                }
            };

            PluginProcessingGuard guard { pluginStreamActive };
            applyPlugins(buffer, kind, sourceReadOffset, suppressInputCallbackChannels, pluginOutputWritten);
        }
        else
        {
            pluginProcessingSkipped = true;
            pluginBusySkipCount.fetch_add(1, std::memory_order_relaxed);
        }
    }

    const auto pluginNodeOutputWritten = pluginOutputWritten;

    applyPluginPassthroughRoutes(buffer, kind, sourceReadOffset, delayStream, suppressInputCallbackChannels, pluginOutputWritten);
    if (!pluginProcessingSkipped)
        applyPluginGraphGate(buffer, kind, suppressInputCallbackChannels, pluginNodeOutputWritten);

    applyDirectRoutes(buffer, kind, sourceReadOffset, enableInputOutputRoutes, suppressInputCallbackChannels, pluginOutputWritten);
    applyConfiguredGains(buffer, kind, delayStream, suppressInputCallbackChannels);
    if (!pluginProcessingSkipped)
        applyPluginGraphGate(buffer, kind, suppressInputCallbackChannels, pluginNodeOutputWritten);
    if (RealtimeAudioProbeDiagnosticsEnabled)
        probePassthroughResidual(buffer, kind, readOffset, ResidualProbeFinal);
    if (kind == CallbackStreamKind::OutputInsert)
        processSinglePingOutput(buffer);

    if (captureTiming)
    {
        QueryPerformanceCounter(&end);
        flushPendingCallbacks();
        publishTiming(elapsedMicroseconds(start, end), buffer);
    }
    else
    {
        publishUntimedCallback();
    }
}

RealtimeStats RealtimeEngine::getStats() const noexcept
{
    auto loadCounter = [](const auto& counters, int index) noexcept -> uint64_t {
        return counters[static_cast<size_t>(index)].load(std::memory_order_acquire);
    };

    auto loadInt = [](const auto& counters, int index) noexcept -> int {
        return counters[static_cast<size_t>(index)].load(std::memory_order_acquire);
    };

    return RealtimeStats {
        sampleRate.load(std::memory_order_acquire),
        blockSize.load(std::memory_order_acquire),
        inputChannels.load(std::memory_order_acquire),
        outputChannels.load(std::memory_order_acquire),
        callbackCount.load(std::memory_order_acquire),
        lastProcessUsec.load(std::memory_order_acquire),
        peakProcessUsec.load(std::memory_order_acquire),
        callbackCpuPercent.load(std::memory_order_acquire),
        callbackOver50Count.load(std::memory_order_acquire),
        callbackOver80Count.load(std::memory_order_acquire),
        callbackOver100Count.load(std::memory_order_acquire),
        pluginBusySkipCount.load(std::memory_order_acquire),
        routeFifoWaitCount.load(std::memory_order_acquire),
        callbackJitterOver25Count.load(std::memory_order_acquire),
        callbackJitterOver50Count.load(std::memory_order_acquire),
        callbackJitterOver100Count.load(std::memory_order_acquire),
        callbackJitterMaxUsec.load(std::memory_order_acquire),
        rawInputPopCount.load(std::memory_order_acquire),
        postCopyPopCount.load(std::memory_order_acquire),
        prePluginPopCount.load(std::memory_order_acquire),
        rawInputDeltaPeakPercent.load(std::memory_order_acquire),
        postCopyDeltaPeakPercent.load(std::memory_order_acquire),
        prePluginDeltaPeakPercent.load(std::memory_order_acquire),
        rawInputLivePeakPpm.load(std::memory_order_acquire),
        postCopyLivePeakPpm.load(std::memory_order_acquire),
        prePluginLivePeakPpm.load(std::memory_order_acquire),
        rawInputBoundaryDeltaPpm.load(std::memory_order_acquire),
        postCopyBoundaryDeltaPpm.load(std::memory_order_acquire),
        prePluginBoundaryDeltaPpm.load(std::memory_order_acquire),
        postCopyResidualCount.load(std::memory_order_acquire),
        prePluginResidualCount.load(std::memory_order_acquire),
        finalResidualCount.load(std::memory_order_acquire),
        postCopyResidualPeakPpm.load(std::memory_order_acquire),
        prePluginResidualPeakPpm.load(std::memory_order_acquire),
        finalResidualPeakPpm.load(std::memory_order_acquire),
        residualProbeStartChannel.load(std::memory_order_acquire),
        residualProbeReadChannel.load(std::memory_order_acquire),
        delayBufferSampleRate.load(std::memory_order_acquire),
        probeInputChannel.load(std::memory_order_acquire),
        probeOutputChannel.load(std::memory_order_acquire),
        inputInsertReadPeakPercent.load(std::memory_order_acquire),
        inputInsertWritePeakPercent.load(std::memory_order_acquire),
        mainInputReadPeakPercent.load(std::memory_order_acquire),
        mainOutputReadPeakPercent.load(std::memory_order_acquire),
        mainWritePeakPercent.load(std::memory_order_acquire),
        outputInsertReadPeakPercent.load(std::memory_order_acquire),
        outputInsertWritePeakPercent.load(std::memory_order_acquire),
        inputInsertMaxReadPeakPercent.load(std::memory_order_acquire),
        inputInsertMaxReadChannel.load(std::memory_order_acquire),
        inputInsertMaxWritePeakPercent.load(std::memory_order_acquire),
        inputInsertMaxWriteChannel.load(std::memory_order_acquire),
        mainSourceMaxReadPeakPercent.load(std::memory_order_acquire),
        mainSourceMaxReadChannel.load(std::memory_order_acquire),
        mainBusMaxReadPeakPercent.load(std::memory_order_acquire),
        mainBusMaxReadChannel.load(std::memory_order_acquire),
        mainMaxWritePeakPercent.load(std::memory_order_acquire),
        mainMaxWriteChannel.load(std::memory_order_acquire),
        outputInsertMaxReadPeakPercent.load(std::memory_order_acquire),
        outputInsertMaxReadChannel.load(std::memory_order_acquire),
        outputInsertMaxWritePeakPercent.load(std::memory_order_acquire),
        outputInsertMaxWriteChannel.load(std::memory_order_acquire),
        inputInsertInputChannels.load(std::memory_order_acquire),
        inputInsertOutputChannels.load(std::memory_order_acquire),
        mainInputChannels.load(std::memory_order_acquire),
        mainOutputChannels.load(std::memory_order_acquire),
        outputInsertInputChannels.load(std::memory_order_acquire),
        outputInsertOutputChannels.load(std::memory_order_acquire),
        loadCounter(callbackJitterOver100ByStream, 0),
        loadCounter(callbackJitterOver100ByStream, 1),
        loadCounter(callbackJitterOver100ByStream, 2),
        loadCounter(callbackJitterOver100ByStream, 3),
        loadInt(callbackJitterMaxUsecByStream, 0),
        loadInt(callbackJitterMaxUsecByStream, 1),
        loadInt(callbackJitterMaxUsecByStream, 2),
        loadInt(callbackJitterMaxUsecByStream, 3),
        loadCounter(rawInputPopCountByStream, 0),
        loadCounter(rawInputPopCountByStream, 1),
        loadCounter(rawInputPopCountByStream, 2),
        loadCounter(rawInputPopCountByStream, 3),
        loadCounter(postCopyPopCountByStream, 0),
        loadCounter(postCopyPopCountByStream, 1),
        loadCounter(postCopyPopCountByStream, 2),
        loadCounter(postCopyPopCountByStream, 3),
        loadCounter(prePluginPopCountByStream, 0),
        loadCounter(prePluginPopCountByStream, 1),
        loadCounter(prePluginPopCountByStream, 2),
        loadCounter(prePluginPopCountByStream, 3)
    };
}

int RealtimeEngine::getReadOffset(AudioBufferView buffer, CallbackStreamKind kind) const noexcept
{
    if (kind != CallbackStreamKind::Main)
        return 0;

    return std::max(0, buffer.inputChannels - buffer.outputChannels);
}

int RealtimeEngine::getSelectedChannelCount() const noexcept
{
    const int start = targetStart.load(std::memory_order_acquire);
    return clampChannelCount(start, targetCount.load(std::memory_order_acquire));
}

int RealtimeEngine::clampChannelCount(int start, int count) const noexcept
{
    const int safeStart = std::clamp(start, 0, MaxChannels - 1);
    return std::clamp(count, 1, MaxChannels - safeStart);
}

RealtimeEngine::GainBank& RealtimeEngine::gainBankFor(CallbackStreamKind kind) noexcept
{
    return kind == CallbackStreamKind::InputInsert ? inputGainPercent : outputGainPercent;
}

const RealtimeEngine::GainBank& RealtimeEngine::gainBankFor(CallbackStreamKind kind) const noexcept
{
    return kind == CallbackStreamKind::InputInsert ? inputGainPercent : outputGainPercent;
}

RealtimeEngine::DelayBank& RealtimeEngine::delayBankFor(CallbackStreamKind kind) noexcept
{
    return kind == CallbackStreamKind::InputInsert ? inputDelayMilliseconds : outputDelayMilliseconds;
}

const RealtimeEngine::DelayBank& RealtimeEngine::delayBankFor(CallbackStreamKind kind) const noexcept
{
    return kind == CallbackStreamKind::InputInsert ? inputDelayMilliseconds : outputDelayMilliseconds;
}

RealtimeEngine::EnableBank& RealtimeEngine::enableBankFor(CallbackStreamKind kind) noexcept
{
    return kind == CallbackStreamKind::InputInsert ? inputEnabled : outputEnabled;
}

const RealtimeEngine::EnableBank& RealtimeEngine::enableBankFor(CallbackStreamKind kind) const noexcept
{
    return kind == CallbackStreamKind::InputInsert ? inputEnabled : outputEnabled;
}

RealtimeEngine::EnableBank& RealtimeEngine::pluginGraphBankFor(CallbackStreamKind kind) noexcept
{
    if (kind == CallbackStreamKind::InputInsert)
        return inputPluginGraphEnabled;

    if (kind == CallbackStreamKind::Main)
        return mainPluginGraphEnabled;

    return outputPluginGraphEnabled;
}

const RealtimeEngine::EnableBank& RealtimeEngine::pluginGraphBankFor(CallbackStreamKind kind) const noexcept
{
    if (kind == CallbackStreamKind::InputInsert)
        return inputPluginGraphEnabled;

    if (kind == CallbackStreamKind::Main)
        return mainPluginGraphEnabled;

    return outputPluginGraphEnabled;
}

RealtimeEngine::DirectRouteBank& RealtimeEngine::directRouteBankFor(CallbackStreamKind kind) noexcept
{
    if (kind == CallbackStreamKind::InputInsert)
        return inputDirectRoutes;

    if (kind == CallbackStreamKind::Main)
        return mainDirectRoutes;

    return outputDirectRoutes;
}

const RealtimeEngine::DirectRouteBank& RealtimeEngine::directRouteBankFor(CallbackStreamKind kind) const noexcept
{
    if (kind == CallbackStreamKind::InputInsert)
        return inputDirectRoutes;

    if (kind == CallbackStreamKind::Main)
        return mainDirectRoutes;

    return outputDirectRoutes;
}

RealtimeEngine::DirectRouteBank& RealtimeEngine::pluginPassthroughBankFor(CallbackStreamKind kind) noexcept
{
    if (kind == CallbackStreamKind::InputInsert)
        return inputPluginPassthroughRoutes;

    if (kind == CallbackStreamKind::Main)
        return mainPluginPassthroughRoutes;

    return outputPluginPassthroughRoutes;
}

const RealtimeEngine::DirectRouteBank& RealtimeEngine::pluginPassthroughBankFor(CallbackStreamKind kind) const noexcept
{
    if (kind == CallbackStreamKind::InputInsert)
        return inputPluginPassthroughRoutes;

    if (kind == CallbackStreamKind::Main)
        return mainPluginPassthroughRoutes;

    return outputPluginPassthroughRoutes;
}

bool RealtimeEngine::isInputCallbackSuppressed(bool suppressInputCallbackChannels, int channel) const noexcept
{
    return suppressInputCallbackChannels &&
           channel >= 0 &&
           channel < MaxChannels &&
           inputCallbackSuppressed[static_cast<size_t>(channel)].load(std::memory_order_relaxed);
}

bool RealtimeEngine::hasProcessingWork(
    CallbackStreamKind kind,
    bool enableInputOutputRoutes,
    int delayStream) const noexcept
{
    const int pluginStreamIndex = std::clamp(delayStreamIndex(kind), 0, DelayStreamCount - 1);
    const int delayIndex = std::clamp(delayStream, 0, DelayStreamCount - 1);

    if (activeDelayChannelCounts[static_cast<size_t>(delayIndex)].load(std::memory_order_relaxed) > 0 ||
        activeGainChannelCounts[static_cast<size_t>(delayIndex)].load(std::memory_order_relaxed) > 0 ||
        activePluginSlotCounts[static_cast<size_t>(pluginStreamIndex)].load(std::memory_order_relaxed) > 0 ||
        activePluginGraphChannelCounts[static_cast<size_t>(pluginStreamIndex)].load(std::memory_order_relaxed) > 0 ||
        pluginPassthroughBankFor(kind).routeCount.load(std::memory_order_acquire) > 0)
    {
        return true;
    }

    if (kind == CallbackStreamKind::InputInsert)
    {
        if (singlePingStatus.load(std::memory_order_acquire) == PingStatusArmed)
            return true;

        return enableInputOutputRoutes && inputDirectRoutes.routeCount.load(std::memory_order_acquire) > 0;
    }

    if (kind == CallbackStreamKind::OutputInsert)
    {
        if (singlePingStatus.load(std::memory_order_acquire) == PingStatusWaiting)
            return true;

        return (enableInputOutputRoutes && inputDirectRoutes.routeCount.load(std::memory_order_acquire) > 0) ||
               outputDirectRoutes.routeCount.load(std::memory_order_acquire) > 0;
    }

    return mainDirectRoutes.routeCount.load(std::memory_order_acquire) > 0;
}
void RealtimeEngine::refreshRealtimeActivityCounters() noexcept
{
    std::array<int, DelayStreamCount> pluginCounts {};
    std::array<int, DelayStreamCount> graphCounts {};
    std::array<int, DelayStreamCount> delayCounts {};
    std::array<int, DelayStreamCount> gainCounts {};

    for (int slot = 0; slot < MaxPluginSlots; ++slot)
    {
        const auto& pluginSlot = pluginSlots[static_cast<size_t>(slot)];
        const auto kind = static_cast<CallbackStreamKind>(pluginSlot.kind.load(std::memory_order_relaxed));
        const int streamIndex = std::clamp(delayStreamIndex(kind), 0, DelayStreamCount - 1);
        if (pluginSlot.enabled.load(std::memory_order_relaxed) &&
            pluginSlot.processor.load(std::memory_order_acquire) != nullptr &&
            pluginSlot.inputRouteCount.load(std::memory_order_relaxed) > 0)
        {
            ++pluginCounts[static_cast<size_t>(streamIndex)];
        }
    }

    const auto countGraphChannels = [](const EnableBank& bank) noexcept {
        int count = 0;
        for (int ch = 0; ch < MaxChannels; ++ch)
        {
            if (bank[static_cast<size_t>(ch)].load(std::memory_order_relaxed))
                ++count;
        }

        return count;
    };

    graphCounts[0] = countGraphChannels(inputPluginGraphEnabled);
    graphCounts[1] = countGraphChannels(outputPluginGraphEnabled);
    graphCounts[2] = countGraphChannels(mainPluginGraphEnabled);

    for (int ch = 0; ch < MaxChannels; ++ch)
    {
        const bool inputActive = inputEnabled[static_cast<size_t>(ch)].load(std::memory_order_relaxed);
        if (inputActive && inputDelayMilliseconds[static_cast<size_t>(ch)].load(std::memory_order_relaxed) > 0)
        {
            ++delayCounts[0];
            ++delayCounts[3];
        }

        if (inputActive && inputGainPercent[static_cast<size_t>(ch)].load(std::memory_order_relaxed) != 100)
        {
            ++gainCounts[0];
            ++gainCounts[3];
        }

        const bool outputActive = outputEnabled[static_cast<size_t>(ch)].load(std::memory_order_relaxed);
        if (outputActive && outputDelayMilliseconds[static_cast<size_t>(ch)].load(std::memory_order_relaxed) > 0)
        {
            ++delayCounts[1];
            ++delayCounts[2];
        }

        if (outputActive && outputGainPercent[static_cast<size_t>(ch)].load(std::memory_order_relaxed) != 100)
        {
            ++gainCounts[1];
            ++gainCounts[2];
        }
    }

    for (int i = 0; i < DelayStreamCount; ++i)
    {
        activePluginSlotCounts[static_cast<size_t>(i)].store(pluginCounts[static_cast<size_t>(i)], std::memory_order_release);
        activePluginGraphChannelCounts[static_cast<size_t>(i)].store(graphCounts[static_cast<size_t>(i)], std::memory_order_release);
        activeDelayChannelCounts[static_cast<size_t>(i)].store(delayCounts[static_cast<size_t>(i)], std::memory_order_release);
        activeGainChannelCounts[static_cast<size_t>(i)].store(gainCounts[static_cast<size_t>(i)], std::memory_order_release);
    }
}
void RealtimeEngine::probeAudioDiscontinuity(
    AudioBufferView buffer,
    CallbackStreamKind kind,
    int streamIndex,
    int readOffset,
    int position,
    bool preferCurrentBuffer) noexcept
{
    if (position < 0 || position >= PopProbePositions || buffer.samplesPerFrame <= 1)
        return;

    const int safeStream = std::clamp(streamIndex, 0, DelayStreamCount - 1);

    thread_local std::array<std::array<std::array<float, MaxChannels>, PopProbePositions>, DelayStreamCount> previousSamples {};
    thread_local std::array<std::array<std::array<bool, MaxChannels>, PopProbePositions>, DelayStreamCount> previousValid {};
    thread_local uint64_t observedProbeGeneration = 0;

    const uint64_t currentProbeGeneration = probeGeneration.load(std::memory_order_acquire);
    if (observedProbeGeneration != currentProbeGeneration)
    {
        previousSamples = {};
        previousValid = {};
        observedProbeGeneration = currentProbeGeneration;
    }

    const int selectedStart = kind == CallbackStreamKind::OutputInsert
        ? probeOutputChannel.load(std::memory_order_relaxed)
        : probeInputChannel.load(std::memory_order_relaxed);
    if (selectedStart < 0 || selectedStart >= MaxChannels)
        return;

    const int starts[1] = { selectedStart };
    bool foundAnyChannel = false;
    int foundStartChannel = -1;
    int foundReadChannel = -1;
    float maxDelta = 0.0f;
    float maxInnerDelta = 0.0f;
    float deltaSum = 0.0f;
    int deltaCount = 0;
    float maxPredictionError = 0.0f;
    float boundaryDelta = 0.0f;
    float peak = 0.0f;
    bool hasBoundary = false;

    for (const int startChannel : starts)
    {
        for (int offset = 0; offset < PopProbeChannelPair; ++offset)
        {
            const int channel = startChannel + offset;
            if (channel < 0 || channel >= MaxChannels)
                continue;

            const float* samples = nullptr;
            if (preferCurrentBuffer)
            {
                samples = currentChannelPointer(buffer, readOffset, channel);
            }
            else if (buffer.read != nullptr)
            {
                const int readChannel = readOffset + channel;
                if (readChannel >= 0 && readChannel < buffer.inputChannels)
                    samples = buffer.read[readChannel];
            }

            if (samples == nullptr)
                continue;

            if (!foundAnyChannel)
            {
                foundStartChannel = startChannel;
                foundReadChannel = readOffset + startChannel;
            }

            foundAnyChannel = true;
            auto& wasValid = previousValid[static_cast<size_t>(safeStream)][static_cast<size_t>(position)][static_cast<size_t>(channel)];
            float previous = wasValid
                ? previousSamples[static_cast<size_t>(safeStream)][static_cast<size_t>(position)][static_cast<size_t>(channel)]
                : samples[0];
            if (wasValid)
            {
                const float boundary = samples[0] >= previous ? samples[0] - previous : previous - samples[0];
                boundaryDelta = std::max(boundaryDelta, boundary);
                hasBoundary = true;
            }

            float previousPrevious = previous;
            bool hasPreviousPrevious = wasValid;
            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
            {
                const float current = samples[sample];
                const float absolute = current >= 0.0f ? current : -current;
                peak = std::max(peak, absolute);
                if (sample > 0)
                {
                    const float delta = current >= previous ? current - previous : previous - current;
                    maxInnerDelta = std::max(maxInnerDelta, delta);
                    maxDelta = std::max(maxDelta, delta);
                    deltaSum += delta;
                    ++deltaCount;
                }

                if (hasPreviousPrevious)
                {
                    const float predicted = previous + (previous - previousPrevious);
                    const float predictionError = current >= predicted ? current - predicted : predicted - current;
                    maxPredictionError = std::max(maxPredictionError, predictionError);
                }

                previousPrevious = previous;
                hasPreviousPrevious = true;
                previous = current;
            }

            previousSamples[static_cast<size_t>(safeStream)][static_cast<size_t>(position)][static_cast<size_t>(channel)] = previous;
            wasValid = true;
        }

        if (foundAnyChannel)
            break;
    }

    if (!foundAnyChannel)
        return;

    residualProbeStartChannel.store(foundStartChannel, std::memory_order_relaxed);
    residualProbeReadChannel.store(foundReadChannel, std::memory_order_relaxed);

    const int livePeakPpm = std::clamp(static_cast<int>((peak * 1000000.0f) + 0.5f), 0, 1000000);
    const int boundaryDeltaPpm = std::clamp(static_cast<int>((boundaryDelta * 1000000.0f) + 0.5f), 0, 1000000);
    if (position == PopProbeRawInput)
    {
        rawInputLivePeakPpm.store(livePeakPpm, std::memory_order_relaxed);
        rawInputBoundaryDeltaPpm.store(boundaryDeltaPpm, std::memory_order_relaxed);
    }
    else if (position == PopProbePostCopy)
    {
        postCopyLivePeakPpm.store(livePeakPpm, std::memory_order_relaxed);
        postCopyBoundaryDeltaPpm.store(boundaryDeltaPpm, std::memory_order_relaxed);
    }
    else if (position == PopProbePrePlugin)
    {
        prePluginLivePeakPpm.store(livePeakPpm, std::memory_order_relaxed);
        prePluginBoundaryDeltaPpm.store(boundaryDeltaPpm, std::memory_order_relaxed);
    }

    const float displayedDelta = std::max(maxDelta, maxPredictionError);
    const int deltaPercent = std::clamp(static_cast<int>((displayedDelta * 100.0f) + 0.5f), 0, 1000);
    auto publishDelta = [deltaPercent](std::atomic<int>& target) noexcept {
        int current = target.load(std::memory_order_relaxed);
        while (deltaPercent > current &&
               !target.compare_exchange_weak(current, deltaPercent, std::memory_order_relaxed, std::memory_order_relaxed))
        {
        }
    };

    if (position == PopProbeRawInput)
        publishDelta(rawInputDeltaPeakPercent);
    else if (position == PopProbePostCopy)
        publishDelta(postCopyDeltaPeakPercent);
    else if (position == PopProbePrePlugin)
        publishDelta(prePluginDeltaPeakPercent);

    const float amplitudeThreshold = std::max(PopProbeDeltaThreshold, peak * PopProbeRelativeDeltaRatio);
    const float slopeThreshold = maxInnerDelta * PopProbeInnerSlopeMultiplier;
    const float boundaryThreshold = std::max(amplitudeThreshold, slopeThreshold);
    const float averageDelta = deltaCount > 0 ? deltaSum / static_cast<float>(deltaCount) : 0.0f;
    const float predictionThreshold = std::max(
        std::max(PopProbePredictionThreshold, peak * PopProbePredictionRelativeRatio),
        averageDelta * PopProbeAverageSlopeMultiplier);
    const bool boundaryPop = hasBoundary && boundaryDelta >= boundaryThreshold;
    const bool predictionPop = maxPredictionError >= predictionThreshold;
    if (peak < PopProbeMinimumPeak || (!boundaryPop && !predictionPop))
        return;

    if (position == PopProbeRawInput)
    {
        rawInputPopCount.fetch_add(1, std::memory_order_relaxed);
        rawInputPopCountByStream[static_cast<size_t>(safeStream)].fetch_add(1, std::memory_order_relaxed);
    }
    else if (position == PopProbePostCopy)
    {
        postCopyPopCount.fetch_add(1, std::memory_order_relaxed);
        postCopyPopCountByStream[static_cast<size_t>(safeStream)].fetch_add(1, std::memory_order_relaxed);
    }
    else if (position == PopProbePrePlugin)
    {
        prePluginPopCount.fetch_add(1, std::memory_order_relaxed);
        prePluginPopCountByStream[static_cast<size_t>(safeStream)].fetch_add(1, std::memory_order_relaxed);
    }
}
void RealtimeEngine::probePassthroughResidual(
    AudioBufferView buffer,
    CallbackStreamKind kind,
    int readOffset,
    int position) noexcept
{
    if (position < 0 || position >= ResidualProbePositions || buffer.samplesPerFrame <= 0 || buffer.read == nullptr)
        return;

    const int selectedStart = kind == CallbackStreamKind::OutputInsert
        ? probeOutputChannel.load(std::memory_order_relaxed)
        : probeInputChannel.load(std::memory_order_relaxed);
    if (selectedStart < 0 || selectedStart >= MaxChannels)
        return;

    const int starts[1] = { selectedStart };
    bool foundAnyChannel = false;
    int foundStartChannel = -1;
    int foundReadChannel = -1;
    float maxResidual = 0.0f;

    for (const int startChannel : starts)
    {
        for (int offset = 0; offset < PopProbeChannelPair; ++offset)
        {
            const int channel = startChannel + offset;
            const int readChannel = readOffset + channel;
            if (channel < 0 || channel >= MaxChannels || readChannel < 0 || readChannel >= buffer.inputChannels)
                continue;

            const float* raw = buffer.read[readChannel];
            const float* current = currentChannelPointer(buffer, readOffset, channel);
            if (raw == nullptr || current == nullptr)
                continue;

            if (!foundAnyChannel)
            {
                foundStartChannel = startChannel;
                foundReadChannel = readOffset + startChannel;
            }

            foundAnyChannel = true;
            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
            {
                const float residual = current[sample] >= raw[sample]
                    ? current[sample] - raw[sample]
                    : raw[sample] - current[sample];
                maxResidual = std::max(maxResidual, residual);
            }
        }

        if (foundAnyChannel)
            break;
    }

    if (!foundAnyChannel)
        return;

    residualProbeStartChannel.store(foundStartChannel, std::memory_order_relaxed);
    residualProbeReadChannel.store(foundReadChannel, std::memory_order_relaxed);

    const int residualPpm = std::clamp(static_cast<int>((maxResidual * 1000000.0f) + 0.5f), 0, 1000000);
    auto publishPeak = [residualPpm](std::atomic<int>& target) noexcept {
        int current = target.load(std::memory_order_relaxed);
        while (residualPpm > current &&
               !target.compare_exchange_weak(current, residualPpm, std::memory_order_relaxed, std::memory_order_relaxed))
        {
        }
    };

    if (position == ResidualProbePostCopy)
        publishPeak(postCopyResidualPeakPpm);
    else if (position == ResidualProbePrePlugin)
        publishPeak(prePluginResidualPeakPpm);
    else if (position == ResidualProbeFinal)
        publishPeak(finalResidualPeakPpm);

    if (maxResidual < ResidualProbeThreshold)
        return;

    if (position == ResidualProbePostCopy)
        postCopyResidualCount.fetch_add(1, std::memory_order_relaxed);
    else if (position == ResidualProbePrePlugin)
        prePluginResidualCount.fetch_add(1, std::memory_order_relaxed);
    else if (position == ResidualProbeFinal)
        finalResidualCount.fetch_add(1, std::memory_order_relaxed);
}
void RealtimeEngine::copyPassthrough(
    AudioBufferView buffer,
    int readOffset,
    bool suppressInputCallbackChannels) noexcept
{
    const int samples = std::max(0, buffer.samplesPerFrame);
    if (samples <= 0)
        return;

    const int channels = std::max(0, std::min(buffer.outputChannels, buffer.inputChannels - readOffset));
    const auto bytes = static_cast<size_t>(samples) * sizeof(float);

    for (int ch = 0; ch < channels; ++ch)
    {
        const float* in = buffer.read[readOffset + ch];
        float* out = buffer.write[ch];

        if (out == nullptr)
            continue;

        if (isInputCallbackSuppressed(suppressInputCallbackChannels, ch))
        {
            std::fill_n(out, samples, 0.0f);
            continue;
        }

        if (in == nullptr || in == out)
            continue;

        std::memcpy(out, in, bytes);
    }

    for (int ch = channels; ch < buffer.outputChannels; ++ch)
    {
        float* out = buffer.write[ch];
        if (out == nullptr)
            continue;

        std::fill_n(out, samples, 0.0f);
    }
}

void RealtimeEngine::applyConfiguredDelays(
    AudioBufferView buffer,
    CallbackStreamKind kind,
    int readOffset,
    int streamIndex,
    bool suppressInputCallbackChannels) noexcept
{
    streamIndex = std::clamp(streamIndex, 0, DelayStreamCount - 1);
    if (activeDelayChannelCounts[static_cast<size_t>(streamIndex)].load(std::memory_order_relaxed) <= 0)
        return;

    const auto buffers = dynamicBuffers.load(std::memory_order_acquire);
    if (buffers == nullptr ||
        buffers->delayLength <= 1 ||
        buffers->sampleRate <= 0 ||
        buffers->delayBuffer.empty())
    {
        return;
    }

    const int channels = std::max(0, std::min(buffer.outputChannels, buffer.inputChannels - readOffset));
    const int safeChannels = std::min(channels, MaxChannels);
    const auto& delayBank = delayBankFor(kind);
    const auto& enabledBank = enableBankFor(kind);
    const int length = buffers->delayLength;
    const int smoothingStep = delaySmoothingStepSamples(buffer.sampleRate, buffer.samplesPerFrame);

    for (int ch = 0; ch < safeChannels; ++ch)
    {
        if (isInputCallbackSuppressed(suppressInputCallbackChannels, ch))
            continue;

        float* out = buffer.write[ch];
        if (out == nullptr)
            continue;

        const int delayMs = delayBank[ch].load(std::memory_order_relaxed);
        const bool enabled = enabledBank[ch].load(std::memory_order_relaxed);
        if (!enabled || delayMs <= 0)
            continue;

        const int lineIndex = buffers->delayLineIndexes[static_cast<size_t>(streamIndex)][static_cast<size_t>(ch)];
        if (lineIndex < 0 || lineIndex >= buffers->delayLineCount)
            continue;

        const int targetDelaySamples = std::min(
            length - 1,
            static_cast<int>((static_cast<int64_t>(delayMs) * buffer.sampleRate) / 1000));
        if (targetDelaySamples <= 0)
            continue;

        auto& delayInitialized = smoothedDelayInitialized[static_cast<size_t>(streamIndex)][static_cast<size_t>(ch)];
        auto& smoothedDelay = smoothedDelaySamples[static_cast<size_t>(streamIndex)][static_cast<size_t>(ch)];
        if (!delayInitialized)
        {
            smoothedDelay = targetDelaySamples;
            delayInitialized = true;
        }
        else
        {
            smoothedDelay = moveToward(
                smoothedDelay,
                targetDelaySamples,
                smoothingStep);
        }
        const int delaySamples = std::clamp(smoothedDelay, 0, length - 1);

        const auto lineOffset = lineBufferOffset(lineIndex, length);
        if (lineOffset + static_cast<size_t>(length) > buffers->delayBuffer.size())
            continue;

        float* line = buffers->delayBuffer.data() + lineOffset;
        int write = delayWritePositions[static_cast<size_t>(streamIndex)][static_cast<size_t>(ch)];

        for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
        {
            const float dry = out[sample];
            line[write] = dry;

            int read = write - delaySamples;
            if (read < 0)
                read += length;

            out[sample] = line[read];

            ++write;
            if (write >= length)
                write = 0;
        }

        delayWritePositions[static_cast<size_t>(streamIndex)][static_cast<size_t>(ch)] = write;
    }
}

void RealtimeEngine::applyPlugins(
    AudioBufferView buffer,
    CallbackStreamKind kind,
    int readOffset,
    bool suppressInputCallbackChannels,
    std::array<bool, MaxChannels>& pluginOutputWritten) noexcept
{
    const int streamIndex = std::clamp(delayStreamIndex(kind), 0, DelayStreamCount - 1);
    if (activePluginSlotCounts[static_cast<size_t>(streamIndex)].load(std::memory_order_relaxed) <= 0)
        return;

    std::array<bool, MaxPluginSlots * MaxPluginPins> pluginBusWritten {};
    const auto scratchBuffers = dynamicPluginScratchBuffers.load(std::memory_order_acquire);

    const auto pluginBusPointer = [&scratchBuffers](int slot, int pin) noexcept -> float* {
        if (slot < 0 || slot >= MaxPluginSlots || pin < 0 || pin >= MaxPluginPins)
            return nullptr;

        if (scratchBuffers == nullptr ||
            scratchBuffers->pluginBusBuffer.empty())
            return nullptr;

        const auto index = pluginBusFlatIndex(slot, pin);
        if (index >= scratchBuffers->pluginBusLineIndexes.size())
            return nullptr;

        const int lineIndex = scratchBuffers->pluginBusLineIndexes[index];
        if (lineIndex < 0 || lineIndex >= scratchBuffers->pluginBusLineCount)
            return nullptr;

        const auto offset = lineBufferOffset(lineIndex, MaxPluginScratchSamples);
        if (offset + static_cast<size_t>(MaxPluginScratchSamples) > scratchBuffers->pluginBusBuffer.size())
            return nullptr;

        return scratchBuffers->pluginBusBuffer.data() + offset;
    };

    std::array<int, MaxPluginSlots> visitState {};
    const auto processSlot = [&](auto&& self, int slot) noexcept -> void {
        if (slot < 0 || slot >= MaxPluginSlots)
            return;

        if (visitState[static_cast<size_t>(slot)] == 2)
            return;

        if (visitState[static_cast<size_t>(slot)] == 1)
            return;

        visitState[static_cast<size_t>(slot)] = 1;
        auto finish = [&]() noexcept {
            visitState[static_cast<size_t>(slot)] = 2;
        };

        auto& pluginSlot = pluginSlots[static_cast<size_t>(slot)];

        if (!pluginSlot.enabled.load(std::memory_order_relaxed))
        {
            finish();
            return;
        }

        if (pluginSlot.kind.load(std::memory_order_relaxed) != static_cast<int>(kind))
        {
            finish();
            return;
        }

        auto* processor = pluginSlot.processor.load(std::memory_order_acquire);
        if (processor == nullptr)
        {
            finish();
            return;
        }

        const int inputRouteCount = std::clamp(pluginSlot.inputRouteCount.load(std::memory_order_acquire), 0, MaxPluginRoutes);
        const int outputRouteCount = std::clamp(pluginSlot.outputRouteCount.load(std::memory_order_acquire), 0, MaxPluginRoutes);
        if (inputRouteCount == 0 || buffer.samplesPerFrame > MaxPluginScratchSamples)
        {
            finish();
            return;
        }

        for (int route = 0; route < inputRouteCount; ++route)
        {
            const int sourceKind = pluginSlot.inputSourceKinds[static_cast<size_t>(route)].load(std::memory_order_relaxed);
            if (sourceKind != static_cast<int>(PluginRouteEndpointKind::PluginPin))
                continue;

            const int sourceSlot = pluginSlot.inputSourceSlots[static_cast<size_t>(route)].load(std::memory_order_relaxed);
            if (sourceSlot >= 0 && sourceSlot < MaxPluginSlots && sourceSlot != slot)
                self(self, sourceSlot);
        }

        std::array<PluginAudioInputRoute, MaxPluginRoutes> inputRoutes {};
        std::array<PluginAudioOutputRoute, MaxPluginRoutes> outputRoutes {};
        int validInputRoutes = 0;
        int validOutputRoutes = 0;

        for (int route = 0; route < inputRouteCount; ++route)
        {
            const int sourceKind = pluginSlot.inputSourceKinds[static_cast<size_t>(route)].load(std::memory_order_relaxed);
            const int source = pluginSlot.inputSourceChannels[static_cast<size_t>(route)].load(std::memory_order_relaxed);
            const int sourceSlot = pluginSlot.inputSourceSlots[static_cast<size_t>(route)].load(std::memory_order_relaxed);
            const int sourcePin = pluginSlot.inputSourcePins[static_cast<size_t>(route)].load(std::memory_order_relaxed);
            const int pin = pluginSlot.inputPluginPins[static_cast<size_t>(route)].load(std::memory_order_relaxed);

            const float* sourcePointer = nullptr;

            if (sourceKind == static_cast<int>(PluginRouteEndpointKind::VoiceMeeterChannel))
            {
                if (source >= 0 &&
                    source < MaxChannels &&
                    !isInputCallbackSuppressed(suppressInputCallbackChannels, source))
                {
                    sourcePointer = kind == CallbackStreamKind::Main
                        ? sourceChannelPointer(buffer, readOffset, source)
                        : currentChannelPointer(buffer, readOffset, source);
                }
            }
            else if (sourceKind == static_cast<int>(PluginRouteEndpointKind::PluginPin) &&
                     sourceSlot >= 0 &&
                     sourceSlot < MaxPluginSlots &&
                     sourcePin >= 0 &&
                     sourcePin < MaxPluginPins)
            {
                const auto busIndex = pluginBusFlatIndex(sourceSlot, sourcePin);
                if (busIndex < pluginBusWritten.size() && pluginBusWritten[busIndex])
                    sourcePointer = pluginBusPointer(sourceSlot, sourcePin);
            }

            if (sourcePointer != nullptr && pin >= 0)
                inputRoutes[static_cast<size_t>(validInputRoutes++)] = PluginAudioInputRoute { sourcePointer, pin };
        }

        for (int route = 0; route < outputRouteCount; ++route)
        {
            const int destinationKind = pluginSlot.outputDestinationKinds[static_cast<size_t>(route)].load(std::memory_order_relaxed);
            const int pin = pluginSlot.outputPluginPins[static_cast<size_t>(route)].load(std::memory_order_relaxed);
            const int destination = pluginSlot.outputDestinationChannels[static_cast<size_t>(route)].load(std::memory_order_relaxed);
            const int destinationSlot = pluginSlot.outputDestinationSlots[static_cast<size_t>(route)].load(std::memory_order_relaxed);
            const int destinationPin = pluginSlot.outputDestinationPins[static_cast<size_t>(route)].load(std::memory_order_relaxed);

            float* destinationPointer = nullptr;
            int resolvedDestinationChannel = -1;
            int resolvedDestinationSlot = -1;
            int resolvedDestinationPin = -1;

            if (destinationKind == static_cast<int>(PluginRouteEndpointKind::VoiceMeeterChannel))
            {
                if (destination >= 0 &&
                    destination < buffer.outputChannels &&
                    destination < MaxChannels &&
                    !isInputCallbackSuppressed(suppressInputCallbackChannels, destination))
                {
                    destinationPointer = buffer.write[destination];
                    resolvedDestinationChannel = destination;
                }
            }
            else if (destinationKind == static_cast<int>(PluginRouteEndpointKind::PluginPin) &&
                     destinationSlot >= 0 &&
                     destinationSlot < MaxPluginSlots &&
                     destinationSlot != slot &&
                     destinationPin >= 0 &&
                     destinationPin < MaxPluginPins)
            {
                destinationPointer = pluginBusPointer(slot, pin);
                resolvedDestinationSlot = slot;
                resolvedDestinationPin = pin;
            }

            if (destinationPointer != nullptr && pin >= 0)
            {
                outputRoutes[static_cast<size_t>(validOutputRoutes++)] = PluginAudioOutputRoute {
                    destinationPointer,
                    pin,
                    resolvedDestinationChannel,
                    resolvedDestinationSlot,
                    resolvedDestinationPin
                };
            }
        }

        if (validInputRoutes > 0)
        {
            const bool bypassed = pluginSlot.bypassed.load(std::memory_order_relaxed);
            bool rendered = false;

            if (bypassed)
            {
                std::array<float*, MaxPluginRoutes> clearedDestinations {};
                int clearedCount = 0;

                for (int outputIndex = 0; outputIndex < validOutputRoutes; ++outputIndex)
                {
                    auto& outputRoute = outputRoutes[static_cast<size_t>(outputIndex)];
                    auto* destination = outputRoute.destination;
                    if (destination == nullptr)
                        continue;

                    const PluginAudioInputRoute* passthroughInput = nullptr;
                    for (int inputIndex = 0; inputIndex < validInputRoutes; ++inputIndex)
                    {
                        const auto& inputRoute = inputRoutes[static_cast<size_t>(inputIndex)];
                        if (inputRoute.pluginPin == outputRoute.pluginPin && inputRoute.source != nullptr)
                        {
                            passthroughInput = &inputRoute;
                            break;
                        }
                    }

                    if (passthroughInput == nullptr)
                        continue;

                    if (passthroughInput->source == destination)
                    {
                        rendered = true;
                        continue;
                    }

                    bool alreadyCleared = false;
                    for (int i = 0; i < clearedCount; ++i)
                        alreadyCleared = alreadyCleared || clearedDestinations[static_cast<size_t>(i)] == destination;

                    if (!alreadyCleared)
                    {
                        for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                            destination[sample] = 0.0f;

                        if (clearedCount < static_cast<int>(clearedDestinations.size()))
                            clearedDestinations[static_cast<size_t>(clearedCount++)] = destination;
                    }

                    for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                        destination[sample] += passthroughInput->source[sample];

                    rendered = true;
                }
            }
            else
            {
                rendered = processor->process(
                    buffer,
                    PluginRoutingView {
                        inputRoutes.data(),
                        validInputRoutes,
                        outputRoutes.data(),
                        validOutputRoutes
                    });
            }

            if (rendered)
            {
                for (int route = 0; route < validOutputRoutes; ++route)
                {
                    const auto& outputRoute = outputRoutes[static_cast<size_t>(route)];
                    const int destination = outputRoute.destinationChannel;
                    if (destination >= 0 && destination < MaxChannels)
                        pluginOutputWritten[static_cast<size_t>(destination)] = true;

                    if (outputRoute.destinationSlot >= 0 &&
                        outputRoute.destinationSlot < MaxPluginSlots &&
                        outputRoute.destinationPin >= 0 &&
                        outputRoute.destinationPin < MaxPluginPins)
                    {
                        const auto busIndex = pluginBusFlatIndex(outputRoute.destinationSlot, outputRoute.destinationPin);
                        if (busIndex < pluginBusWritten.size())
                            pluginBusWritten[busIndex] = true;
                    }
                }
            }
        }

        finish();
    };

    for (int slot = 0; slot < MaxPluginSlots; ++slot)
        processSlot(processSlot, slot);
}

void RealtimeEngine::applyDirectRoutes(
    AudioBufferView buffer,
    CallbackStreamKind kind,
    int readOffset,
    bool enableInputOutputRoutes,
    bool suppressInputCallbackChannels,
    std::array<bool, MaxChannels>& pluginOutputWritten) noexcept
{
    if (kind == CallbackStreamKind::InputInsert)
    {
        if (enableInputOutputRoutes && inputDirectRoutes.routeCount.load(std::memory_order_acquire) > 0)
            captureInputRoutes(buffer, readOffset, suppressInputCallbackChannels, pluginOutputWritten);
        return;
    }

    if (kind == CallbackStreamKind::OutputInsert)
    {
        if (enableInputOutputRoutes && inputDirectRoutes.routeCount.load(std::memory_order_acquire) > 0)
            mixCapturedInputRoutes(buffer, pluginOutputWritten);
        if (outputDirectRoutes.routeCount.load(std::memory_order_acquire) > 0)
            applySameBufferDirectRoutes(buffer, kind, readOffset, suppressInputCallbackChannels, pluginOutputWritten);
        return;
    }

    if (mainDirectRoutes.routeCount.load(std::memory_order_acquire) > 0)
        applySameBufferDirectRoutes(buffer, kind, readOffset, suppressInputCallbackChannels, pluginOutputWritten);
}

void RealtimeEngine::applyPluginPassthroughRoutes(
    AudioBufferView buffer,
    CallbackStreamKind kind,
    int readOffset,
    int streamIndex,
    bool suppressInputCallbackChannels,
    std::array<bool, MaxChannels>& pluginOutputWritten) noexcept
{
    const auto& routeBank = pluginPassthroughBankFor(kind);
    const int routeCount = std::clamp(routeBank.routeCount.load(std::memory_order_acquire), 0, MaxDirectRoutes);
    if (routeCount <= 0)
        return;

    streamIndex = std::clamp(streamIndex, 0, DelayStreamCount - 1);
    const auto scratchBuffers = dynamicPluginScratchBuffers.load(std::memory_order_acquire);
    std::array<bool, MaxChannels> gatedPairs {};
    const bool gateSameBufferRoutes = kind != CallbackStreamKind::Main;
    const int passthroughCapacity = scratchBuffers != nullptr
        ? scratchBuffers->passthroughRouteCapacities[static_cast<size_t>(streamIndex)]
        : 0;
    const int passthroughStartLine = scratchBuffers != nullptr
        ? scratchBuffers->passthroughRouteStartLines[static_cast<size_t>(streamIndex)]
        : -1;
    const bool canSnapshotRoutes =
        gateSameBufferRoutes &&
        buffer.samplesPerFrame >= 0 &&
        buffer.samplesPerFrame <= MaxPluginScratchSamples &&
        scratchBuffers != nullptr &&
        passthroughStartLine >= 0 &&
        passthroughCapacity >= routeCount &&
        !scratchBuffers->pluginPassthroughScratchBuffer.empty();

    std::array<int, MaxDirectRoutes> routeDestinations {};
    std::array<const float*, MaxDirectRoutes> routeInputs {};
    int validRouteCount = 0;

    for (int route = 0; route < routeCount; ++route)
    {
        const int source = routeBank.sourceChannels[static_cast<size_t>(route)].load(std::memory_order_relaxed);
        const int destination = routeBank.destinationChannels[static_cast<size_t>(route)].load(std::memory_order_relaxed);

        if (source < 0 || source >= MaxChannels)
            continue;

        if (destination < 0)
        {
            if (isInputCallbackSuppressed(suppressInputCallbackChannels, source))
                continue;

            if (gateSameBufferRoutes)
            {
                markStereoPair(gatedPairs, source);
            }
            else
            {
                const int pairBase = source - (source % 2);
                const int channels = std::max(0, std::min(buffer.outputChannels, MaxChannels));
                for (int channel = pairBase; channel < pairBase + 2 && channel < channels; ++channel)
                {
                    if (channel < 0 || pluginOutputWritten[static_cast<size_t>(channel)])
                        continue;

                    float* output = buffer.write[channel];
                    if (output == nullptr)
                        continue;

                    for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                        output[sample] = 0.0f;

                    pluginOutputWritten[static_cast<size_t>(channel)] = true;
                }
            }

            continue;
        }

        if (destination >= buffer.outputChannels || destination >= MaxChannels)
            continue;

        if (isInputCallbackSuppressed(suppressInputCallbackChannels, source) ||
            isInputCallbackSuppressed(suppressInputCallbackChannels, destination))
        {
            continue;
        }

        const float* input = kind == CallbackStreamKind::Main
            ? sourceChannelPointer(buffer, readOffset, source)
            : currentChannelPointer(buffer, readOffset, source);
        float* output = buffer.write[destination];
        if (input == nullptr || output == nullptr)
            continue;

        if (gateSameBufferRoutes)
        {
            markStereoPair(gatedPairs, source);
            markStereoPair(gatedPairs, destination);
        }

        routeDestinations[static_cast<size_t>(validRouteCount)] = destination;

        if (canSnapshotRoutes)
        {
            const int lineIndex = passthroughStartLine + validRouteCount;
            const auto snapshotOffset = lineBufferOffset(lineIndex, MaxPluginScratchSamples);
            float* snapshot = nullptr;
            if (snapshotOffset + static_cast<size_t>(MaxPluginScratchSamples) <=
                scratchBuffers->pluginPassthroughScratchBuffer.size())
            {
                snapshot = scratchBuffers->pluginPassthroughScratchBuffer.data() + snapshotOffset;
            }

            if (snapshot == nullptr)
            {
                routeInputs[static_cast<size_t>(validRouteCount)] = input;
                ++validRouteCount;
                continue;
            }

            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                snapshot[sample] = input[sample];

            routeInputs[static_cast<size_t>(validRouteCount)] = snapshot;
        }
        else
        {
            routeInputs[static_cast<size_t>(validRouteCount)] = input;
        }

        ++validRouteCount;
    }

    for (int route = 0; route < validRouteCount; ++route)
    {
        const int destination = routeDestinations[static_cast<size_t>(route)];
        const float* input = routeInputs[static_cast<size_t>(route)];
        float* output = buffer.write[destination];
        if (input == nullptr || output == nullptr)
            continue;

        if (!gateSameBufferRoutes && input == output)
        {
            pluginOutputWritten[static_cast<size_t>(destination)] = true;
            continue;
        }

        if (!pluginOutputWritten[static_cast<size_t>(destination)])
        {
            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                output[sample] = 0.0f;
        }

        for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
            output[sample] += input[sample];

        pluginOutputWritten[static_cast<size_t>(destination)] = true;
    }

    if (!gateSameBufferRoutes)
        return;

    const int channels = std::max(0, std::min(buffer.outputChannels, MaxChannels));
    for (int channel = 0; channel < channels; ++channel)
    {
        if (!gatedPairs[static_cast<size_t>(channel)] ||
            pluginOutputWritten[static_cast<size_t>(channel)])
        {
            continue;
        }

        float* output = buffer.write[channel];
        if (output == nullptr)
            continue;

        for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
            output[sample] = 0.0f;

        pluginOutputWritten[static_cast<size_t>(channel)] = true;
    }
}

void RealtimeEngine::processSinglePingInput(AudioBufferView buffer) noexcept
{
    if (singlePingStatus.load(std::memory_order_acquire) != PingStatusArmed)
        return;

    const int channel = singlePingInputChannel.load(std::memory_order_relaxed);
    if (channel < 0 || channel >= buffer.outputChannels || channel >= MaxChannels)
    {
        singlePingStatus.store(PingStatusTimeout, std::memory_order_release);
        return;
    }

    float* output = buffer.write[channel];
    if (output == nullptr)
    {
        singlePingStatus.store(PingStatusTimeout, std::memory_order_release);
        return;
    }

    const int currentSampleRate = buffer.sampleRate > 0
        ? buffer.sampleRate
        : std::max(8000, sampleRate.load(std::memory_order_acquire));
    const int timeoutMilliseconds = singlePingTimeoutMilliseconds.load(std::memory_order_relaxed);
    singlePingSampleRate.store(currentSampleRate, std::memory_order_relaxed);
    singlePingTimeoutSamples.store(
        static_cast<int>((static_cast<int64_t>(timeoutMilliseconds) * currentSampleRate) / 1000),
        std::memory_order_relaxed);
    singlePingElapsedSamples.store(0, std::memory_order_relaxed);
    singlePingLatencySamples.store(-1, std::memory_order_relaxed);
    singlePingPeakPercent.store(0, std::memory_order_relaxed);

    const int pulseSamples = std::min(PingPulseSamples, std::max(0, buffer.samplesPerFrame));
    for (int sample = 0; sample < pulseSamples; ++sample)
    {
        const float sign = (sample % 2) == 0 ? 1.0f : -1.0f;
        output[sample] += sign * PingPulseAmplitude;
    }

    singlePingPulsePosition.store(pulseSamples, std::memory_order_relaxed);
    singlePingStatus.store(PingStatusWaiting, std::memory_order_release);
}

void RealtimeEngine::processSinglePingOutput(AudioBufferView buffer) noexcept
{
    if (singlePingStatus.load(std::memory_order_acquire) != PingStatusWaiting)
        return;

    const int channel = singlePingOutputChannel.load(std::memory_order_relaxed);
    if (channel < 0 || channel >= buffer.outputChannels || channel >= MaxChannels)
    {
        singlePingStatus.store(PingStatusTimeout, std::memory_order_release);
        return;
    }

    const float* input = buffer.write[channel];
    if (input == nullptr)
    {
        singlePingStatus.store(PingStatusTimeout, std::memory_order_release);
        return;
    }

    int elapsed = singlePingElapsedSamples.load(std::memory_order_relaxed);
    for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
    {
        const float absolute = input[sample] < 0.0f ? -input[sample] : input[sample];
        if (absolute < PingDetectThreshold)
            continue;

        singlePingLatencySamples.store(elapsed + sample, std::memory_order_relaxed);
        singlePingPeakPercent.store(
            std::clamp(static_cast<int>(absolute * 100.0f), 0, 1000),
            std::memory_order_relaxed);
        singlePingStatus.store(PingStatusDetected, std::memory_order_release);
        return;
    }

    elapsed += std::max(0, buffer.samplesPerFrame);
    singlePingElapsedSamples.store(elapsed, std::memory_order_relaxed);
    if (elapsed >= singlePingTimeoutSamples.load(std::memory_order_relaxed))
        singlePingStatus.store(PingStatusTimeout, std::memory_order_release);
}

void RealtimeEngine::captureInputRoutes(
    AudioBufferView buffer,
    int readOffset,
    bool suppressInputCallbackChannels,
    std::array<bool, MaxChannels>& pluginOutputWritten) noexcept
{
    const auto& routeBank = inputDirectRoutes;
    const int routeCount = std::clamp(routeBank.routeCount.load(std::memory_order_acquire), 0, MaxDirectRoutes);
    if (routeCount <= 0)
        return;

    const auto buffers = dynamicBuffers.load(std::memory_order_acquire);
    if (buffers == nullptr ||
        buffers->routeLength <= 0 ||
        buffers->routeBuffer.empty())
    {
        return;
    }

    const int length = buffers->routeLength;
    std::array<bool, MaxChannels> muteSources {};

    for (int route = 0; route < routeCount; ++route)
    {
        const int source = routeBank.sourceChannels[static_cast<size_t>(route)].load(std::memory_order_relaxed);
        if (source < 0 || source >= MaxChannels)
            continue;

        if (isInputCallbackSuppressed(suppressInputCallbackChannels, source))
            continue;

        const float* input = currentChannelPointer(buffer, readOffset, source);
        if (input == nullptr)
            continue;

        const int lineIndex = buffers->routeLineIndexes[0][static_cast<size_t>(route)];
        if (lineIndex < 0 || lineIndex >= buffers->routeLineCount)
            continue;

        const int gainPercent = routeBank.gainPercent[static_cast<size_t>(route)].load(std::memory_order_relaxed);
        auto& currentGain = smoothedRouteGains[0][static_cast<size_t>(route)];
        const float targetGain = gainFromPercent(gainPercent);
        const float gainStep = buffer.samplesPerFrame > 0
            ? (targetGain - currentGain) / static_cast<float>(buffer.samplesPerFrame)
            : 0.0f;
        float gain = currentGain;
        const auto lineOffset = lineBufferOffset(lineIndex, length);
        if (lineOffset + static_cast<size_t>(length) > buffers->routeBuffer.size())
            continue;

        float* fifo = buffers->routeBuffer.data() + lineOffset;

        int write = routeWritePositions[0][static_cast<size_t>(route)].load(std::memory_order_relaxed);
        int read = routeReadPositions[static_cast<size_t>(route)].load(std::memory_order_acquire);
        const int previouslyAvailable = routePrimedSamples[0][static_cast<size_t>(route)].load(std::memory_order_acquire);

        for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
        {
            gain += gainStep;
            fifo[write] = input[sample] * gain;
            ++write;
            if (write >= length)
                write = 0;
        }

        const int writtenSamples = std::max(0, buffer.samplesPerFrame);
        const int capacity = std::max(0, length - 1);
        const int overflow = std::max(0, previouslyAvailable + writtenSamples - capacity);
        if (overflow > 0)
        {
            read = advanceRingPosition(read, overflow, length);
            routeReadPositions[static_cast<size_t>(route)].store(read, std::memory_order_release);
        }

        currentGain = targetGain;
        routeWritePositions[0][static_cast<size_t>(route)].store(write, std::memory_order_release);
        routePrimedSamples[0][static_cast<size_t>(route)].store(
            std::min(capacity, previouslyAvailable + writtenSamples),
            std::memory_order_release);

        if (routeBank.muteSource[static_cast<size_t>(route)].load(std::memory_order_relaxed))
            muteSources[static_cast<size_t>(source)] = true;
    }

    const int channels = std::min(buffer.outputChannels, MaxChannels);
    for (int source = 0; source < channels; ++source)
    {
        if (!muteSources[static_cast<size_t>(source)])
            continue;

        float* output = buffer.write[source];
        if (output == nullptr)
            continue;

        for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
            output[sample] = 0.0f;

        pluginOutputWritten[static_cast<size_t>(source)] = true;
    }
}

void RealtimeEngine::mixCapturedInputRoutes(
    AudioBufferView buffer,
    std::array<bool, MaxChannels>& pluginOutputWritten) noexcept
{
    const auto& routeBank = inputDirectRoutes;
    const int routeCount = std::clamp(routeBank.routeCount.load(std::memory_order_acquire), 0, MaxDirectRoutes);
    if (routeCount <= 0)
        return;

    const auto buffers = dynamicBuffers.load(std::memory_order_acquire);
    if (buffers == nullptr ||
        buffers->routeLength <= 0 ||
        buffers->routeBuffer.empty())
    {
        return;
    }

    const int length = buffers->routeLength;
    for (int route = 0; route < routeCount; ++route)
    {
        const int destination = routeBank.destinationChannels[static_cast<size_t>(route)].load(std::memory_order_relaxed);
        if (destination < 0 || destination >= buffer.outputChannels || destination >= MaxChannels)
            continue;

        float* output = buffer.write[destination];
        if (output == nullptr)
            continue;

        const int lineIndex = buffers->routeLineIndexes[0][static_cast<size_t>(route)];
        if (lineIndex < 0 || lineIndex >= buffers->routeLineCount)
            continue;

        const int delayMs = routeBank.delayMilliseconds[static_cast<size_t>(route)].load(std::memory_order_relaxed);
        const int delaySamples = std::clamp(
            buffer.sampleRate > 0 ? static_cast<int>((static_cast<int64_t>(delayMs) * buffer.sampleRate) / 1000) : 0,
            0,
            length - 1);
        const int safetySamples = std::min(
            std::max(0, length - buffer.samplesPerFrame - delaySamples - 1),
            capturedInputRouteSafetySamples(buffer));
        int availableSamples = routePrimedSamples[0][static_cast<size_t>(route)].load(std::memory_order_acquire);
        const int blockSamples = std::max(0, buffer.samplesPerFrame);
        const int minimumSamples = delaySamples + blockSamples;
        const int neededSamples = minimumSamples + safetySamples;
        if (availableSamples < neededSamples)
        {
            routeFifoWaitCount.fetch_add(1, std::memory_order_relaxed);
            continue;
        }

        const auto lineOffset = lineBufferOffset(lineIndex, length);
        if (lineOffset + static_cast<size_t>(length) > buffers->routeBuffer.size())
            continue;

        float* fifo = buffers->routeBuffer.data() + lineOffset;
        int read = routeReadPositions[static_cast<size_t>(route)].load(std::memory_order_acquire);
        const int extraBufferedSamples = availableSamples - neededSamples;
        if (extraBufferedSamples > 0)
        {
            read = advanceRingPosition(read, extraBufferedSamples, length);
            availableSamples -= extraBufferedSamples;
        }

        for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
        {
            output[sample] += fifo[read];
            ++read;
            if (read >= length)
                read = 0;
        }

        routeReadPositions[static_cast<size_t>(route)].store(read, std::memory_order_release);
        routePrimedSamples[0][static_cast<size_t>(route)].store(
            std::max(0, availableSamples - blockSamples),
            std::memory_order_release);

        pluginOutputWritten[static_cast<size_t>(destination)] = true;
    }
}

void RealtimeEngine::applySameBufferDirectRoutes(
    AudioBufferView buffer,
    CallbackStreamKind kind,
    int readOffset,
    bool suppressInputCallbackChannels,
    std::array<bool, MaxChannels>& pluginOutputWritten) noexcept
{
    const auto& routeBank = directRouteBankFor(kind);
    const int routeCount = std::clamp(routeBank.routeCount.load(std::memory_order_acquire), 0, MaxDirectRoutes);
    if (routeCount <= 0)
        return;

    const int streamIndex = delayStreamIndex(kind);
    const auto buffers = dynamicBuffers.load(std::memory_order_acquire);

    for (int route = 0; route < routeCount; ++route)
    {
        const int source = routeBank.sourceChannels[static_cast<size_t>(route)].load(std::memory_order_relaxed);
        const int destination = routeBank.destinationChannels[static_cast<size_t>(route)].load(std::memory_order_relaxed);

        if (source < 0 || source >= buffer.outputChannels || source >= MaxChannels)
            continue;

        if (destination < 0)
            continue;

        if (destination >= buffer.outputChannels || destination >= MaxChannels)
            continue;

        if (isInputCallbackSuppressed(suppressInputCallbackChannels, source) ||
            isInputCallbackSuppressed(suppressInputCallbackChannels, destination))
        {
            continue;
        }

        const float* input = kind == CallbackStreamKind::Main
            ? sourceChannelPointer(buffer, readOffset, source)
            : currentChannelPointer(buffer, readOffset, source);
        float* output = buffer.write[destination];
        if (input == nullptr || output == nullptr)
            continue;

        const int gainPercent = routeBank.gainPercent[static_cast<size_t>(route)].load(std::memory_order_relaxed);
        auto& currentGain = smoothedRouteGains[static_cast<size_t>(streamIndex)][static_cast<size_t>(route)];
        const float targetGain = gainFromPercent(gainPercent);
        const float gainStep = buffer.samplesPerFrame > 0
            ? (targetGain - currentGain) / static_cast<float>(buffer.samplesPerFrame)
            : 0.0f;
        float gain = currentGain;

        const int delayMs = routeBank.delayMilliseconds[static_cast<size_t>(route)].load(std::memory_order_relaxed);
        const bool delayRequested = delayMs > 0;
        int length = 0;
        int delaySamples = 0;
        int lineIndex = -1;
        if (delayRequested && buffers != nullptr)
        {
            length = buffers->routeLength;
            lineIndex = buffers->routeLineIndexes[static_cast<size_t>(streamIndex)][static_cast<size_t>(route)];
            delaySamples = std::clamp(
                buffer.sampleRate > 0 ? static_cast<int>((static_cast<int64_t>(delayMs) * buffer.sampleRate) / 1000) : 0,
                0,
                std::max(0, length - 1));
        }

        const bool useDelay =
            delaySamples > 0 &&
            length > 1 &&
            buffers != nullptr &&
            lineIndex >= 0 &&
            lineIndex < buffers->routeLineCount &&
            !buffers->routeBuffer.empty();

        if (delayRequested && !useDelay)
        {
            currentGain = targetGain;
            if (kind != CallbackStreamKind::Main)
            {
                for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                    output[sample] = 0.0f;

                if (routeBank.muteSource[static_cast<size_t>(route)].load(std::memory_order_relaxed) &&
                    source < buffer.outputChannels)
                {
                    float* sourceOutput = buffer.write[source];
                    if (sourceOutput != nullptr)
                    {
                        for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                            sourceOutput[sample] = 0.0f;
                    }
                }
            }

            pluginOutputWritten[static_cast<size_t>(destination)] = true;
            continue;
        }

        if (useDelay)
        {
            const auto lineOffset = lineBufferOffset(lineIndex, length);
            if (lineOffset + static_cast<size_t>(length) > buffers->routeBuffer.size())
                continue;

            float* fifo = buffers->routeBuffer.data() + lineOffset;
            int write = routeWritePositions[static_cast<size_t>(streamIndex)][static_cast<size_t>(route)].load(std::memory_order_relaxed);
            const int previouslyPrimed = routePrimedSamples[static_cast<size_t>(streamIndex)][static_cast<size_t>(route)].load(std::memory_order_relaxed);
            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
            {
                gain += gainStep;
                fifo[write] = input[sample] * gain;
                ++write;
                if (write >= length)
                    write = 0;
            }

            routeWritePositions[static_cast<size_t>(streamIndex)][static_cast<size_t>(route)].store(write, std::memory_order_release);
            const int primedSamples = std::min(length, previouslyPrimed + std::max(0, buffer.samplesPerFrame));
            routePrimedSamples[static_cast<size_t>(streamIndex)][static_cast<size_t>(route)].store(
                primedSamples,
                std::memory_order_release);

            if (primedSamples < delaySamples + buffer.samplesPerFrame)
            {
                currentGain = targetGain;
                if (kind != CallbackStreamKind::Main)
                {
                    for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                        output[sample] = 0.0f;

                    if (routeBank.muteSource[static_cast<size_t>(route)].load(std::memory_order_relaxed) &&
                        source < buffer.outputChannels)
                    {
                        float* sourceOutput = buffer.write[source];
                        if (sourceOutput != nullptr)
                        {
                            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                                sourceOutput[sample] = 0.0f;
                        }
                    }
                }

                pluginOutputWritten[static_cast<size_t>(destination)] = true;
                continue;
            }

            int read = write - delaySamples - buffer.samplesPerFrame;
            while (read < 0)
                read += length;
            while (read >= length)
                read -= length;

            if (kind == CallbackStreamKind::Main)
            {
                for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                {
                    output[sample] += fifo[read];
                    ++read;
                    if (read >= length)
                        read = 0;
                }
            }
            else
            {
                for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                {
                    output[sample] = fifo[read];
                    ++read;
                    if (read >= length)
                        read = 0;
                }
            }

            currentGain = targetGain;
        }
        else if (input != output)
        {
            if (kind == CallbackStreamKind::Main)
            {
                for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                {
                    gain += gainStep;
                    output[sample] += input[sample] * gain;
                }
            }
            else
            {
                for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                {
                    gain += gainStep;
                    output[sample] = input[sample] * gain;
                }
            }

            currentGain = targetGain;
        }

        if (kind != CallbackStreamKind::Main &&
            routeBank.muteSource[static_cast<size_t>(route)].load(std::memory_order_relaxed) &&
            source < buffer.outputChannels)
        {
            float* sourceOutput = buffer.write[source];
            if (sourceOutput != nullptr)
            {
                for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                    sourceOutput[sample] = 0.0f;
            }
        }

        pluginOutputWritten[static_cast<size_t>(destination)] = true;
    }
}

void RealtimeEngine::applyPluginGraphGate(
    AudioBufferView buffer,
    CallbackStreamKind kind,
    bool suppressInputCallbackChannels,
    const std::array<bool, MaxChannels>& pluginOutputWritten) noexcept
{
    const int streamIndex = std::clamp(delayStreamIndex(kind), 0, DelayStreamCount - 1);
    if (activePluginGraphChannelCounts[static_cast<size_t>(streamIndex)].load(std::memory_order_relaxed) <= 0)
        return;

    const int channels = std::max(0, std::min(buffer.outputChannels, MaxChannels));
    const auto& graphBank = pluginGraphBankFor(kind);

    for (int ch = 0; ch < channels; ++ch)
    {
        if (isInputCallbackSuppressed(suppressInputCallbackChannels, ch))
            continue;

        if (!graphBank[static_cast<size_t>(ch)].load(std::memory_order_relaxed))
            continue;

        if (pluginOutputWritten[static_cast<size_t>(ch)])
            continue;

        float* out = buffer.write[ch];
        if (out == nullptr)
            continue;

        for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
            out[sample] = 0.0f;
    }
}

void RealtimeEngine::applyConfiguredGains(
    AudioBufferView buffer,
    CallbackStreamKind kind,
    int streamIndex,
    bool suppressInputCallbackChannels) noexcept
{
    const int channels = std::max(0, std::min(buffer.outputChannels, buffer.inputChannels - getReadOffset(buffer, kind)));
    const int safeChannels = std::min(channels, MaxChannels);
    const auto& gainBank = gainBankFor(kind);
    const auto& enabledBank = enableBankFor(kind);
    streamIndex = std::clamp(streamIndex, 0, DelayStreamCount - 1);

    for (int ch = 0; ch < safeChannels; ++ch)
    {
        if (isInputCallbackSuppressed(suppressInputCallbackChannels, ch))
            continue;

        if (!enabledBank[ch].load(std::memory_order_relaxed))
        {
            smoothedChannelGains[static_cast<size_t>(streamIndex)][static_cast<size_t>(ch)] = 1.0f;
            continue;
        }

        const int gainPercent = gainBank[ch].load(std::memory_order_relaxed);
        auto& currentGain = smoothedChannelGains[static_cast<size_t>(streamIndex)][static_cast<size_t>(ch)];
        const float targetGain = gainFromPercent(gainPercent);
        if (gainPercent == 100 && currentGain == 1.0f)
            continue;

        float* out = buffer.write[ch];
        if (out == nullptr)
            continue;

        const float gainStep = buffer.samplesPerFrame > 0
            ? (targetGain - currentGain) / static_cast<float>(buffer.samplesPerFrame)
            : 0.0f;
        float gain = currentGain;
        for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
        {
            gain += gainStep;
            out[sample] *= gain;
        }

        currentGain = targetGain;
    }
}

void RealtimeEngine::captureProbeRead(AudioBufferView buffer, CallbackStreamKind kind, int readOffset) noexcept
{
    const int inputChannel = probeInputChannel.load(std::memory_order_relaxed);
    const int outputChannel = probeOutputChannel.load(std::memory_order_relaxed);

    if (kind == CallbackStreamKind::InputInsert)
    {
        inputInsertInputChannels.store(buffer.inputChannels, std::memory_order_relaxed);
        inputInsertOutputChannels.store(buffer.outputChannels, std::memory_order_relaxed);
        inputInsertReadPeakPercent.store(
            peakPercent(readChannelPointer(buffer, inputChannel), buffer.samplesPerFrame),
            std::memory_order_relaxed);
        const auto inputPeak = maxReadPeak(buffer, 0, buffer.inputChannels, 0);
        inputInsertMaxReadPeakPercent.store(inputPeak.percent, std::memory_order_relaxed);
        inputInsertMaxReadChannel.store(inputPeak.channel, std::memory_order_relaxed);
        return;
    }

    if (kind == CallbackStreamKind::Main)
    {
        mainInputChannels.store(buffer.inputChannels, std::memory_order_relaxed);
        mainOutputChannels.store(buffer.outputChannels, std::memory_order_relaxed);
        mainInputReadPeakPercent.store(
            peakPercent(readChannelPointer(buffer, inputChannel), buffer.samplesPerFrame),
            std::memory_order_relaxed);
        mainOutputReadPeakPercent.store(
            peakPercent(readChannelPointer(buffer, readOffset + outputChannel), buffer.samplesPerFrame),
            std::memory_order_relaxed);
        const int sourceChannels = std::clamp(readOffset, 0, buffer.inputChannels);
        const int busChannels = std::clamp(buffer.inputChannels - readOffset, 0, buffer.outputChannels);
        const auto sourcePeak = maxReadPeak(buffer, 0, sourceChannels, 0);
        const auto busPeak = maxReadPeak(buffer, readOffset, busChannels, readOffset);
        mainSourceMaxReadPeakPercent.store(sourcePeak.percent, std::memory_order_relaxed);
        mainSourceMaxReadChannel.store(sourcePeak.channel, std::memory_order_relaxed);
        mainBusMaxReadPeakPercent.store(busPeak.percent, std::memory_order_relaxed);
        mainBusMaxReadChannel.store(busPeak.channel, std::memory_order_relaxed);
        return;
    }

    outputInsertInputChannels.store(buffer.inputChannels, std::memory_order_relaxed);
    outputInsertOutputChannels.store(buffer.outputChannels, std::memory_order_relaxed);
    outputInsertReadPeakPercent.store(
        peakPercent(readChannelPointer(buffer, outputChannel), buffer.samplesPerFrame),
        std::memory_order_relaxed);
    const auto outputPeak = maxReadPeak(buffer, 0, buffer.inputChannels, 0);
    outputInsertMaxReadPeakPercent.store(outputPeak.percent, std::memory_order_relaxed);
    outputInsertMaxReadChannel.store(outputPeak.channel, std::memory_order_relaxed);
}

void RealtimeEngine::captureProbeWrite(AudioBufferView buffer, CallbackStreamKind kind, int) noexcept
{
    const int inputChannel = probeInputChannel.load(std::memory_order_relaxed);
    const int outputChannel = probeOutputChannel.load(std::memory_order_relaxed);

    if (kind == CallbackStreamKind::InputInsert)
    {
        inputInsertWritePeakPercent.store(
            peakPercent(writeChannelPointer(buffer, inputChannel), buffer.samplesPerFrame),
            std::memory_order_relaxed);
        const auto inputPeak = maxWritePeak(buffer, buffer.outputChannels);
        inputInsertMaxWritePeakPercent.store(inputPeak.percent, std::memory_order_relaxed);
        inputInsertMaxWriteChannel.store(inputPeak.channel, std::memory_order_relaxed);
        return;
    }

    if (kind == CallbackStreamKind::Main)
    {
        mainWritePeakPercent.store(
            peakPercent(writeChannelPointer(buffer, outputChannel), buffer.samplesPerFrame),
            std::memory_order_relaxed);
        const auto mainPeak = maxWritePeak(buffer, buffer.outputChannels);
        mainMaxWritePeakPercent.store(mainPeak.percent, std::memory_order_relaxed);
        mainMaxWriteChannel.store(mainPeak.channel, std::memory_order_relaxed);
        return;
    }

    outputInsertWritePeakPercent.store(
        peakPercent(writeChannelPointer(buffer, outputChannel), buffer.samplesPerFrame),
        std::memory_order_relaxed);
    const auto outputPeak = maxWritePeak(buffer, buffer.outputChannels);
    outputInsertMaxWritePeakPercent.store(outputPeak.percent, std::memory_order_relaxed);
    outputInsertMaxWriteChannel.store(outputPeak.channel, std::memory_order_relaxed);
}

void RealtimeEngine::publishTiming(double elapsedUsec, AudioBufferView buffer) noexcept
{
    callbackCount.fetch_add(1, std::memory_order_relaxed);
    lastProcessUsec.store(elapsedUsec, std::memory_order_relaxed);

    auto currentPeak = peakProcessUsec.load(std::memory_order_relaxed);
    while (elapsedUsec > currentPeak &&
           !peakProcessUsec.compare_exchange_weak(currentPeak, elapsedUsec, std::memory_order_relaxed))
    {
    }

    if (buffer.sampleRate > 0 && buffer.samplesPerFrame > 0)
    {
        const double budgetUsec =
            (static_cast<double>(buffer.samplesPerFrame) / static_cast<double>(buffer.sampleRate)) * 1000000.0;
        const double loadPercent = (elapsedUsec / budgetUsec) * 100.0;
        callbackCpuPercent.store(loadPercent, std::memory_order_relaxed);
        if (loadPercent > 50.0)
            callbackOver50Count.fetch_add(1, std::memory_order_relaxed);
        if (loadPercent > 80.0)
            callbackOver80Count.fetch_add(1, std::memory_order_relaxed);
        if (loadPercent > 100.0)
            callbackOver100Count.fetch_add(1, std::memory_order_relaxed);
    }
}
}
