#include "voicemeeter/VoicemeeterRemoteApi.h"

#include <array>
#include <cstring>

namespace elka
{
namespace
{
constexpr wchar_t UninstallKey[] =
    L"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\VB:Voicemeeter {17359A74-1236-5467}";

std::wstring systemErrorMessage(DWORD error)
{
    wchar_t* buffer = nullptr;
    const DWORD size = FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr,
        error,
        MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
        reinterpret_cast<LPWSTR>(&buffer),
        0,
        nullptr);

    std::wstring message = size > 0 && buffer != nullptr ? buffer : L"Unknown error";
    if (buffer != nullptr)
        LocalFree(buffer);
    return message;
}
}

VoicemeeterRemoteApi::~VoicemeeterRemoteApi()
{
    unload();
}

bool VoicemeeterRemoteApi::load(std::wstring& error)
{
    if (module != nullptr)
        return true;

    std::wstring path;
    if (!findDllPath(path, error))
        return false;

    module = LoadLibraryW(path.c_str());
    if (module == nullptr)
    {
        error = L"Failed to load VoiceMeeter Remote DLL: " + path + L"\n" + systemErrorMessage(GetLastError());
        return false;
    }

    loadedDllPath = path;

    return loadFunction(loginFn, "VBVMR_Login", error) &&
           loadFunction(logoutFn, "VBVMR_Logout", error) &&
           loadFunction(getVoicemeeterTypeFn, "VBVMR_GetVoicemeeterType", error) &&
           loadFunction(getParameterFloatFn, "VBVMR_GetParameterFloat", error) &&
           loadFunction(setParameterFloatFn, "VBVMR_SetParameterFloat", error) &&
           loadFunction(audioCallbackRegisterFn, "VBVMR_AudioCallbackRegister", error) &&
           loadFunction(audioCallbackStartFn, "VBVMR_AudioCallbackStart", error) &&
           loadFunction(audioCallbackStopFn, "VBVMR_AudioCallbackStop", error) &&
           loadFunction(audioCallbackUnregisterFn, "VBVMR_AudioCallbackUnregister", error);
}

void VoicemeeterRemoteApi::unload() noexcept
{
    if (module != nullptr)
    {
        FreeLibrary(module);
        module = nullptr;
    }

    loginFn = nullptr;
    logoutFn = nullptr;
    getVoicemeeterTypeFn = nullptr;
    getParameterFloatFn = nullptr;
    setParameterFloatFn = nullptr;
    audioCallbackRegisterFn = nullptr;
    audioCallbackStartFn = nullptr;
    audioCallbackStopFn = nullptr;
    audioCallbackUnregisterFn = nullptr;
    loadedDllPath.clear();
}

bool VoicemeeterRemoteApi::isLoaded() const noexcept
{
    return module != nullptr;
}

long VoicemeeterRemoteApi::login() const
{
    return loginFn != nullptr ? loginFn() : -1;
}

long VoicemeeterRemoteApi::logout() const
{
    return logoutFn != nullptr ? logoutFn() : -1;
}

long VoicemeeterRemoteApi::getVoicemeeterType(long* type) const
{
    return getVoicemeeterTypeFn != nullptr ? getVoicemeeterTypeFn(type) : -1;
}

long VoicemeeterRemoteApi::getParameterFloat(const char* parameterName, float* value) const
{
    return getParameterFloatFn != nullptr ? getParameterFloatFn(const_cast<char*>(parameterName), value) : -1;
}

long VoicemeeterRemoteApi::setParameterFloat(const char* parameterName, float value) const
{
    return setParameterFloatFn != nullptr ? setParameterFloatFn(const_cast<char*>(parameterName), value) : -1;
}

long VoicemeeterRemoteApi::audioCallbackRegister(
    long mode,
    vmr::AudioCallback callback,
    void* user,
    char clientName[64]) const
{
    return audioCallbackRegisterFn != nullptr
        ? audioCallbackRegisterFn(mode, callback, user, clientName)
        : -1;
}

long VoicemeeterRemoteApi::audioCallbackStart() const
{
    return audioCallbackStartFn != nullptr ? audioCallbackStartFn() : -1;
}

long VoicemeeterRemoteApi::audioCallbackStop() const
{
    return audioCallbackStopFn != nullptr ? audioCallbackStopFn() : -1;
}

long VoicemeeterRemoteApi::audioCallbackUnregister() const
{
    return audioCallbackUnregisterFn != nullptr ? audioCallbackUnregisterFn() : -1;
}

const std::wstring& VoicemeeterRemoteApi::dllPath() const noexcept
{
    return loadedDllPath;
}

bool VoicemeeterRemoteApi::findDllPath(std::wstring& path, std::wstring& error)
{
    std::wstring installPath;
    if (!readInstallPathFromRegistry(installPath))
    {
        error = L"VoiceMeeter install path was not found in the registry.";
        return false;
    }

#if defined(_WIN64)
    path = installPath + L"\\VoicemeeterRemote64.dll";
#else
    path = installPath + L"\\VoicemeeterRemote.dll";
#endif

    const DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
    {
        error = L"VoiceMeeter Remote DLL was not found at: " + path;
        return false;
    }

    return true;
}

bool VoicemeeterRemoteApi::readInstallPathFromRegistry(std::wstring& installPath)
{
    HKEY key = nullptr;
    const LSTATUS openStatus = RegOpenKeyExW(HKEY_LOCAL_MACHINE, UninstallKey, 0, KEY_READ, &key);
    if (openStatus != ERROR_SUCCESS)
        return false;

    std::array<wchar_t, 1024> uninstallString {};
    DWORD type = 0;
    DWORD bytes = static_cast<DWORD>(uninstallString.size() * sizeof(wchar_t));
    const LSTATUS queryStatus = RegQueryValueExW(
        key,
        L"UninstallString",
        nullptr,
        &type,
        reinterpret_cast<LPBYTE>(uninstallString.data()),
        &bytes);

    RegCloseKey(key);

    if (queryStatus != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ))
        return false;

    installPath = directoryOf(uninstallString.data());
    return !installPath.empty();
}

std::wstring VoicemeeterRemoteApi::directoryOf(std::wstring path)
{
    if (path.size() >= 2 && path.front() == L'"' && path.back() == L'"')
        path = path.substr(1, path.size() - 2);

    const auto slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos)
        return {};

    return path.substr(0, slash);
}
}
