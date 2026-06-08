#pragma once

#include "voicemeeter/VoicemeeterRemoteTypes.h"

#include <string>
#include <windows.h>

namespace elka
{
class VoicemeeterRemoteApi
{
public:
    VoicemeeterRemoteApi() = default;
    ~VoicemeeterRemoteApi();

    VoicemeeterRemoteApi(const VoicemeeterRemoteApi&) = delete;
    VoicemeeterRemoteApi& operator=(const VoicemeeterRemoteApi&) = delete;

    bool load(std::wstring& error);
    void unload() noexcept;
    bool isLoaded() const noexcept;

    long login() const;
    long logout() const;
    long getVoicemeeterType(long* type) const;
    long getParameterFloat(const char* parameterName, float* value) const;
    long setParameterFloat(const char* parameterName, float value) const;
    long audioCallbackRegister(long mode, vmr::AudioCallback callback, void* user, char clientName[64]) const;
    long audioCallbackStart() const;
    long audioCallbackStop() const;
    long audioCallbackUnregister() const;

    const std::wstring& dllPath() const noexcept;

private:
    using LoginFn = long(__stdcall*)();
    using LogoutFn = long(__stdcall*)();
    using GetVoicemeeterTypeFn = long(__stdcall*)(long*);
    using GetParameterFloatFn = long(__stdcall*)(char*, float*);
    using SetParameterFloatFn = long(__stdcall*)(char*, float);
    using AudioCallbackRegisterFn = long(__stdcall*)(long, vmr::AudioCallback, void*, char[64]);
    using AudioCallbackStartFn = long(__stdcall*)();
    using AudioCallbackStopFn = long(__stdcall*)();
    using AudioCallbackUnregisterFn = long(__stdcall*)();

    static bool findDllPath(std::wstring& path, std::wstring& error);
    static bool readInstallPathFromRegistry(std::wstring& installPath);
    static std::wstring directoryOf(std::wstring path);

    template <typename Fn>
    bool loadFunction(Fn& out, const char* name, std::wstring& error)
    {
        out = reinterpret_cast<Fn>(GetProcAddress(module, name));
        if (out != nullptr)
            return true;

        error = L"Missing VoiceMeeter Remote API export: ";
        error += std::wstring(name, name + strlen(name));
        return false;
    }

    HMODULE module = nullptr;
    std::wstring loadedDllPath;

    LoginFn loginFn = nullptr;
    LogoutFn logoutFn = nullptr;
    GetVoicemeeterTypeFn getVoicemeeterTypeFn = nullptr;
    GetParameterFloatFn getParameterFloatFn = nullptr;
    SetParameterFloatFn setParameterFloatFn = nullptr;
    AudioCallbackRegisterFn audioCallbackRegisterFn = nullptr;
    AudioCallbackStartFn audioCallbackStartFn = nullptr;
    AudioCallbackStopFn audioCallbackStopFn = nullptr;
    AudioCallbackUnregisterFn audioCallbackUnregisterFn = nullptr;
};
}
