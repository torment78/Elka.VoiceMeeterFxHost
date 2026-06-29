#pragma once

#include "engine/AudioBufferView.h"
#include "plugins/RealtimePluginProcessor.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <memory>
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
    uint64_t callbackOver50Count = 0;
    uint64_t callbackOver80Count = 0;
    uint64_t callbackOver100Count = 0;
    uint64_t pluginBusySkipCount = 0;
    uint64_t routeFifoWaitCount = 0;
    uint64_t callbackJitterOver25Count = 0;
    uint64_t callbackJitterOver50Count = 0;
    uint64_t callbackJitterOver100Count = 0;
    int callbackJitterMaxUsec = 0;
    uint64_t rawInputPopCount = 0;
    uint64_t postCopyPopCount = 0;
    uint64_t prePluginPopCount = 0;
    int rawInputDeltaPeakPercent = 0;
    int postCopyDeltaPeakPercent = 0;
    int prePluginDeltaPeakPercent = 0;
    int rawInputLivePeakPpm = 0;
    int postCopyLivePeakPpm = 0;
    int prePluginLivePeakPpm = 0;
    int rawInputBoundaryDeltaPpm = 0;
    int postCopyBoundaryDeltaPpm = 0;
    int prePluginBoundaryDeltaPpm = 0;
    uint64_t postCopyResidualCount = 0;
    uint64_t prePluginResidualCount = 0;
    uint64_t finalResidualCount = 0;
    int postCopyResidualPeakPpm = 0;
    int prePluginResidualPeakPpm = 0;
    int finalResidualPeakPpm = 0;
    int residualProbeStartChannel = -1;
    int residualProbeReadChannel = -1;
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
    uint64_t callbackJitterOver100Input = 0;
    uint64_t callbackJitterOver100Output = 0;
    uint64_t callbackJitterOver100Main = 0;
    uint64_t callbackJitterOver100InsertAsio = 0;
    int callbackJitterMaxUsecInput = 0;
    int callbackJitterMaxUsecOutput = 0;
    int callbackJitterMaxUsecMain = 0;
    int callbackJitterMaxUsecInsertAsio = 0;
    uint64_t rawInputPopCountInput = 0;
    uint64_t rawInputPopCountOutput = 0;
    uint64_t rawInputPopCountMain = 0;
    uint64_t rawInputPopCountInsertAsio = 0;
    uint64_t postCopyPopCountInput = 0;
    uint64_t postCopyPopCountOutput = 0;
    uint64_t postCopyPopCountMain = 0;
    uint64_t postCopyPopCountInsertAsio = 0;
    uint64_t prePluginPopCountInput = 0;
    uint64_t prePluginPopCountOutput = 0;
    uint64_t prePluginPopCountMain = 0;
    uint64_t prePluginPopCountInsertAsio = 0;
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
    void setInputCallbackSuppressedChannel(int channel, bool shouldSuppress) noexcept;

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
    void processInsertAsio(AudioBufferView buffer) noexcept;
    RealtimeStats getStats() const noexcept;

private:
    static constexpr int MaxChannels = 64;
    static constexpr int DelayStreamCount = 4;

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
    bool isInputCallbackSuppressed(bool suppressInputCallbackChannels, int channel) const noexcept;
    bool hasProcessingWork(CallbackStreamKind kind, bool enableInputOutputRoutes, int delayStreamIndex) const noexcept;
    void recordCallbackArrivalJitter(AudioBufferView buffer, int streamIndex) noexcept;
    void refreshRealtimeActivityCounters() noexcept;
    void processInternal(AudioBufferView buffer, CallbackStreamKind kind, bool enableInputOutputRoutes, int delayStreamIndex) noexcept;
    void probeAudioDiscontinuity(AudioBufferView buffer, CallbackStreamKind kind, int streamIndex, int readOffset, int position, bool preferCurrentBuffer) noexcept;
    void probePassthroughResidual(AudioBufferView buffer, CallbackStreamKind kind, int readOffset, int position) noexcept;
    void copyPassthrough(AudioBufferView buffer, int readOffset, bool suppressInputCallbackChannels) noexcept;
    void applyConfiguredDelays(AudioBufferView buffer, CallbackStreamKind kind, int readOffset, int streamIndex, bool suppressInputCallbackChannels) noexcept;
    void applyPlugins(AudioBufferView buffer, CallbackStreamKind kind, int readOffset, bool suppressInputCallbackChannels, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void applyPluginPassthroughRoutes(AudioBufferView buffer, CallbackStreamKind kind, int readOffset, int streamIndex, bool suppressInputCallbackChannels, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void processSinglePingInput(AudioBufferView buffer) noexcept;
    void processSinglePingOutput(AudioBufferView buffer) noexcept;
    void applyDirectRoutes(AudioBufferView buffer, CallbackStreamKind kind, int readOffset, bool enableInputOutputRoutes, bool suppressInputCallbackChannels, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void captureInputRoutes(AudioBufferView buffer, int readOffset, bool suppressInputCallbackChannels, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void mixCapturedInputRoutes(AudioBufferView buffer, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void applySameBufferDirectRoutes(AudioBufferView buffer, CallbackStreamKind kind, int readOffset, bool suppressInputCallbackChannels, std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void applyPluginGraphGate(AudioBufferView buffer, CallbackStreamKind kind, bool suppressInputCallbackChannels, const std::array<bool, MaxChannels>& pluginOutputWritten) noexcept;
    void applyConfiguredGains(AudioBufferView buffer, CallbackStreamKind kind, int streamIndex, bool suppressInputCallbackChannels) noexcept;
    void captureProbeRead(AudioBufferView buffer, CallbackStreamKind kind, int readOffset) noexcept;
    void captureProbeWrite(AudioBufferView buffer, CallbackStreamKind kind, int readOffset) noexcept;
    void publishTiming(double elapsedUsec, AudioBufferView buffer) noexcept;
    bool rebuildDynamicBuffers(int requestedSampleRate) noexcept;
    void refreshDynamicBuffersForCurrentSampleRate() noexcept;
    bool rebuildDynamicPluginScratchBuffers() noexcept;
    void refreshDynamicPluginScratchBuffers() noexcept;

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

    struct DynamicAudioBuffers
    {
        int sampleRate = 0;
        int delayLength = 0;
        int routeLength = 0;
        int delayLineCount = 0;
        int routeLineCount = 0;
        std::array<std::array<int, MaxChannels>, DelayStreamCount> delayLineIndexes {};
        std::array<std::array<int, MaxDirectRoutes>, DelayStreamCount> routeLineIndexes {};
        std::vector<float> delayBuffer;
        std::vector<float> routeBuffer;
    };

    struct DynamicPluginScratchBuffers
    {
        int pluginBusLineCount = 0;
        int passthroughLineCount = 0;
        std::array<int, MaxPluginSlots * MaxPluginPins> pluginBusLineIndexes {};
        std::array<int, DelayStreamCount> passthroughRouteCapacities {};
        std::array<int, DelayStreamCount> passthroughRouteStartLines {};
        std::vector<float> pluginBusBuffer;
        std::vector<float> pluginPassthroughScratchBuffer;
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
    EnableBank inputCallbackSuppressed {};
    std::atomic<int> suppressedInputChannelCount { 0 };
    DirectRouteBank inputDirectRoutes {};
    DirectRouteBank outputDirectRoutes {};
    DirectRouteBank mainDirectRoutes {};
    DirectRouteBank inputPluginPassthroughRoutes {};
    DirectRouteBank outputPluginPassthroughRoutes {};
    DirectRouteBank mainPluginPassthroughRoutes {};
    std::atomic<std::shared_ptr<DynamicPluginScratchBuffers>> dynamicPluginScratchBuffers;
    std::array<std::array<int, MaxChannels>, DelayStreamCount> delayWritePositions {};
    std::array<std::array<float, MaxChannels>, DelayStreamCount> smoothedChannelGains {};
    std::array<std::array<int, MaxChannels>, DelayStreamCount> smoothedDelaySamples {};
    std::array<std::array<bool, MaxChannels>, DelayStreamCount> smoothedDelayInitialized {};
    std::atomic<std::shared_ptr<DynamicAudioBuffers>> dynamicBuffers;
    std::array<std::atomic<int>, MaxDirectRoutes> routeReadPositions {};
    std::array<std::array<std::atomic<int>, MaxDirectRoutes>, DelayStreamCount> routeWritePositions {};
    std::array<std::array<std::atomic<int>, MaxDirectRoutes>, DelayStreamCount> routePrimedSamples {};
    std::array<int, MaxDirectRoutes> routeCounts {};
    std::array<std::array<float, MaxDirectRoutes>, DelayStreamCount> smoothedRouteGains {};
    std::atomic<int> delayBufferSampleRate { 0 };
    std::atomic<int> delayBufferLength { 0 };
    std::atomic<int> routeBufferLength { 0 };
    std::atomic<int> delayBufferLineCount { 0 };
    std::atomic<int> routeBufferLineCount { 0 };
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
    std::atomic<int> probeOutputChannel { -1 };
    std::atomic<uint64_t> probeGeneration { 0 };
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
    std::array<std::atomic<int>, DelayStreamCount> activePluginSlotCounts {};
    std::array<std::atomic<int>, DelayStreamCount> activePluginGraphChannelCounts {};
    std::array<std::atomic<int>, DelayStreamCount> activeDelayChannelCounts {};
    std::array<std::atomic<int>, DelayStreamCount> activeGainChannelCounts {};
    std::array<std::atomic<bool>, DelayStreamCount> pluginProcessingActive {};
    std::atomic<uint64_t> callbackCount { 0 };
    std::atomic<double> lastProcessUsec { 0.0 };
    std::atomic<double> peakProcessUsec { 0.0 };
    std::atomic<double> callbackCpuPercent { 0.0 };
    std::atomic<uint64_t> callbackOver50Count { 0 };
    std::atomic<uint64_t> callbackOver80Count { 0 };
    std::atomic<uint64_t> callbackOver100Count { 0 };
    std::atomic<uint64_t> pluginBusySkipCount { 0 };
    std::atomic<uint64_t> routeFifoWaitCount { 0 };
    std::array<std::atomic<int64_t>, DelayStreamCount> callbackLastEntryQpc {};
    std::atomic<uint64_t> callbackJitterOver25Count { 0 };
    std::atomic<uint64_t> callbackJitterOver50Count { 0 };
    std::atomic<uint64_t> callbackJitterOver100Count { 0 };
    std::atomic<int> callbackJitterMaxUsec { 0 };
    std::array<std::atomic<uint64_t>, DelayStreamCount> callbackJitterOver100ByStream {};
    std::array<std::atomic<int>, DelayStreamCount> callbackJitterMaxUsecByStream {};
    std::atomic<uint64_t> rawInputPopCount { 0 };
    std::atomic<uint64_t> postCopyPopCount { 0 };
    std::atomic<uint64_t> prePluginPopCount { 0 };
    std::array<std::atomic<uint64_t>, DelayStreamCount> rawInputPopCountByStream {};
    std::array<std::atomic<uint64_t>, DelayStreamCount> postCopyPopCountByStream {};
    std::array<std::atomic<uint64_t>, DelayStreamCount> prePluginPopCountByStream {};
    std::atomic<int> rawInputDeltaPeakPercent { 0 };
    std::atomic<int> postCopyDeltaPeakPercent { 0 };
    std::atomic<int> prePluginDeltaPeakPercent { 0 };
    std::atomic<int> rawInputLivePeakPpm { 0 };
    std::atomic<int> postCopyLivePeakPpm { 0 };
    std::atomic<int> prePluginLivePeakPpm { 0 };
    std::atomic<int> rawInputBoundaryDeltaPpm { 0 };
    std::atomic<int> postCopyBoundaryDeltaPpm { 0 };
    std::atomic<int> prePluginBoundaryDeltaPpm { 0 };
    std::atomic<uint64_t> postCopyResidualCount { 0 };
    std::atomic<uint64_t> prePluginResidualCount { 0 };
    std::atomic<uint64_t> finalResidualCount { 0 };
    std::atomic<int> postCopyResidualPeakPpm { 0 };
    std::atomic<int> prePluginResidualPeakPpm { 0 };
    std::atomic<int> finalResidualPeakPpm { 0 };
    std::atomic<int> residualProbeStartChannel { -1 };
    std::atomic<int> residualProbeReadChannel { -1 };
};
}
