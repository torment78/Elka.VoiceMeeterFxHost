#include "voicemeeter/VoicemeeterClient.h"

#include <cstring>

namespace elka
{
namespace
{
constexpr char ClientName[] = "Elka VoiceMeeter FX Host";
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
        return true;

    if (!api.load(error))
        return false;

    const long result = api.login();
    if (result < 0)
    {
        error = L"VBVMR_Login failed with code " + std::to_wstring(result);
        return false;
    }

    connectionState = ConnectionState::Connected;
    return true;
}

void VoicemeeterClient::disconnect() noexcept
{
    stop();

    if (connectionState == ConnectionState::CallbackRegistered)
    {
        api.audioCallbackUnregister();
        connectionState = ConnectionState::Connected;
    }

    if (connectionState == ConnectionState::Connected)
        api.logout();

    connectionState = ConnectionState::Disconnected;
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

bool VoicemeeterClient::getLevel(int type, int channel, float& value) const noexcept
{
    value = 0.0f;
    if (type < 0 || channel < 0)
        return false;

    return api.getLevel(type, channel, &value) == 0;
}

long __stdcall VoicemeeterClient::audioCallback(void* user, long command, void* data, long reserved) noexcept
{
    auto* self = static_cast<VoicemeeterClient*>(user);
    return self != nullptr ? self->handleAudioCallback(command, data, reserved) : 0;
}

long VoicemeeterClient::handleAudioCallback(long command, void* data, long) noexcept
{
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
}
