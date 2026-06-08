#pragma once

#include "engine/AudioBufferView.h"
#include "plugins/RealtimePluginProcessor.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <vector>

namespace elka
{
enum class CallbackStreamKind
{
    InputInsert,
    OutputInsert,
    Main
};

struct RealtimeStats
{
    int sampleRate = 0;
    int blockSize = 0;
    int inputChannels = 0;
    int outputChannels = 0;
    uint64_t callbackCount = 0;
    double lastProcessUsec = 0.0;
    double peakProcessUsec = 0.0;
    double callbackCpuPercent = 0.0;
    int delayBufferSampleRate = 0;
    int probeInputChannel = -1;
    int probeOutputChannel = -1;
    int inputInsertReadPeakPercent = 0;
    int inputInsertWritePeakPercent = 0;
    int mainInputReadPeakPercent = 0;
    int mainOutputReadPeakPercent = 0;
    int mainWritePeakPercent = 0;
    int outputInsertReadPeakPercent = 0;
    int outputInsertWritePeakPercent = 0;
    int inputInsertMaxReadPeakPercent = 0;
    int inputInsertMaxReadChannel = -1;
    int inputInsertMaxWritePeakPercent = 0;
    int inputInsertMaxWriteChannel = -1;
    int mainSourceMaxReadPeakPercent = 0;
    int mainSourceMaxReadChannel = -1;
    int mainBusMaxReadPeakPercent = 0;
    int mainBusMaxReadChannel = -1;
    int mainMaxWritePeakPercent = 0;
    int mainMaxWriteChannel = -1;
    int outputInsertMaxReadPeakPercent = 0;
    int outputInsertMaxReadChannel = -1;
    int outputInsertMaxWritePeakPercent = 0;
    int outputInsertMaxWriteChannel = -1;
    int inputInsertInputChannels = 0;
    int inputInsertOutputChannels = 0;
    int mainInputChannels = 0;
    int mainOutputChannels = 0;
    int outputInsertInputChannels = 0;
    int outputInsertOutputChannels = 0;
};

struct DirectAudioRoute
{
    int sourceChannel = -1;
    int destinationChannel = -1;
    int delayMilliseconds = 0;
    int gainPercent = 100;
    bool muteSource = false;
};

struct SinglePingResult
{
    int status = 0;
    int inputChannel = -1;
    int outputChannel = -1;
    int sampleRate = 0;
    int latencySamples = -1;
    int elapsedSamples = 0;
    int peakPercent = 0;
    int timeoutMilliseconds = 0;
};

class RealtimeEngine
{
public:
    static constexpr int MaxDelayMilliseconds = 10000;
    static constexpr int MaxPluginSlots = 16;
    static constexpr int MaxPluginRoutes = 64;
    static constexpr int MaxPluginPins = 32;
    static constexpr int MaxPluginScratchSamples = 8192;
    static constexpr int MaxDirectRoutes = 128;

    RealtimeEngine() noexcept;

    void setEnabled(bool shouldEnable) noexcept;
    bool isEnabled() const noexcept;

    void setGainPercent(int percent) noexcept;
    int getGainPercent() const noexcept;

    void setChannelEnabled(CallbackStreamKind kind, int channel, bool shouldEnable) noexcept;
    bool isChannelEnabled(CallbackStreamKind kind, int channel) const noexcept;
    void setChannelGainPercent(CallbackStreamKind kind, int channel, int percent) noexcept;
    int getChannelGainPercent(CallbackStreamKind kind, int channel) const noexcept;
    void setChannelDelayMilliseconds(CallbackStreamKind kind, int channel, int milliseconds) noexcept;
    int getChannelDelayMilliseconds(CallbackStreamKind kind, int channel) const noexcept;
    void setChannelPluginGraphEnabled(CallbackStreamKind kind, int channel, bool shouldEnable) noexcept;
    bool isChannelPluginGraphEnabled(CallbackStreamKind kind, int channel) const noexcept;

    bool prepareDelayBuffers(int sampleRate) noexcept;
    int getDelayBufferSampleRate() const noexcept;

    void setTargetRange(CallbackStreamKind kind, int startChannel, int channelCount) noexcept;
    void setDirectRoutes(CallbackStreamKind kind, const DirectAudioRoute* routes, int routeCount) noexcept;
    void setPluginPassthroughRoutes(CallbackStreamKind kind, const DirectAudioRoute* routes, int routeCount) noexcept;
    bool startSinglePing(int inputChannel, int outputChannel, int timeoutMilliseconds) noexcept;
    SinglePingResult singlePingResult() const noexcept;
    void clearPluginSlots() noexcept;
    void setPluginSlot(
        int slot,
        RealtimePluginProcessor* processor,
        CallbackStreamKind kind,
        const PluginInputRoute* inputRoutes,
        int inputRouteCount,
        const PluginOutputRoute* outputRoutes,
        int outputRouteCount,
        bool enabled,
        bool bypassed) noexcept;
    void clearPluginSlot(int slot) noexcept;
    void setPluginSlotRoutes(
        int slot,
        const PluginInputRoute* inputRoutes,
        int inputRouteCount,
        const PluginOutputRoute* outputRoutes,
        int outputRouteCount) noexcept;
    void setPluginSlotEnabled(int slot, bool shouldEnable) noexcept;
    bool isPluginSlotEnabled(int slot) const noexcept;
    void setProbeChannels(int inputChannel, int outputChannel) noexcept;
    void updateFormat(int sampleRate, int blockSize) noexcept;
    void process(AudioBufferView buffer, CallbackStreamKind kind) noexcept;
    RealtimeStats getStats() const noexcept;

private:
    static constexpr int MaxChannels = 64;
    static constexpr int DelayStreamCount = 3;

    using GainBank = std::array<std::atomic<int>, MaxChannels>;
    using DelayBank = std::array<std::atomic<int>, MaxChannels>;
    using EnableBank = std::array<std::atomic<bool>, MaxChannels>;

    int getReadOffset(AudioBufferView buffer, CallbackStreamKind kind) const noexcept;
    int getSelectedChannelCount() const noexcept;
    int clampChannelCount(int start, int count) const noexcept;
    GainBank& gainBankFor(CallbackStreamKind kind) noexcept;
    const GainBank& gainBankFor(CallbackStreamKind kind) const noexcept;
    DelayBank& delayBankFor(CallbackStreamKind kind) noexcept;
    const DelayBank& delayBankFor(CallbackStreamKind kind) const noexcept;
    EnableBank& enableBankFor(CallbackStreamKind kind) noexcept;
    const EnableBank& enableBankFor(CallbackStreamKind kind) const noexcept;
    EnableBank& pluginGraphBankFor(CallbackStreamKind kind) noexcept;
    const EnableBank& pluginGraphBankFor(CallbackStreamKind kind) const noexcept;
    struct DirectRouteBank
    {
        std::array<std::atomic<int>, MaxDirectRoutes> sourceChannels {};
        std::array<std::atomic<int>, MaxDirectRoutes> destinationChannels {};
        std::array<std::atomic<int>, MaxDirectRoutes> delayMilliseconds {};
        std::array<std::atomic<int>, MaxDirectRoutes> gainPercent {};
        std::array<std::atomic<bool>, MaxDirectRoutes> muteSource {};
        std::atomic<int> routeCount { 0 };
    };

    DirectRouteBank& directRouteBankFor(CallbackStreamKind kind) noexcept;
    const DirectRouteBank& directRouteBankFor(CallbackStreamKind kind) const noexcept;
    DirectRouteBank& pluginPassthroughBankFor(CallbackStreamKind kind) noexcept;
    const DirectRouteBank& pluginPassthroughBankFor(CallbackStreamKind kind) const noexcept;
    void copyPassthrough(AudioBufferView buffer, int readOffset) noexcept;
    void applyConfiguredDelays(AudioBufferView buffer, CallbackStreamKind kind, int readOffset) noexcept;
    void applyPlugins(AudioBufferView buffer, CallbackStreamKind kind, int readOffset, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void applyPluginPassthroughRoutes(AudioBufferView buffer, CallbackStreamKind kind, int readOffset, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void processSinglePingInput(AudioBufferView buffer) noexcept;
    void processSinglePingOutput(AudioBufferView buffer) noexcept;
    void applyDirectRoutes(AudioBufferView buffer, CallbackStreamKind kind, int readOffset, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void captureInputRoutes(AudioBufferView buffer, int readOffset, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void mixCapturedInputRoutes(AudioBufferView buffer, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void applySameBufferDirectRoutes(AudioBufferView buffer, CallbackStreamKind kind, int readOffset, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void applyPluginGraphGate(AudioBufferView buffer, CallbackStreamKind kind, const std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void applyConfiguredGains(AudioBufferView buffer, CallbackStreamKind kind) noexcept;
    void captureProbeRead(AudioBufferView buffer, CallbackStreamKind kind, int readOffset) noexcept;
    void captureProbeWrite(AudioBufferView buffer, CallbackStreamKind kind, int readOffset) noexcept;
    void publishTiming(double elapsedUsec, AudioBufferView buffer) noexcept;

    struct PluginSlot
    {
        std::atomic<RealtimePluginProcessor*> processor { nullptr };
        std::atomic<bool> enabled { false };
        std::atomic<bool> bypassed { false };
        std::atomic<int> kind { static_cast<int>(CallbackStreamKind::InputInsert) };
        std::array<std::atomic<int>, MaxPluginRoutes> inputSourceKinds {};
        std::array<std::atomic<int>, MaxPluginRoutes> inputSourceChannels {};
        std::array<std::atomic<int>, MaxPluginRoutes> inputSourceSlots {};
        std::array<std::atomic<int>, MaxPluginRoutes> inputSourcePins {};
        std::array<std::atomic<int>, MaxPluginRoutes> inputPluginPins {};
        std::atomic<int> inputRouteCount { 0 };
        std::array<std::atomic<int>, MaxPluginRoutes> outputDestinationKinds {};
        std::array<std::atomic<int>, MaxPluginRoutes> outputPluginPins {};
        std::array<std::atomic<int>, MaxPluginRoutes> outputDestinationChannels {};
        std::array<std::atomic<int>, MaxPluginRoutes> outputDestinationSlots {};
        std::array<std::atomic<int>, MaxPluginRoutes> outputDestinationPins {};
        std::atomic<int> outputRouteCount { 0 };
    };

    GainBank inputGainPercent {};
    GainBank outputGainPercent {};
    DelayBank inputDelayMilliseconds {};
    DelayBank outputDelayMilliseconds {};
    EnableBank inputEnabled {};
    EnableBank outputEnabled {};
    EnableBank inputPluginGraphEnabled {};
    EnableBank outputPluginGraphEnabled {};
    EnableBank mainPluginGraphEnabled {};
    DirectRouteBank inputDirectRoutes {};
    DirectRouteBank outputDirectRoutes {};
    DirectRouteBank mainDirectRoutes {};
    DirectRouteBank inputPluginPassthroughRoutes {};
    DirectRouteBank outputPluginPassthroughRoutes {};
    DirectRouteBank mainPluginPassthroughRoutes {};
    std::vector<float> pluginBusBuffer;
    std::array<std::array<int, MaxChannels>, DelayStreamCount> delayWritePositions {};
    std::vector<float> delayBuffer;
    std::vector<float> routeBuffer;
    std::array<int, MaxDirectRoutes> routeReadPositions {};
    std::array<std::atomic<int>, MaxDirectRoutes> routeWritePositions {};
    std::array<int, MaxDirectRoutes> routeCounts {};
    std::atomic<int> delayBufferSampleRate { 0 };
    std::atomic<int> delayBufferLength { 0 };
    std::atomic<int> routeBufferLength { 0 };
    std::atomic<int> singlePingStatus { 0 };
    std::atomic<int> singlePingInputChannel { -1 };
    std::atomic<int> singlePingOutputChannel { -1 };
    std::atomic<int> singlePingSampleRate { 0 };
    std::atomic<int> singlePingLatencySamples { -1 };
    std::atomic<int> singlePingElapsedSamples { 0 };
    std::atomic<int> singlePingPeakPercent { 0 };
    std::atomic<int> singlePingTimeoutMilliseconds { 0 };
    std::atomic<int> singlePingTimeoutSamples { 0 };
    std::atomic<int> singlePingPulsePosition { 0 };
    std::atomic<int> selectedKind { static_cast<int>(CallbackStreamKind::InputInsert) };
    std::atomic<int> targetStart { 0 };
    std::atomic<int> targetCount { 1 };
    std::atomic<int> probeInputChannel { 0 };
    std::atomic<int> probeOutputChannel { 0 };
    std::atomic<int> inputInsertReadPeakPercent { 0 };
    std::atomic<int> inputInsertWritePeakPercent { 0 };
    std::atomic<int> mainInputReadPeakPercent { 0 };
    std::atomic<int> mainOutputReadPeakPercent { 0 };
    std::atomic<int> mainWritePeakPercent { 0 };
    std::atomic<int> outputInsertReadPeakPercent { 0 };
    std::atomic<int> outputInsertWritePeakPercent { 0 };
    std::atomic<int> inputInsertMaxReadPeakPercent { 0 };
    std::atomic<int> inputInsertMaxReadChannel { -1 };
    std::atomic<int> inputInsertMaxWritePeakPercent { 0 };
    std::atomic<int> inputInsertMaxWriteChannel { -1 };
    std::atomic<int> mainSourceMaxReadPeakPercent { 0 };
    std::atomic<int> mainSourceMaxReadChannel { -1 };
    std::atomic<int> mainBusMaxReadPeakPercent { 0 };
    std::atomic<int> mainBusMaxReadChannel { -1 };
    std::atomic<int> mainMaxWritePeakPercent { 0 };
    std::atomic<int> mainMaxWriteChannel { -1 };
    std::atomic<int> outputInsertMaxReadPeakPercent { 0 };
    std::atomic<int> outputInsertMaxReadChannel { -1 };
    std::atomic<int> outputInsertMaxWritePeakPercent { 0 };
    std::atomic<int> outputInsertMaxWriteChannel { -1 };
    std::atomic<int> inputInsertInputChannels { 0 };
    std::atomic<int> inputInsertOutputChannels { 0 };
    std::atomic<int> mainInputChannels { 0 };
    std::atomic<int> mainOutputChannels { 0 };
    std::atomic<int> outputInsertInputChannels { 0 };
    std::atomic<int> outputInsertOutputChannels { 0 };
    std::array<PluginSlot, MaxPluginSlots> pluginSlots {};
    std::atomic<int> sampleRate { 0 };
    std::atomic<int> blockSize { 0 };
    std::atomic<int> inputChannels { 0 };
    std::atomic<int> outputChannels { 0 };
    std::atomic<uint64_t> callbackCount { 0 };
    std::atomic<double> lastProcessUsec { 0.0 };
    std::atomic<double> peakProcessUsec { 0.0 };
    std::atomic<double> callbackCpuPercent { 0.0 };
};
}
