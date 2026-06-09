#pragma once

#include "engine/RealtimeEngine.h"
#include "voicemeeter/VoicemeeterRemoteApi.h"

#include <string>

namespace elka
{
enum class CallbackMode
{
    InputInsert = 0x00000001,
    OutputInsert = 0x00000002,
    Main = 0x00000004
};

enum class ConnectionState
{
    Disconnected,
    Connected,
    CallbackRegistered,
    Running
};

class VoicemeeterClient
{
public:
    explicit VoicemeeterClient(RealtimeEngine& engine);
    ~VoicemeeterClient();

    bool connect(std::wstring& error);
    void disconnect() noexcept;

    bool registerCallback(CallbackMode mode, std::wstring& error);
    bool start(std::wstring& error);
    void stop() noexcept;

    void setPreferredMode(CallbackMode mode) noexcept;
    ConnectionState state() const noexcept;
    CallbackMode mode() const noexcept;
    std::wstring statusText() const;
    std::wstring dllPath() const;
    bool getConfiguredSampleRate(int& sampleRate) const noexcept;
    bool refreshParameters() const noexcept;
    bool getParameterFloat(const char* parameterName, float& value) const noexcept;
    bool getLevel(int type, int channel, float& value) const noexcept;

private:
    static long __stdcall audioCallback(void* user, long command, void* data, long reserved) noexcept;
    long handleAudioCallback(long command, void* data, long reserved) noexcept;

    static long toApiMode(CallbackMode mode) noexcept;
    static CallbackStreamKind toStreamKind(CallbackMode mode) noexcept;
    static CallbackStreamKind toStreamKindForCommand(long command, CallbackMode fallbackMode) noexcept;

    RealtimeEngine& engine;
    VoicemeeterRemoteApi api;
    ConnectionState connectionState = ConnectionState::Disconnected;
    CallbackMode callbackMode = CallbackMode::InputInsert;
};
}
