#pragma once

#include "engine/RealtimeEngine.h"
#include "voicemeeter/VoicemeeterRemoteApi.h"

#include <atomic>
#include <cstdint>
#include <string>

namespace elka
{
enum class CallbackMode
{
    None = 0x00000000,
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

struct CallbackCommandStats
{
    uint64_t total = 0;
    uint64_t starting = 0;
    uint64_t ending = 0;
    uint64_t change = 0;
    uint64_t bufferIn = 0;
    uint64_t bufferOut = 0;
    uint64_t bufferMain = 0;
    long lastCommand = 0;
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
    void unregisterCallback() noexcept;

    void setPreferredMode(CallbackMode mode) noexcept;
    ConnectionState state() const noexcept;
    CallbackMode mode() const noexcept;
    CallbackCommandStats callbackStats() const noexcept;
    std::wstring statusText() const;
    std::wstring dllPath() const;
    bool getVoicemeeterType(int& type) const noexcept;
    bool getConfiguredSampleRate(int& sampleRate) const noexcept;
    bool refreshParameters() const noexcept;
    bool getParameterFloat(const char* parameterName, float& value) const noexcept;
    bool setParameterFloat(const char* parameterName, float value) const noexcept;
    bool getLevel(int type, int channel, float& value) const noexcept;
    long setCustomButton(
        long index,
        long type,
        long state,
        const std::wstring& label,
        HWND commandWindow,
        long commandId,
        std::wstring& error);
    long clearCustomButton(std::wstring& error, long* releaseResult = nullptr) noexcept;
    long forceHideCustomButton(long index, HWND commandWindow, long commandId) noexcept;

private:
    static long __stdcall audioCallback(void* user, long command, void* data, long reserved) noexcept;
    long handleAudioCallback(long command, void* data, long reserved) noexcept;

    static long toApiMode(CallbackMode mode) noexcept;
    static CallbackStreamKind toStreamKind(CallbackMode mode) noexcept;
    static CallbackStreamKind toStreamKindForCommand(long command, CallbackMode fallbackMode) noexcept;
    void resetCallbackStats() noexcept;
    long applyCustomButton(std::wstring& error) noexcept;

    struct CustomButtonRegistration
    {
        bool active = false;
        long index = 0;
        long type = 1;
        long state = 0;
        std::wstring label;
        HWND commandWindow = nullptr;
        long commandId = 0;
    };

    RealtimeEngine& engine;
    VoicemeeterRemoteApi api;
    ConnectionState connectionState = ConnectionState::Disconnected;
    CallbackMode callbackMode = CallbackMode::None;
    std::atomic<uint64_t> callbackCommandCount { 0 };
    std::atomic<uint64_t> callbackStartingCount { 0 };
    std::atomic<uint64_t> callbackEndingCount { 0 };
    std::atomic<uint64_t> callbackChangeCount { 0 };
    std::atomic<uint64_t> callbackBufferInCount { 0 };
    std::atomic<uint64_t> callbackBufferOutCount { 0 };
    std::atomic<uint64_t> callbackBufferMainCount { 0 };
    std::atomic<long> callbackLastCommand { 0 };
    CustomButtonRegistration customButton;
    bool customButtonApplied = false;
};
}
