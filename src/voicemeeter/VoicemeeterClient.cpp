#include "voicemeeter/VoicemeeterClient.h"

#include <avrt.h>
#include <windows.h>

#include <cstring>

namespace elka
{
namespace
{
constexpr char ClientName[] = "Elka VoiceMeeter FX Host";

class CallbackThreadAudioPriority
{
public:
    CallbackThreadAudioPriority() noexcept
    {
        DWORD taskIndex = 0;
        mmcssHandle = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
        if (mmcssHandle != nullptr)
            AvSetMmThreadPriority(mmcssHandle, AVRT_PRIORITY_HIGH);
    }

    ~CallbackThreadAudioPriority()
    {
        if (mmcssHandle != nullptr)
            AvRevertMmThreadCharacteristics(mmcssHandle);
    }

    CallbackThreadAudioPriority(const CallbackThreadAudioPriority&) = delete;
    CallbackThreadAudioPriority& operator=(const CallbackThreadAudioPriority&) = delete;

private:
    HANDLE mmcssHandle = nullptr;
};

void ensureCallbackThreadAudioPriority() noexcept
{
    thread_local CallbackThreadAudioPriority priority;
    (void)priority;
}
}

VoicemeeterClient::VoicemeeterClient(RealtimeEngine& engineRef)
    : engine(engineRef)
{
}

VoicemeeterClient::~VoicemeeterClient()
{
    disconnect();
}

bool VoicemeeterClient::connect(std::wstring& error)
{
    if (connectionState != ConnectionState::Disconnected)
    {
        if (customButton.active && !customButtonApplied)
        {
            std::wstring ignored;
            applyCustomButton(ignored);
        }
        return true;
    }

    if (!api.load(error))
        return false;

    const long result = api.login();
    if (result < 0)
    {
        error = L"VBVMR_Login failed with code " + std::to_wstring(result);
        return false;
    }

    connectionState = ConnectionState::Connected;
    customButtonApplied = false;
    if (customButton.active)
    {
        std::wstring ignored;
        applyCustomButton(ignored);
    }
    return true;
}

void VoicemeeterClient::disconnect() noexcept
{
    unregisterCallback();

    if (connectionState == ConnectionState::Connected)
        api.logout();

    connectionState = ConnectionState::Disconnected;
    customButtonApplied = false;
    api.unload();
}

bool VoicemeeterClient::registerCallback(CallbackMode modeToRegister, std::wstring& error)
{
    if (connectionState == ConnectionState::Disconnected && !connect(error))
        return false;

    if (connectionState == ConnectionState::Running)
        stop();

    if (connectionState == ConnectionState::CallbackRegistered)
    {
        api.audioCallbackUnregister();
        connectionState = ConnectionState::Connected;
    }

    char clientName[64] {};
    strncpy_s(clientName, ClientName, sizeof(clientName) - 1);

    const long result = api.audioCallbackRegister(toApiMode(modeToRegister), &VoicemeeterClient::audioCallback, this, clientName);
    if (result != 0)
    {
        if (result == 1)
        {
            error = L"VoiceMeeter callback is already registered by another application: ";
            error += std::wstring(clientName, clientName + strlen(clientName));
        }
        else
        {
            error = L"VBVMR_AudioCallbackRegister failed with code " + std::to_wstring(result);
        }
        return false;
    }

    resetCallbackStats();
    callbackMode = modeToRegister;
    connectionState = ConnectionState::CallbackRegistered;
    return true;
}

bool VoicemeeterClient::start(std::wstring& error)
{
    if (connectionState == ConnectionState::Disconnected && !connect(error))
        return false;

    if (connectionState == ConnectionState::Connected && !registerCallback(callbackMode, error))
        return false;

    if (connectionState == ConnectionState::Running)
        return true;

    const long result = api.audioCallbackStart();
    if (result != 0)
    {
        error = L"VBVMR_AudioCallbackStart failed with code " + std::to_wstring(result);
        unregisterCallback();
        return false;
    }

    connectionState = ConnectionState::Running;
    return true;
}

void VoicemeeterClient::stop() noexcept
{
    if (connectionState == ConnectionState::Running)
    {
        api.audioCallbackStop();
        connectionState = ConnectionState::CallbackRegistered;
    }
}

void VoicemeeterClient::unregisterCallback() noexcept
{
    stop();

    if (connectionState == ConnectionState::CallbackRegistered)
    {
        api.audioCallbackUnregister();
        connectionState = ConnectionState::Connected;
    }

    resetCallbackStats();
}

void VoicemeeterClient::setPreferredMode(CallbackMode modeToUse) noexcept
{
    callbackMode = modeToUse;
}

ConnectionState VoicemeeterClient::state() const noexcept
{
    return connectionState;
}

CallbackMode VoicemeeterClient::mode() const noexcept
{
    return callbackMode;
}

CallbackCommandStats VoicemeeterClient::callbackStats() const noexcept
{
    return CallbackCommandStats {
        callbackCommandCount.load(std::memory_order_acquire),
        callbackStartingCount.load(std::memory_order_acquire),
        callbackEndingCount.load(std::memory_order_acquire),
        callbackChangeCount.load(std::memory_order_acquire),
        callbackBufferInCount.load(std::memory_order_acquire),
        callbackBufferOutCount.load(std::memory_order_acquire),
        callbackBufferMainCount.load(std::memory_order_acquire),
        callbackLastCommand.load(std::memory_order_acquire)
    };
}

std::wstring VoicemeeterClient::statusText() const
{
    switch (connectionState)
    {
    case ConnectionState::Disconnected:
        return L"Disconnected";
    case ConnectionState::Connected:
        return L"Connected";
    case ConnectionState::CallbackRegistered:
        return L"Callback registered";
    case ConnectionState::Running:
        return L"Running";
    }

    return L"Unknown";
}

std::wstring VoicemeeterClient::dllPath() const
{
    return api.dllPath();
}

bool VoicemeeterClient::getVoicemeeterType(int& type) const noexcept
{
    type = 0;
    if (connectionState == ConnectionState::Disconnected)
        return false;

    long apiType = 0;
    if (api.getVoicemeeterType(&apiType) != 0 || apiType < 1 || apiType > 3)
        return false;

    type = static_cast<int>(apiType);
    return true;
}

bool VoicemeeterClient::getConfiguredSampleRate(int& sampleRate) const noexcept
{
    float value = 0.0f;
    if (!getParameterFloat("Option.sr", value) || value <= 0.0f)
        return false;

    sampleRate = static_cast<int>(value + 0.5f);
    return true;
}

bool VoicemeeterClient::refreshParameters() const noexcept
{
    return api.isParametersDirty() >= 0;
}

bool VoicemeeterClient::getParameterFloat(const char* parameterName, float& value) const noexcept
{
    value = 0.0f;
    if (parameterName == nullptr)
        return false;

    return api.getParameterFloat(parameterName, &value) == 0;
}

bool VoicemeeterClient::setParameterFloat(const char* parameterName, float value) const noexcept
{
    if (parameterName == nullptr)
        return false;

    return api.setParameterFloat(parameterName, value) == 0;
}

bool VoicemeeterClient::getLevel(int type, int channel, float& value) const noexcept
{
    value = 0.0f;
    if (type < 0 || channel < 0)
        return false;

    return api.getLevel(type, channel, &value) == 0;
}

long VoicemeeterClient::setCustomButton(
    long index,
    long type,
    long state,
    const std::wstring& label,
    HWND commandWindow,
    long commandId,
    std::wstring& error)
{
    if (index < 0 || index > 1)
    {
        error = L"VoiceMeeter custom button index must be 0 or 1.";
        return -1;
    }

    if (type < 0 || type > 2)
    {
        error = L"VoiceMeeter custom button type must be 0, 1, or 2.";
        return -1;
    }

    if (label.size() > 32)
    {
        error = L"VoiceMeeter custom button label cannot exceed 32 characters.";
        return -1;
    }

    if (commandWindow == nullptr || commandId <= 0)
    {
        error = L"VoiceMeeter custom button requires a valid command window and command ID.";
        return -1;
    }

    customButton.active = true;
    customButton.index = index;
    customButton.type = type;
    customButton.state = state;
    customButton.label = label;
    customButton.commandWindow = commandWindow;
    customButton.commandId = commandId;
    customButtonApplied = false;

    if (connectionState == ConnectionState::Disconnected && !connect(error))
        return -2;

    if (customButtonApplied)
        return 0;

    return applyCustomButton(error);
}

long VoicemeeterClient::clearCustomButton(
    std::wstring& error,
    long* releaseResult) noexcept
{
    const auto registration = customButton;
    customButton = {};
    customButtonApplied = false;

    if (releaseResult != nullptr)
        *releaseResult = 0;

    if (!registration.active || connectionState == ConnectionState::Disconnected)
        return 0;

    if (!api.supportsCustomButton())
    {
        error = L"Installed VoiceMeeter Remote API does not expose VBVMR_SetCustomButton.";
        return -1;
    }

    const long released = api.setCustomButton(
        registration.index,
        registration.type,
        0,
        registration.label.c_str(),
        registration.commandWindow,
        registration.commandId);
    if (releaseResult != nullptr)
        *releaseResult = released;

    Sleep(25);
    const long result = api.setCustomButton(
        registration.index,
        -1,
        0,
        L"",
        registration.commandWindow,
        registration.commandId);
    if (result != 0)
        error = L"VBVMR_SetCustomButton removal failed with code " + std::to_wstring(result) + L".";
    return result;
}

long VoicemeeterClient::forceHideCustomButton(
    long index,
    HWND commandWindow,
    long commandId) noexcept
{
    if (index < 0 ||
        index > 1 ||
        commandWindow == nullptr ||
        commandId <= 0 ||
        connectionState == ConnectionState::Disconnected ||
        !api.supportsCustomButton())
    {
        return -1;
    }

    return api.setCustomButton(index, -1, 0, L"", commandWindow, commandId);
}

long VoicemeeterClient::applyCustomButton(std::wstring& error) noexcept
{
    customButtonApplied = false;
    if (!customButton.active || connectionState == ConnectionState::Disconnected)
        return -2;

    if (!api.supportsCustomButton())
    {
        error = L"Installed VoiceMeeter Remote API does not expose VBVMR_SetCustomButton.";
        return -1;
    }

    const long result = api.setCustomButton(
        customButton.index,
        customButton.type,
        customButton.state,
        customButton.label.c_str(),
        customButton.commandWindow,
        customButton.commandId);
    customButtonApplied = result == 0;
    if (result != 0)
        error = L"VBVMR_SetCustomButton failed with code " + std::to_wstring(result) + L".";
    return result;
}

long __stdcall VoicemeeterClient::audioCallback(void* user, long command, void* data, long reserved) noexcept
{
    auto* self = static_cast<VoicemeeterClient*>(user);
    return self != nullptr ? self->handleAudioCallback(command, data, reserved) : 0;
}

long VoicemeeterClient::handleAudioCallback(long command, void* data, long) noexcept
{
    ensureCallbackThreadAudioPriority();

    const bool isBufferCommand =
        command == vmr::CommandBufferIn ||
        command == vmr::CommandBufferOut ||
        command == vmr::CommandBufferMain;

    if (isBufferCommand)
    {
        constexpr uint64_t StatsFlushInterval = 16;
        thread_local uint64_t pendingTotal = 0;
        thread_local uint64_t pendingBufferIn = 0;
        thread_local uint64_t pendingBufferOut = 0;
        thread_local uint64_t pendingBufferMain = 0;

        ++pendingTotal;
        if (command == vmr::CommandBufferIn)
            ++pendingBufferIn;
        else if (command == vmr::CommandBufferOut)
            ++pendingBufferOut;
        else
            ++pendingBufferMain;

        if (pendingTotal >= StatsFlushInterval)
        {
            callbackCommandCount.fetch_add(pendingTotal, std::memory_order_relaxed);
            if (pendingBufferIn > 0)
                callbackBufferInCount.fetch_add(pendingBufferIn, std::memory_order_relaxed);
            if (pendingBufferOut > 0)
                callbackBufferOutCount.fetch_add(pendingBufferOut, std::memory_order_relaxed);
            if (pendingBufferMain > 0)
                callbackBufferMainCount.fetch_add(pendingBufferMain, std::memory_order_relaxed);

            callbackLastCommand.store(command, std::memory_order_relaxed);
            pendingTotal = 0;
            pendingBufferIn = 0;
            pendingBufferOut = 0;
            pendingBufferMain = 0;
        }
    }
    else
    {
        callbackCommandCount.fetch_add(1, std::memory_order_relaxed);
        callbackLastCommand.store(command, std::memory_order_relaxed);

        switch (command)
        {
        case vmr::CommandStarting:
            callbackStartingCount.fetch_add(1, std::memory_order_relaxed);
            break;
        case vmr::CommandEnding:
            callbackEndingCount.fetch_add(1, std::memory_order_relaxed);
            break;
        case vmr::CommandChange:
            callbackChangeCount.fetch_add(1, std::memory_order_relaxed);
            break;
        default:
            break;
        }
    }

    if (command == vmr::CommandStarting || command == vmr::CommandChange)
    {
        auto* info = static_cast<vmr::AudioInfo*>(data);
        if (info != nullptr)
            engine.updateFormat(static_cast<int>(info->sampleRate), static_cast<int>(info->samplesPerFrame));

        return 0;
    }

    if (command != vmr::CommandBufferIn &&
        command != vmr::CommandBufferOut &&
        command != vmr::CommandBufferMain)
    {
        return 0;
    }

    auto* raw = static_cast<vmr::AudioBuffer*>(data);
    if (raw == nullptr)
        return 0;

    AudioBufferView buffer {
        static_cast<int>(raw->sampleRate),
        static_cast<int>(raw->samplesPerFrame),
        static_cast<int>(raw->inputChannels),
        static_cast<int>(raw->outputChannels),
        raw->read,
        raw->write
    };

    engine.process(buffer, toStreamKindForCommand(command, callbackMode));
    return 0;
}

long VoicemeeterClient::toApiMode(CallbackMode mode) noexcept
{
    switch (mode)
    {
    case CallbackMode::None:
        return 0;
    case CallbackMode::InputInsert:
        return vmr::ModeInputInsert;
    case CallbackMode::OutputInsert:
        return vmr::ModeOutputInsert;
    case CallbackMode::Main:
        return vmr::ModeMain;
    }

    return static_cast<long>(mode);
}

CallbackStreamKind VoicemeeterClient::toStreamKind(CallbackMode mode) noexcept
{
    switch (mode)
    {
    case CallbackMode::None:
        return CallbackStreamKind::InputInsert;
    case CallbackMode::InputInsert:
        return CallbackStreamKind::InputInsert;
    case CallbackMode::OutputInsert:
        return CallbackStreamKind::OutputInsert;
    case CallbackMode::Main:
        return CallbackStreamKind::Main;
    }

    return CallbackStreamKind::InputInsert;
}

CallbackStreamKind VoicemeeterClient::toStreamKindForCommand(long command, CallbackMode fallbackMode) noexcept
{
    switch (command)
    {
    case vmr::CommandBufferIn:
        return CallbackStreamKind::InputInsert;
    case vmr::CommandBufferOut:
        return CallbackStreamKind::OutputInsert;
    case vmr::CommandBufferMain:
        return CallbackStreamKind::Main;
    }

    return toStreamKind(fallbackMode);
}

void VoicemeeterClient::resetCallbackStats() noexcept
{
    callbackCommandCount.store(0, std::memory_order_release);
    callbackStartingCount.store(0, std::memory_order_release);
    callbackEndingCount.store(0, std::memory_order_release);
    callbackChangeCount.store(0, std::memory_order_release);
    callbackBufferInCount.store(0, std::memory_order_release);
    callbackBufferOutCount.store(0, std::memory_order_release);
    callbackBufferMainCount.store(0, std::memory_order_release);
    callbackLastCommand.store(0, std::memory_order_release);
}
}
