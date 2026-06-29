#include "engine/RealtimeEngine.h"
#include "voicemeeter/VoicemeeterClient.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cwchar>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace
{
using namespace elka;

struct NativeDirectRoute
{
    int sourceChannel = -1;
    int destinationChannel = -1;
    int delayMilliseconds = 0;
    int gainPercent = 100;
    int muteSource = 0;
};

struct NativePluginPassthroughRoute
{
    int sourceChannel = -1;
    int destinationChannel = -1;
};

struct NativeStats
{
    int connectionState = 0;
    int mode = 1;
    int sampleRate = 0;
    int blockSize = 0;
    int inputChannels = 0;
    int outputChannels = 0;
    unsigned long long callbackCount = 0;
    unsigned long long callbackCommandCount = 0;
    unsigned long long callbackStartingCount = 0;
    unsigned long long callbackEndingCount = 0;
    unsigned long long callbackChangeCount = 0;
    unsigned long long callbackBufferInCount = 0;
    unsigned long long callbackBufferOutCount = 0;
    unsigned long long callbackBufferMainCount = 0;
    int callbackLastCommand = 0;
    double lastProcessUsec = 0.0;
    double peakProcessUsec = 0.0;
    double callbackCpuPercent = 0.0;
    unsigned long long callbackOver50Count = 0;
    unsigned long long callbackOver80Count = 0;
    unsigned long long callbackOver100Count = 0;
    unsigned long long pluginBusySkipCount = 0;
    unsigned long long routeFifoWaitCount = 0;
    unsigned long long callbackJitterOver25Count = 0;
    unsigned long long callbackJitterOver50Count = 0;
    unsigned long long callbackJitterOver100Count = 0;
    int callbackJitterMaxUsec = 0;
    unsigned long long rawInputPopCount = 0;
    unsigned long long postCopyPopCount = 0;
    unsigned long long prePluginPopCount = 0;
    int rawInputDeltaPeakPercent = 0;
    int postCopyDeltaPeakPercent = 0;
    int prePluginDeltaPeakPercent = 0;
    int rawInputLivePeakPpm = 0;
    int postCopyLivePeakPpm = 0;
    int prePluginLivePeakPpm = 0;
    int rawInputBoundaryDeltaPpm = 0;
    int postCopyBoundaryDeltaPpm = 0;
    int prePluginBoundaryDeltaPpm = 0;
    unsigned long long postCopyResidualCount = 0;
    unsigned long long prePluginResidualCount = 0;
    unsigned long long finalResidualCount = 0;
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
    int voicemeeterPreFaderLevelPercent = -1;
    int voicemeeterPostFaderLevelPercent = -1;
    int voicemeeterPostMuteLevelPercent = -1;
    int voicemeeterNextPreFaderLevelPercent = -1;
    int voicemeeterNextPostFaderLevelPercent = -1;
    int voicemeeterNextPostMuteLevelPercent = -1;
    int voicemeeterInputMaxLevelPercent = -1;
    int voicemeeterInputMaxChannel = -1;
    int voicemeeterOutputLevelPercent = -1;
    unsigned long long callbackJitterOver100Input = 0;
    unsigned long long callbackJitterOver100Output = 0;
    unsigned long long callbackJitterOver100Main = 0;
    unsigned long long callbackJitterOver100InsertAsio = 0;
    int callbackJitterMaxUsecInput = 0;
    int callbackJitterMaxUsecOutput = 0;
    int callbackJitterMaxUsecMain = 0;
    int callbackJitterMaxUsecInsertAsio = 0;
    unsigned long long rawInputPopCountInput = 0;
    unsigned long long rawInputPopCountOutput = 0;
    unsigned long long rawInputPopCountMain = 0;
    unsigned long long rawInputPopCountInsertAsio = 0;
    unsigned long long postCopyPopCountInput = 0;
    unsigned long long postCopyPopCountOutput = 0;
    unsigned long long postCopyPopCountMain = 0;
    unsigned long long postCopyPopCountInsertAsio = 0;
    unsigned long long prePluginPopCountInput = 0;
    unsigned long long prePluginPopCountOutput = 0;
    unsigned long long prePluginPopCountMain = 0;
    unsigned long long prePluginPopCountInsertAsio = 0;
};

struct NativeSinglePingResult
{
    int status = 0;
    int inputChannel = -1;
    int outputChannel = -1;
    int sampleRate = 0;
    int latencySamples = -1;
    int elapsedSamples = 0;
    int peakPercent = 0;
    int timeoutMilliseconds = 0;
    double latencyMilliseconds = 0.0;
    double elapsedMilliseconds = 0.0;
};

class RealtimeCoreHost
{
public:
    RealtimeCoreHost()
        : client(engine)
    {
    }

    RealtimeEngine engine;
    VoicemeeterClient client;
    CallbackMode mode = CallbackMode::None;
    std::wstring lastStatus = L"Realtime core ready";
};

std::mutex g_mutex;
std::unique_ptr<RealtimeCoreHost> g_host;

RealtimeCoreHost& host()
{
    if (!g_host)
        g_host = std::make_unique<RealtimeCoreHost>();

    return *g_host;
}

void writeWide(const std::wstring& text, wchar_t* buffer, int bufferChars) noexcept
{
    if (buffer == nullptr || bufferChars <= 0)
        return;

    const auto count = std::min<int>(static_cast<int>(text.size()), bufferChars - 1);
    if (count > 0)
        std::wmemcpy(buffer, text.c_str(), static_cast<size_t>(count));

    buffer[count] = L'\0';
}

CallbackMode callbackModeFromApi(int mode) noexcept
{
    const int validBits =
        static_cast<int>(CallbackMode::InputInsert) |
        static_cast<int>(CallbackMode::OutputInsert) |
        static_cast<int>(CallbackMode::Main);
    return static_cast<CallbackMode>(mode & validBits);
}

CallbackStreamKind streamKindFromApi(int mode) noexcept
{
    switch (mode)
    {
    case 2:
        return CallbackStreamKind::OutputInsert;
    case 4:
        return CallbackStreamKind::Main;
    case 1:
    default:
        return CallbackStreamKind::InputInsert;
    }
}

int apiModeFromCallbackMode(CallbackMode mode) noexcept
{
    return static_cast<int>(mode);
}

int clampVoiceMeeterSampleRate(int sampleRate) noexcept
{
    return sampleRate > 0 ? std::clamp(sampleRate, 8000, 192000) : 0;
}

int configuredVoiceMeeterSampleRate(RealtimeCoreHost& target) noexcept
{
    int sampleRate = 0;
    return target.client.getConfiguredSampleRate(sampleRate)
        ? clampVoiceMeeterSampleRate(sampleRate)
        : 0;
}

int activeVoiceMeeterSampleRate(RealtimeCoreHost& target) noexcept
{
    const auto stats = target.engine.getStats();
    const int liveSampleRate = clampVoiceMeeterSampleRate(stats.sampleRate);
    if (liveSampleRate > 0)
        return liveSampleRate;

    return configuredVoiceMeeterSampleRate(target);
}

int readRoundedParameter(RealtimeCoreHost& target, const char* parameterName) noexcept
{
    float value = 0.0f;
    if (!target.client.getParameterFloat(parameterName, value))
        return -1;

    return static_cast<int>(std::lround(value));
}

int readIndexedParameter(RealtimeCoreHost& target, const char* format, int index) noexcept
{
    if (index < 0)
        return -1;

    char parameterName[64] {};
    const int written = std::snprintf(parameterName, sizeof(parameterName), format, index);
    if (written <= 0 || written >= static_cast<int>(sizeof(parameterName)))
        return -1;

    return readRoundedParameter(target, parameterName);
}

bool startLocked(RealtimeCoreHost& target, std::wstring& error)
{
    if (!target.client.connect(error))
        return false;

    if (target.mode == CallbackMode::None)
    {
        target.client.unregisterCallback();
        target.lastStatus = L"Connected | realtime core callback idle";
        return true;
    }

    const int configuredSampleRate = configuredVoiceMeeterSampleRate(target);
    if (configuredSampleRate > 0 && !target.engine.prepareDelayBuffers(configuredSampleRate))
    {
        error = L"Realtime core failed to allocate delay buffers for " +
            std::to_wstring(configuredSampleRate) +
            L" Hz";
        return false;
    }

    if (!target.client.start(error))
        return false;

    const int activeSampleRate = activeVoiceMeeterSampleRate(target);
    if (activeSampleRate > 0 &&
        target.engine.getDelayBufferSampleRate() != activeSampleRate)
    {
        target.client.stop();
        if (!target.engine.prepareDelayBuffers(activeSampleRate))
        {
            error = L"Realtime core failed to allocate delay buffers for " +
                std::to_wstring(activeSampleRate) +
                L" Hz";
            return false;
        }

        if (!target.client.start(error))
            return false;
    }

    target.lastStatus = target.client.statusText();
    return true;
}

int ensureRealtimePreparedLocked(RealtimeCoreHost& target, std::wstring& message)
{
    std::wstring connectError;
    if (!target.client.connect(connectError))
    {
        message = connectError.empty() ? L"Could not connect to VoiceMeeter Remote API." : connectError;
        return -1;
    }

    const auto stats = target.engine.getStats();
    const int desiredSampleRate = clampVoiceMeeterSampleRate(stats.sampleRate);
    if (desiredSampleRate <= 0)
    {
        message = L"Realtime core prepare skipped: callback has not reported a live sample rate yet.";
        return 0;
    }

    if (target.engine.getDelayBufferSampleRate() == desiredSampleRate)
    {
        message = L"Realtime core buffers are current.";
        return 0;
    }

    const bool wasRunning = target.client.state() == ConnectionState::Running;
    if (wasRunning)
        target.client.stop();

    if (!target.engine.prepareDelayBuffers(desiredSampleRate))
    {
        if (wasRunning)
        {
            std::wstring restartError;
            target.client.start(restartError);
        }

        message = L"Realtime core prepare failed: could not allocate delay buffers for " +
            std::to_wstring(desiredSampleRate) +
            L" Hz.";
        return -1;
    }

    if (wasRunning)
    {
        std::wstring restartError;
        if (!target.client.start(restartError))
        {
            message = L"Realtime core buffers prepared for " +
                std::to_wstring(desiredSampleRate) +
                L" Hz, but callback restart failed: " +
                restartError;
            return -1;
        }
    }

    message = L"Realtime core callback buffers prepared for " +
        std::to_wstring(desiredSampleRate) +
        L" Hz.";
    target.lastStatus = target.client.statusText();
    return 1;
}

bool setModeLocked(RealtimeCoreHost& target, CallbackMode mode, std::wstring& error)
{
    const CallbackMode previousMode = target.mode;
    if (mode == previousMode && target.client.state() == ConnectionState::Running)
    {
        target.client.setPreferredMode(mode);
        target.lastStatus = target.client.statusText();
        return true;
    }

    if (mode == CallbackMode::None)
    {
        if (!target.client.connect(error))
            return false;

        target.client.unregisterCallback();
        target.mode = mode;
        target.lastStatus = L"Connected | realtime core callback idle";
        return true;
    }

    auto restorePreviousMode = [&]() noexcept {
        target.client.setPreferredMode(previousMode);
        target.mode = previousMode;
    };

    target.client.setPreferredMode(mode);
    if (target.client.state() == ConnectionState::Running)
        target.client.stop();

    if (target.client.state() != ConnectionState::Disconnected &&
        !target.client.registerCallback(mode, error))
    {
        const std::wstring firstError = error;
        target.client.disconnect();
        error.clear();
        target.client.setPreferredMode(mode);

        if (!target.client.registerCallback(mode, error))
        {
            if (error.empty())
                error = firstError;
            restorePreviousMode();
            target.lastStatus = error;
            return false;
        }
    }

    target.mode = mode;
    if (startLocked(target, error))
        return true;

    const std::wstring firstStartError = error;
    target.client.disconnect();
    error.clear();
    target.client.setPreferredMode(mode);
    target.mode = mode;

    if (startLocked(target, error))
        return true;

    if (error.empty())
        error = firstStartError;

    target.client.unregisterCallback();
    restorePreviousMode();
    target.lastStatus = error;
    return false;
}

bool rearmModeLocked(RealtimeCoreHost& target, CallbackMode mode, std::wstring& error)
{
    if (mode == CallbackMode::None)
        return setModeLocked(target, mode, error);

    if (!target.client.connect(error))
        return false;

    target.client.setPreferredMode(mode);
    target.client.unregisterCallback();
    target.mode = mode;

    if (startLocked(target, error))
        return true;

    const std::wstring firstStartError = error;
    target.client.disconnect();
    error.clear();
    target.client.setPreferredMode(mode);
    target.mode = mode;

    if (startLocked(target, error))
        return true;

    if (error.empty())
        error = firstStartError;

    target.client.unregisterCallback();
    target.lastStatus = error;
    return false;
}
void copyStats(RealtimeCoreHost& target, NativeStats& destination) noexcept
{
    destination = NativeStats {};
    const auto realtimeStats = target.engine.getStats();
    const auto callbackStats = target.client.callbackStats();
    destination.connectionState = static_cast<int>(target.client.state());
    destination.mode = apiModeFromCallbackMode(target.mode);
    destination.sampleRate = realtimeStats.sampleRate;
    destination.blockSize = realtimeStats.blockSize;
    destination.inputChannels = realtimeStats.inputChannels;
    destination.outputChannels = realtimeStats.outputChannels;
    destination.callbackCount = realtimeStats.callbackCount;
    destination.callbackCommandCount = callbackStats.total;
    destination.callbackStartingCount = callbackStats.starting;
    destination.callbackEndingCount = callbackStats.ending;
    destination.callbackChangeCount = callbackStats.change;
    destination.callbackBufferInCount = callbackStats.bufferIn;
    destination.callbackBufferOutCount = callbackStats.bufferOut;
    destination.callbackBufferMainCount = callbackStats.bufferMain;
    destination.callbackLastCommand = static_cast<int>(callbackStats.lastCommand);
    destination.lastProcessUsec = realtimeStats.lastProcessUsec;
    destination.peakProcessUsec = realtimeStats.peakProcessUsec;
    destination.callbackCpuPercent = realtimeStats.callbackCpuPercent;
    destination.callbackOver50Count = realtimeStats.callbackOver50Count;
    destination.callbackOver80Count = realtimeStats.callbackOver80Count;
    destination.callbackOver100Count = realtimeStats.callbackOver100Count;
    destination.pluginBusySkipCount = realtimeStats.pluginBusySkipCount;
    destination.routeFifoWaitCount = realtimeStats.routeFifoWaitCount;
    destination.callbackJitterOver25Count = realtimeStats.callbackJitterOver25Count;
    destination.callbackJitterOver50Count = realtimeStats.callbackJitterOver50Count;
    destination.callbackJitterOver100Count = realtimeStats.callbackJitterOver100Count;
    destination.callbackJitterMaxUsec = realtimeStats.callbackJitterMaxUsec;
    destination.rawInputPopCount = realtimeStats.rawInputPopCount;
    destination.postCopyPopCount = realtimeStats.postCopyPopCount;
    destination.prePluginPopCount = realtimeStats.prePluginPopCount;
    destination.rawInputDeltaPeakPercent = realtimeStats.rawInputDeltaPeakPercent;
    destination.postCopyDeltaPeakPercent = realtimeStats.postCopyDeltaPeakPercent;
    destination.prePluginDeltaPeakPercent = realtimeStats.prePluginDeltaPeakPercent;
    destination.rawInputLivePeakPpm = realtimeStats.rawInputLivePeakPpm;
    destination.postCopyLivePeakPpm = realtimeStats.postCopyLivePeakPpm;
    destination.prePluginLivePeakPpm = realtimeStats.prePluginLivePeakPpm;
    destination.rawInputBoundaryDeltaPpm = realtimeStats.rawInputBoundaryDeltaPpm;
    destination.postCopyBoundaryDeltaPpm = realtimeStats.postCopyBoundaryDeltaPpm;
    destination.prePluginBoundaryDeltaPpm = realtimeStats.prePluginBoundaryDeltaPpm;
    destination.postCopyResidualCount = realtimeStats.postCopyResidualCount;
    destination.prePluginResidualCount = realtimeStats.prePluginResidualCount;
    destination.finalResidualCount = realtimeStats.finalResidualCount;
    destination.postCopyResidualPeakPpm = realtimeStats.postCopyResidualPeakPpm;
    destination.prePluginResidualPeakPpm = realtimeStats.prePluginResidualPeakPpm;
    destination.finalResidualPeakPpm = realtimeStats.finalResidualPeakPpm;
    destination.residualProbeStartChannel = realtimeStats.residualProbeStartChannel;
    destination.residualProbeReadChannel = realtimeStats.residualProbeReadChannel;
    destination.delayBufferSampleRate = realtimeStats.delayBufferSampleRate;
    destination.probeInputChannel = realtimeStats.probeInputChannel;
    destination.probeOutputChannel = realtimeStats.probeOutputChannel;
    destination.inputInsertReadPeakPercent = realtimeStats.inputInsertReadPeakPercent;
    destination.inputInsertWritePeakPercent = realtimeStats.inputInsertWritePeakPercent;
    destination.mainInputReadPeakPercent = realtimeStats.mainInputReadPeakPercent;
    destination.mainOutputReadPeakPercent = realtimeStats.mainOutputReadPeakPercent;
    destination.mainWritePeakPercent = realtimeStats.mainWritePeakPercent;
    destination.outputInsertReadPeakPercent = realtimeStats.outputInsertReadPeakPercent;
    destination.outputInsertWritePeakPercent = realtimeStats.outputInsertWritePeakPercent;
    destination.inputInsertMaxReadPeakPercent = realtimeStats.inputInsertMaxReadPeakPercent;
    destination.inputInsertMaxReadChannel = realtimeStats.inputInsertMaxReadChannel;
    destination.inputInsertMaxWritePeakPercent = realtimeStats.inputInsertMaxWritePeakPercent;
    destination.inputInsertMaxWriteChannel = realtimeStats.inputInsertMaxWriteChannel;
    destination.mainSourceMaxReadPeakPercent = realtimeStats.mainSourceMaxReadPeakPercent;
    destination.mainSourceMaxReadChannel = realtimeStats.mainSourceMaxReadChannel;
    destination.mainBusMaxReadPeakPercent = realtimeStats.mainBusMaxReadPeakPercent;
    destination.mainBusMaxReadChannel = realtimeStats.mainBusMaxReadChannel;
    destination.mainMaxWritePeakPercent = realtimeStats.mainMaxWritePeakPercent;
    destination.mainMaxWriteChannel = realtimeStats.mainMaxWriteChannel;
    destination.outputInsertMaxReadPeakPercent = realtimeStats.outputInsertMaxReadPeakPercent;
    destination.outputInsertMaxReadChannel = realtimeStats.outputInsertMaxReadChannel;
    destination.outputInsertMaxWritePeakPercent = realtimeStats.outputInsertMaxWritePeakPercent;
    destination.outputInsertMaxWriteChannel = realtimeStats.outputInsertMaxWriteChannel;
    destination.inputInsertInputChannels = realtimeStats.inputInsertInputChannels;
    destination.inputInsertOutputChannels = realtimeStats.inputInsertOutputChannels;
    destination.mainInputChannels = realtimeStats.mainInputChannels;
    destination.mainOutputChannels = realtimeStats.mainOutputChannels;
    destination.outputInsertInputChannels = realtimeStats.outputInsertInputChannels;
    destination.outputInsertOutputChannels = realtimeStats.outputInsertOutputChannels;
    destination.callbackJitterOver100Input = realtimeStats.callbackJitterOver100Input;
    destination.callbackJitterOver100Output = realtimeStats.callbackJitterOver100Output;
    destination.callbackJitterOver100Main = realtimeStats.callbackJitterOver100Main;
    destination.callbackJitterOver100InsertAsio = realtimeStats.callbackJitterOver100InsertAsio;
    destination.callbackJitterMaxUsecInput = realtimeStats.callbackJitterMaxUsecInput;
    destination.callbackJitterMaxUsecOutput = realtimeStats.callbackJitterMaxUsecOutput;
    destination.callbackJitterMaxUsecMain = realtimeStats.callbackJitterMaxUsecMain;
    destination.callbackJitterMaxUsecInsertAsio = realtimeStats.callbackJitterMaxUsecInsertAsio;
    destination.rawInputPopCountInput = realtimeStats.rawInputPopCountInput;
    destination.rawInputPopCountOutput = realtimeStats.rawInputPopCountOutput;
    destination.rawInputPopCountMain = realtimeStats.rawInputPopCountMain;
    destination.rawInputPopCountInsertAsio = realtimeStats.rawInputPopCountInsertAsio;
    destination.postCopyPopCountInput = realtimeStats.postCopyPopCountInput;
    destination.postCopyPopCountOutput = realtimeStats.postCopyPopCountOutput;
    destination.postCopyPopCountMain = realtimeStats.postCopyPopCountMain;
    destination.postCopyPopCountInsertAsio = realtimeStats.postCopyPopCountInsertAsio;
    destination.prePluginPopCountInput = realtimeStats.prePluginPopCountInput;
    destination.prePluginPopCountOutput = realtimeStats.prePluginPopCountOutput;
    destination.prePluginPopCountMain = realtimeStats.prePluginPopCountMain;
    destination.prePluginPopCountInsertAsio = realtimeStats.prePluginPopCountInsertAsio;
    }
}

extern "C"
{
__declspec(dllexport) int __cdecl ElkaFx_Initialize(wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    std::wstring error;
    const bool started = setModeLocked(target, target.mode, error);
    writeWide(started ? target.client.statusText() : error, status, statusChars);
    return started ? 0 : -1;
}

__declspec(dllexport) void __cdecl ElkaFx_Shutdown()
{
    std::lock_guard lock(g_mutex);
    if (g_host)
    {
        g_host->client.disconnect();
        g_host.reset();
    }
}

__declspec(dllexport) int __cdecl ElkaFx_SetMode(int mode, wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    std::wstring error;
    const bool ok = setModeLocked(target, callbackModeFromApi(mode), error);
    writeWide(ok ? target.client.statusText() : error, status, statusChars);
    return ok ? 0 : -1;
}

__declspec(dllexport) int __cdecl ElkaFx_RearmMode(int mode, wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    std::wstring error;
    const bool ok = rearmModeLocked(target, callbackModeFromApi(mode), error);
    writeWide(ok ? target.client.statusText() : error, status, statusChars);
    return ok ? 0 : -1;
}
__declspec(dllexport) int __cdecl ElkaFx_Start(wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    std::wstring error;
    const bool ok = startLocked(target, error);
    writeWide(ok ? target.client.statusText() : error, status, statusChars);
    return ok ? 0 : -1;
}

__declspec(dllexport) int __cdecl ElkaFx_EnsureRealtimePrepared(wchar_t* status, int statusChars)
{
    try
    {
        std::lock_guard lock(g_mutex);
        auto& target = host();
        std::wstring message;
        const int result = ensureRealtimePreparedLocked(target, message);
        writeWide(message, status, statusChars);
        return result;
    }
    catch (...)
    {
        writeWide(L"Realtime core prepare failed: native exception.", status, statusChars);
        return -1;
    }
}

__declspec(dllexport) void __cdecl ElkaFx_Disconnect()
{
    std::lock_guard lock(g_mutex);
    if (g_host)
        g_host->client.disconnect();
}

__declspec(dllexport) void __cdecl ElkaFx_SetTargetRange(int kind, int startChannel, int channelCount)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    target.engine.setTargetRange(streamKindFromApi(kind), startChannel, channelCount);
}

__declspec(dllexport) void __cdecl ElkaFx_SetChannelSettings(
    int kind,
    int channel,
    int enabled,
    int delayMilliseconds,
    int gainPercent)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    const auto streamKind = streamKindFromApi(kind);
    target.engine.setChannelEnabled(streamKind, channel, enabled != 0);
    target.engine.setChannelDelayMilliseconds(streamKind, channel, delayMilliseconds);
    target.engine.setChannelGainPercent(streamKind, channel, gainPercent);
}

__declspec(dllexport) void __cdecl ElkaFx_SetPluginGraphEnabled(int kind, int channel, int enabled)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    target.engine.setChannelPluginGraphEnabled(streamKindFromApi(kind), channel, enabled != 0);
}

__declspec(dllexport) void __cdecl ElkaFx_SetInputCallbackSuppressedChannel(int channel, int suppressed)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    target.engine.setInputCallbackSuppressedChannel(channel, suppressed != 0);
}

__declspec(dllexport) void __cdecl ElkaFx_SetDirectRoutes(int kind, const NativeDirectRoute* routes, int routeCount)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();

    std::vector<DirectAudioRoute> nativeRoutes;
    const int safeCount = routes != nullptr ? std::clamp(routeCount, 0, RealtimeEngine::MaxDirectRoutes) : 0;
    nativeRoutes.reserve(static_cast<size_t>(safeCount));
    for (int i = 0; i < safeCount; ++i)
    {
        nativeRoutes.push_back(DirectAudioRoute {
            routes[i].sourceChannel,
            routes[i].destinationChannel,
            routes[i].delayMilliseconds,
            routes[i].gainPercent,
            routes[i].muteSource != 0
        });
    }

    target.engine.setDirectRoutes(
        streamKindFromApi(kind),
        nativeRoutes.empty() ? nullptr : nativeRoutes.data(),
        static_cast<int>(nativeRoutes.size()));
}

__declspec(dllexport) void __cdecl ElkaFx_SetPluginPassthroughRoutes(
    int kind,
    const NativePluginPassthroughRoute* routes,
    int routeCount)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();

    std::vector<DirectAudioRoute> nativeRoutes;
    const int safeCount = routes != nullptr ? std::clamp(routeCount, 0, RealtimeEngine::MaxDirectRoutes) : 0;
    nativeRoutes.reserve(static_cast<size_t>(safeCount));
    for (int i = 0; i < safeCount; ++i)
    {
        nativeRoutes.push_back(DirectAudioRoute {
            routes[i].sourceChannel,
            routes[i].destinationChannel,
            0,
            100,
            false
        });
    }

    target.engine.setPluginPassthroughRoutes(
        streamKindFromApi(kind),
        nativeRoutes.empty() ? nullptr : nativeRoutes.data(),
        static_cast<int>(nativeRoutes.size()));
}

__declspec(dllexport) void __cdecl ElkaFx_SetProbeChannels(int inputChannel, int outputChannel)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    target.engine.setProbeChannels(inputChannel, outputChannel);
}

__declspec(dllexport) void __cdecl ElkaFx_GetStats(NativeStats* stats)
{
    if (stats == nullptr)
        return;

    std::lock_guard lock(g_mutex);
    auto& target = host();
    copyStats(target, *stats);
}

__declspec(dllexport) int __cdecl ElkaFx_GetPatchAsioChannel(int inputChannel)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    return readIndexedParameter(target, "patch.asio[%d]", inputChannel);
}

__declspec(dllexport) int __cdecl ElkaFx_RefreshVoicemeeterParameters()
{
    try
    {
        std::lock_guard lock(g_mutex);
        auto& target = host();
        const bool refreshed = target.client.refreshParameters();
        const auto stats = target.engine.getStats();
        const int configuredSampleRate = configuredVoiceMeeterSampleRate(target);
        if (configuredSampleRate > 0 && stats.sampleRate <= 0)
        {
            const int blockSize = stats.blockSize > 0 ? stats.blockSize : 512;
            target.engine.updateFormat(configuredSampleRate, blockSize);
        }

        return refreshed ? 0 : -1;
    }
    catch (...)
    {
        return -1;
    }
}

__declspec(dllexport) int __cdecl ElkaFx_GetPatchInsertEnabled(int inputChannel)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();

    auto result = readIndexedParameter(target, "Patch.insert[%d]", inputChannel);
    if (result < 0)
        result = readIndexedParameter(target, "patch.insert[%d]", inputChannel);

    return result < 0 ? -1 : (result != 0 ? 1 : 0);
}

__declspec(dllexport) int __cdecl ElkaFx_GetPatchPostFxInsertEnabled()
{
    std::lock_guard lock(g_mutex);
    auto& target = host();

    auto result = readRoundedParameter(target, "Patch.PostFxInsert");
    if (result < 0)
        result = readRoundedParameter(target, "patch.PostFxInsert");

    return result < 0 ? -1 : (result != 0 ? 1 : 0);
}

__declspec(dllexport) int __cdecl ElkaFx_StartSinglePing(int inputChannel, int outputChannel, int timeoutMilliseconds)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();

    const int requiredMode =
        static_cast<int>(CallbackMode::InputInsert) |
        static_cast<int>(CallbackMode::OutputInsert) |
        static_cast<int>(CallbackMode::Main);
    std::wstring error;
    if (!setModeLocked(target, static_cast<CallbackMode>(requiredMode), error))
        return -1;

    return target.engine.startSinglePing(inputChannel, outputChannel, timeoutMilliseconds) ? 0 : -1;
}

__declspec(dllexport) void __cdecl ElkaFx_GetSinglePingResult(NativeSinglePingResult* result)
{
    if (result == nullptr)
        return;

    std::lock_guard lock(g_mutex);
    const auto ping = host().engine.singlePingResult();
    result->status = ping.status;
    result->inputChannel = ping.inputChannel;
    result->outputChannel = ping.outputChannel;
    result->sampleRate = ping.sampleRate;
    result->latencySamples = ping.latencySamples;
    result->elapsedSamples = ping.elapsedSamples;
    result->peakPercent = ping.peakPercent;
    result->timeoutMilliseconds = ping.timeoutMilliseconds;
    result->latencyMilliseconds = ping.sampleRate > 0 && ping.latencySamples >= 0
        ? (static_cast<double>(ping.latencySamples) * 1000.0) / static_cast<double>(ping.sampleRate)
        : -1.0;
    result->elapsedMilliseconds = ping.sampleRate > 0
        ? (static_cast<double>(ping.elapsedSamples) * 1000.0) / static_cast<double>(ping.sampleRate)
        : 0.0;
}

__declspec(dllexport) void __cdecl ElkaFx_GetRealtimeCoreStatus(wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    writeWide(target.lastStatus, status, statusChars);
}
}
