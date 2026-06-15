#include "engine/RealtimeEngine.h"
#include "insert_asio/InsertAsioHost.h"
#include "plugins/PluginHostLayer.h"
#include "voicemeeter/VoicemeeterClient.h"

#include <windows.h>
#include <audioclient.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <cwchar>
#include <cstring>
#include <propkeydef.h>
#include <functiondiscoverykeys_devpkey.h>
#include <ksmedia.h>
#include <mmreg.h>
#include <mmdeviceapi.h>
#include <memory>
#include <mutex>
#include <propsys.h>
#include <string>
#include <thread>
#include <vector>
#include <wrl/client.h>

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
    int voicemeeterPreFaderLevelPercent = -1;
    int voicemeeterPostFaderLevelPercent = -1;
    int voicemeeterPostMuteLevelPercent = -1;
    int voicemeeterNextPreFaderLevelPercent = -1;
    int voicemeeterNextPostFaderLevelPercent = -1;
    int voicemeeterNextPostMuteLevelPercent = -1;
    int voicemeeterInputMaxLevelPercent = -1;
    int voicemeeterInputMaxChannel = -1;
    int voicemeeterOutputLevelPercent = -1;
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

struct NativeExternalWasapiPingResult
{
    int status = 0;
    int sampleRate = 0;
    int latencySamples = -1;
    int peakPercent = 0;
    int renderChannels = 0;
    int captureChannels = 0;
    double latencyMilliseconds = 0.0;
};

class NativeHost
{
public:
    NativeHost()
        : insertAsio(engine),
          client(engine)
    {
    }

    RealtimeEngine engine;
    InsertAsioHost insertAsio;
    PluginHostLayer plugins;
    VoicemeeterClient client;
    CallbackMode mode = CallbackMode::InputInsert;
    std::wstring lastStatus = L"Native engine ready";
    std::array<int, 34> savedPatchInsertStates {};
    std::array<bool, 34> savedPatchInsertStateValid {};
    bool hasSavedPatchInsertStates = false;
};

std::mutex g_mutex;
std::mutex g_scanMutex;
std::unique_ptr<NativeHost> g_host;

struct ScanProgressState
{
    bool running = false;
    int current = 0;
    int total = 0;
    std::wstring stage;
    std::wstring path;
};

std::mutex g_scanProgressMutex;
ScanProgressState g_scanProgress;
std::mutex g_pluginLoadProgressMutex;
ScanProgressState g_pluginLoadProgress;

constexpr int ScanFormatVst3 = 1;
constexpr int ScanFormatVst2 = 2;
constexpr int ScanFormatAll = ScanFormatVst3 | ScanFormatVst2;
constexpr int ExternalPingStatusFailed = 0;
constexpr int ExternalPingStatusDetected = 1;
constexpr int ExternalPingStatusTimeout = 2;
constexpr float ExternalPingAmplitude = 0.85f;
constexpr float ExternalPingThreshold = 0.35f;
constexpr int ExternalPingPulseFrames = 128;
constexpr int MaxInsertPatchChannels = 34;

class ScopedCom
{
public:
    explicit ScopedCom(DWORD flags) noexcept
        : result(CoInitializeEx(nullptr, flags)),
          shouldUninitialize(result == S_OK || result == S_FALSE)
    {
    }

    ~ScopedCom()
    {
        if (shouldUninitialize)
            CoUninitialize();
    }

    HRESULT Result() const noexcept
    {
        return result == RPC_E_CHANGED_MODE ? S_OK : result;
    }

private:
    HRESULT result = E_FAIL;
    bool shouldUninitialize = false;
};

CallbackMode callbackModeFromApi(int mode) noexcept
{
    const int validBits =
        static_cast<int>(CallbackMode::InputInsert) |
        static_cast<int>(CallbackMode::OutputInsert) |
        static_cast<int>(CallbackMode::Main);
    const int safeMode = mode & validBits;
    return static_cast<CallbackMode>(safeMode != 0 ? safeMode : static_cast<int>(CallbackMode::InputInsert));
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

int readRoundedParameter(NativeHost& target, const char* parameterName) noexcept
{
    float value = 0.0f;
    if (!target.client.getParameterFloat(parameterName, value))
        return -1;

    return static_cast<int>(std::lround(value));
}

int readIndexedParameter(NativeHost& target, const char* format, int index) noexcept
{
    if (index < 0)
        return -1;

    char parameterName[64] {};
    const int written = std::snprintf(parameterName, sizeof(parameterName), format, index);
    if (written <= 0 || written >= static_cast<int>(sizeof(parameterName)))
        return -1;

    return readRoundedParameter(target, parameterName);
}

bool writeIndexedParameter(NativeHost& target, const char* format, int index, float value) noexcept
{
    if (index < 0)
        return false;

    char parameterName[64] {};
    const int written = std::snprintf(parameterName, sizeof(parameterName), format, index);
    if (written <= 0 || written >= static_cast<int>(sizeof(parameterName)))
        return false;

    return target.client.setParameterFloat(parameterName, value);
}

int readPatchInsertState(NativeHost& target, int channel) noexcept
{
    auto result = readIndexedParameter(target, "Patch.insert[%d]", channel);
    if (result < 0)
        result = readIndexedParameter(target, "patch.insert[%d]", channel);

    return result < 0 ? 0 : (result != 0 ? 1 : 0);
}

bool writePatchInsertState(NativeHost& target, int channel, int enabled) noexcept
{
    const float value = enabled != 0 ? 1.0f : 0.0f;
    return writeIndexedParameter(target, "Patch.insert[%d]", channel, value) ||
           writeIndexedParameter(target, "patch.insert[%d]", channel, value);
}

bool savePatchInsertStateIfNeeded(NativeHost& target, int channel) noexcept
{
    if (channel < 0 || channel >= MaxInsertPatchChannels)
        return false;

    const auto index = static_cast<size_t>(channel);
    if (!target.savedPatchInsertStateValid[index])
    {
        target.savedPatchInsertStates[index] = readPatchInsertState(target, channel);
        target.savedPatchInsertStateValid[index] = true;
    }

    target.hasSavedPatchInsertStates = true;
    return true;
}

int restoreInsertPatchChannels(NativeHost& target) noexcept
{
    if (!target.hasSavedPatchInsertStates)
        return 0;

    int restored = 0;
    for (int channel = 0; channel < MaxInsertPatchChannels; ++channel)
    {
        const auto index = static_cast<size_t>(channel);
        if (!target.savedPatchInsertStateValid[index])
            continue;

        if (writePatchInsertState(target, channel, target.savedPatchInsertStates[index]))
            ++restored;
        target.savedPatchInsertStateValid[index] = false;
    }

    target.hasSavedPatchInsertStates = false;
    return restored;
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

std::wstring widenUtf8(const std::string& value)
{
    if (value.empty())
        return {};

    const int required = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, nullptr, 0);
    if (required <= 0)
        return {};

    std::wstring result(static_cast<size_t>(required - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, result.data(), required);
    return result;
}

std::string narrowWide(const wchar_t* value)
{
    if (value == nullptr || value[0] == L'\0')
        return {};

    const int required = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (required <= 0)
        return {};

    std::string result(static_cast<size_t>(required - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), required, nullptr, nullptr);
    return result;
}

void setScanProgress(bool running, const std::string& stage, const std::string& path, int current, int total)
{
    std::lock_guard lock(g_scanProgressMutex);
    g_scanProgress.running = running;
    g_scanProgress.current = current;
    g_scanProgress.total = total;
    g_scanProgress.stage = widenUtf8(stage);
    g_scanProgress.path = widenUtf8(path);
}

std::wstring scanProgressSnapshot()
{
    std::lock_guard lock(g_scanProgressMutex);
    std::wstring snapshot;
    snapshot += L"running=" + std::to_wstring(g_scanProgress.running ? 1 : 0) + L"\n";
    snapshot += L"current=" + std::to_wstring(g_scanProgress.current) + L"\n";
    snapshot += L"total=" + std::to_wstring(g_scanProgress.total) + L"\n";
    snapshot += L"stage=" + g_scanProgress.stage + L"\n";
    snapshot += L"path=" + g_scanProgress.path + L"\n";
    return snapshot;
}

void setPluginLoadProgress(bool running, const std::string& stage, const std::string& detail)
{
    std::lock_guard lock(g_pluginLoadProgressMutex);
    g_pluginLoadProgress.running = running;
    g_pluginLoadProgress.current = 0;
    g_pluginLoadProgress.total = 0;
    g_pluginLoadProgress.stage = widenUtf8(stage);
    g_pluginLoadProgress.path = widenUtf8(detail);
}

std::wstring pluginLoadProgressSnapshot()
{
    std::lock_guard lock(g_pluginLoadProgressMutex);
    std::wstring snapshot;
    snapshot += L"running=" + std::to_wstring(g_pluginLoadProgress.running ? 1 : 0) + L"\n";
    snapshot += L"stage=" + g_pluginLoadProgress.stage + L"\n";
    snapshot += L"detail=" + g_pluginLoadProgress.path + L"\n";
    return snapshot;
}

std::vector<std::string> splitPluginFolders(const wchar_t* folders)
{
    std::vector<std::string> result;
    std::wstring text = folders != nullptr ? folders : L"";
    std::wstring current;
    for (wchar_t ch : text)
    {
        if (ch == L';' || ch == L'\n' || ch == L'\r')
        {
            if (!current.empty())
            {
                result.push_back(narrowWide(current.c_str()));
                current.clear();
            }
            continue;
        }

        current.push_back(ch);
    }

    if (!current.empty())
        result.push_back(narrowWide(current.c_str()));

    result.erase(
        std::remove_if(result.begin(), result.end(), [](const std::string& value) { return value.empty(); }),
        result.end());
    std::sort(result.begin(), result.end());
    result.erase(std::unique(result.begin(), result.end()), result.end());
    return result;
}

NativeHost& host()
{
    if (!g_host)
        g_host = std::make_unique<NativeHost>();

    return *g_host;
}

int normalizePluginScanFlags(int formatFlags) noexcept
{
    const int normalized = formatFlags & ScanFormatAll;
    return normalized != 0 ? normalized : ScanFormatAll;
}

std::wstring sanitizeDeviceText(std::wstring text)
{
    std::replace(text.begin(), text.end(), L'\t', L' ');
    std::replace(text.begin(), text.end(), L'\r', L' ');
    std::replace(text.begin(), text.end(), L'\n', L' ');
    return text;
}

std::wstring deviceFriendlyName(IMMDevice* device)
{
    if (device == nullptr)
        return L"Windows audio device";

    Microsoft::WRL::ComPtr<IPropertyStore> properties;
    if (FAILED(device->OpenPropertyStore(STGM_READ, &properties)))
        return L"Windows audio device";

    PROPVARIANT friendlyName;
    PropVariantInit(&friendlyName);
    std::wstring name = L"Windows audio device";
    if (SUCCEEDED(properties->GetValue(PKEY_Device_FriendlyName, &friendlyName)) &&
        friendlyName.vt == VT_LPWSTR &&
        friendlyName.pwszVal != nullptr)
    {
        name = friendlyName.pwszVal;
    }

    PropVariantClear(&friendlyName);
    return sanitizeDeviceText(name);
}

int listWasapiDevices(EDataFlow flow, wchar_t* buffer, int bufferChars)
{
    if (buffer == nullptr || bufferChars <= 0)
        return -1;

    buffer[0] = L'\0';

    ScopedCom com(COINIT_MULTITHREADED);
    if (FAILED(com.Result()))
        return static_cast<int>(com.Result());

    using Microsoft::WRL::ComPtr;
    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT hr = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        IID_PPV_ARGS(&enumerator));
    if (FAILED(hr))
        return static_cast<int>(hr);

    ComPtr<IMMDeviceCollection> devices;
    hr = enumerator->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &devices);
    if (FAILED(hr))
        return static_cast<int>(hr);

    UINT count = 0;
    hr = devices->GetCount(&count);
    if (FAILED(hr))
        return static_cast<int>(hr);

    std::wstring result;
    for (UINT index = 0; index < count; ++index)
    {
        ComPtr<IMMDevice> device;
        if (FAILED(devices->Item(index, &device)))
            continue;

        wchar_t* id = nullptr;
        if (FAILED(device->GetId(&id)) || id == nullptr)
            continue;

        result += id;
        result += L'\t';
        result += deviceFriendlyName(device.Get());
        result += L'\n';
        CoTaskMemFree(id);
    }

    writeWide(result, buffer, bufferChars);
    return static_cast<int>(count);
}

bool isFloatFormat(const WAVEFORMATEX* format) noexcept
{
    if (format == nullptr)
        return false;

    if (format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
        return true;

    if (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
        format->cbSize >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX))
    {
        const auto* extensible = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format);
        return IsEqualGUID(extensible->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    }

    return false;
}

bool isPcmFormat(const WAVEFORMATEX* format) noexcept
{
    if (format == nullptr)
        return false;

    if (format->wFormatTag == WAVE_FORMAT_PCM)
        return true;

    if (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
        format->cbSize >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX))
    {
        const auto* extensible = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format);
        return IsEqualGUID(extensible->SubFormat, KSDATAFORMAT_SUBTYPE_PCM);
    }

    return false;
}

void writeAudioSample(BYTE* data, UINT32 frameIndex, UINT32 channelIndex, const WAVEFORMATEX* format, float value) noexcept
{
    if (data == nullptr || format == nullptr || format->nChannels == 0)
        return;

    const auto channelCount = static_cast<UINT32>(format->nChannels);
    const auto bytesPerSample = static_cast<UINT32>(format->nBlockAlign / channelCount);
    if (bytesPerSample == 0)
        return;

    BYTE* sample = data +
        (static_cast<size_t>(frameIndex) * static_cast<size_t>(format->nBlockAlign)) +
        (static_cast<size_t>(channelIndex) * static_cast<size_t>(bytesPerSample));

    const float clamped = std::clamp(value, -1.0f, 1.0f);
    if (isFloatFormat(format) && format->wBitsPerSample == 32 && bytesPerSample >= 4)
    {
        const float sampleValue = clamped;
        std::memcpy(sample, &sampleValue, sizeof(sampleValue));
        return;
    }

    if (!isPcmFormat(format))
        return;

    if (format->wBitsPerSample == 16 && bytesPerSample >= 2)
    {
        const auto sampleValue = static_cast<int16_t>(std::lrint(clamped * 32767.0f));
        std::memcpy(sample, &sampleValue, sizeof(sampleValue));
        return;
    }

    if (format->wBitsPerSample == 24 && bytesPerSample >= 3)
    {
        const auto sampleValue = static_cast<int32_t>(std::lrint(clamped * 8388607.0f));
        sample[0] = static_cast<BYTE>(sampleValue & 0xff);
        sample[1] = static_cast<BYTE>((sampleValue >> 8) & 0xff);
        sample[2] = static_cast<BYTE>((sampleValue >> 16) & 0xff);
        return;
    }

    if (format->wBitsPerSample == 32 && bytesPerSample >= 4)
    {
        const auto sampleValue = static_cast<int32_t>(std::lrint(clamped * 2147483647.0f));
        std::memcpy(sample, &sampleValue, sizeof(sampleValue));
    }
}

float readAudioSample(const BYTE* data, UINT32 frameIndex, UINT32 channelIndex, const WAVEFORMATEX* format) noexcept
{
    if (data == nullptr || format == nullptr || format->nChannels == 0)
        return 0.0f;

    const auto channelCount = static_cast<UINT32>(format->nChannels);
    const auto bytesPerSample = static_cast<UINT32>(format->nBlockAlign / channelCount);
    if (bytesPerSample == 0)
        return 0.0f;

    const BYTE* sample = data +
        (static_cast<size_t>(frameIndex) * static_cast<size_t>(format->nBlockAlign)) +
        (static_cast<size_t>(channelIndex) * static_cast<size_t>(bytesPerSample));

    if (isFloatFormat(format) && format->wBitsPerSample == 32 && bytesPerSample >= 4)
    {
        float value = 0.0f;
        std::memcpy(&value, sample, sizeof(value));
        return std::clamp(value, -4.0f, 4.0f);
    }

    if (!isPcmFormat(format))
        return 0.0f;

    if (format->wBitsPerSample == 16 && bytesPerSample >= 2)
    {
        int16_t value = 0;
        std::memcpy(&value, sample, sizeof(value));
        return static_cast<float>(value) / 32768.0f;
    }

    if (format->wBitsPerSample == 24 && bytesPerSample >= 3)
    {
        int32_t value =
            static_cast<int32_t>(sample[0]) |
            (static_cast<int32_t>(sample[1]) << 8) |
            (static_cast<int32_t>(sample[2]) << 16);
        if ((value & 0x00800000) != 0)
            value |= static_cast<int32_t>(0xff000000);
        return static_cast<float>(value) / 8388608.0f;
    }

    if (format->wBitsPerSample == 32 && bytesPerSample >= 4)
    {
        int32_t value = 0;
        std::memcpy(&value, sample, sizeof(value));
        return static_cast<float>(value) / 2147483648.0f;
    }

    return 0.0f;
}

void writePulseBuffer(BYTE* data, UINT32 frames, const WAVEFORMATEX* format) noexcept
{
    if (data == nullptr || format == nullptr)
        return;

    std::memset(data, 0, static_cast<size_t>(frames) * static_cast<size_t>(format->nBlockAlign));
    const UINT32 pulseFrames = std::min<UINT32>(frames, ExternalPingPulseFrames);
    for (UINT32 frame = 0; frame < pulseFrames; ++frame)
    {
        const float value = (frame % 2 == 0 ? 1.0f : -1.0f) * ExternalPingAmplitude;
        for (UINT32 channel = 0; channel < format->nChannels; ++channel)
            writeAudioSample(data, frame, channel, format, value);
    }
}

void drainCapturePackets(IAudioCaptureClient* captureClient) noexcept
{
    if (captureClient == nullptr)
        return;

    UINT32 packetFrames = 0;
    while (SUCCEEDED(captureClient->GetNextPacketSize(&packetFrames)) && packetFrames > 0)
    {
        BYTE* data = nullptr;
        DWORD flags = 0;
        UINT32 frames = 0;
        if (FAILED(captureClient->GetBuffer(&data, &frames, &flags, nullptr, nullptr)))
            return;

        captureClient->ReleaseBuffer(frames);
    }
}

double elapsedMilliseconds(LARGE_INTEGER start, LARGE_INTEGER end) noexcept
{
    LARGE_INTEGER frequency {};
    QueryPerformanceFrequency(&frequency);
    if (frequency.QuadPart <= 0)
        return 0.0;

    return (static_cast<double>(end.QuadPart - start.QuadPart) * 1000.0) /
        static_cast<double>(frequency.QuadPart);
}

int runExternalWasapiPing(
    const wchar_t* renderDeviceId,
    const wchar_t* captureDeviceId,
    int timeoutMilliseconds,
    NativeExternalWasapiPingResult* result,
    wchar_t* status,
    int statusChars)
{
    if (result == nullptr)
        return -1;

    *result = NativeExternalWasapiPingResult {};
    result->status = ExternalPingStatusFailed;

    if (renderDeviceId == nullptr || renderDeviceId[0] == L'\0' ||
        captureDeviceId == nullptr || captureDeviceId[0] == L'\0')
    {
        writeWide(L"Select a render device and a capture device.", status, statusChars);
        return -1;
    }

    ScopedCom com(COINIT_MULTITHREADED);
    if (FAILED(com.Result()))
    {
        writeWide(L"Could not initialize COM for WASAPI.", status, statusChars);
        return static_cast<int>(com.Result());
    }

    using Microsoft::WRL::ComPtr;
    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT hr = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        IID_PPV_ARGS(&enumerator));
    if (FAILED(hr))
    {
        writeWide(L"Could not create the Windows audio device enumerator.", status, statusChars);
        return static_cast<int>(hr);
    }

    ComPtr<IMMDevice> renderDevice;
    hr = enumerator->GetDevice(renderDeviceId, &renderDevice);
    if (FAILED(hr))
    {
        writeWide(L"Could not open the selected render device.", status, statusChars);
        return static_cast<int>(hr);
    }

    ComPtr<IMMDevice> captureDevice;
    hr = enumerator->GetDevice(captureDeviceId, &captureDevice);
    if (FAILED(hr))
    {
        writeWide(L"Could not open the selected capture device.", status, statusChars);
        return static_cast<int>(hr);
    }

    ComPtr<IAudioClient> renderClient;
    hr = renderDevice->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &renderClient);
    if (FAILED(hr))
    {
        writeWide(L"Could not activate WASAPI render client.", status, statusChars);
        return static_cast<int>(hr);
    }

    ComPtr<IAudioClient> captureClient;
    hr = captureDevice->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &captureClient);
    if (FAILED(hr))
    {
        writeWide(L"Could not activate WASAPI capture client.", status, statusChars);
        return static_cast<int>(hr);
    }

    WAVEFORMATEX* renderFormat = nullptr;
    WAVEFORMATEX* captureFormat = nullptr;
    hr = renderClient->GetMixFormat(&renderFormat);
    if (FAILED(hr) || renderFormat == nullptr)
    {
        writeWide(L"Could not read render mix format.", status, statusChars);
        return static_cast<int>(FAILED(hr) ? hr : E_FAIL);
    }

    hr = captureClient->GetMixFormat(&captureFormat);
    if (FAILED(hr) || captureFormat == nullptr)
    {
        CoTaskMemFree(renderFormat);
        writeWide(L"Could not read capture mix format.", status, statusChars);
        return static_cast<int>(FAILED(hr) ? hr : E_FAIL);
    }

    const REFERENCE_TIME bufferDuration = 1000000; // 100 ms
    const DWORD streamFlags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    hr = renderClient->Initialize(AUDCLNT_SHAREMODE_SHARED, streamFlags, bufferDuration, 0, renderFormat, nullptr);
    if (FAILED(hr))
    {
        CoTaskMemFree(renderFormat);
        CoTaskMemFree(captureFormat);
        writeWide(L"Could not initialize WASAPI render stream.", status, statusChars);
        return static_cast<int>(hr);
    }

    hr = captureClient->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, bufferDuration, 0, captureFormat, nullptr);
    if (FAILED(hr))
    {
        CoTaskMemFree(renderFormat);
        CoTaskMemFree(captureFormat);
        writeWide(L"Could not initialize WASAPI capture stream.", status, statusChars);
        return static_cast<int>(hr);
    }

    ComPtr<IAudioRenderClient> renderService;
    hr = renderClient->GetService(IID_PPV_ARGS(&renderService));
    if (FAILED(hr))
    {
        CoTaskMemFree(renderFormat);
        CoTaskMemFree(captureFormat);
        writeWide(L"Could not get WASAPI render buffer.", status, statusChars);
        return static_cast<int>(hr);
    }

    ComPtr<IAudioCaptureClient> captureService;
    hr = captureClient->GetService(IID_PPV_ARGS(&captureService));
    if (FAILED(hr))
    {
        CoTaskMemFree(renderFormat);
        CoTaskMemFree(captureFormat);
        writeWide(L"Could not get WASAPI capture buffer.", status, statusChars);
        return static_cast<int>(hr);
    }

    UINT32 renderBufferFrames = 0;
    hr = renderClient->GetBufferSize(&renderBufferFrames);
    if (FAILED(hr) || renderBufferFrames == 0)
    {
        CoTaskMemFree(renderFormat);
        CoTaskMemFree(captureFormat);
        writeWide(L"Could not get render buffer size.", status, statusChars);
        return static_cast<int>(FAILED(hr) ? hr : E_FAIL);
    }

    result->sampleRate = static_cast<int>(captureFormat->nSamplesPerSec);
    result->renderChannels = renderFormat->nChannels;
    result->captureChannels = captureFormat->nChannels;

    hr = captureClient->Start();
    if (FAILED(hr))
    {
        CoTaskMemFree(renderFormat);
        CoTaskMemFree(captureFormat);
        writeWide(L"Could not start WASAPI capture stream.", status, statusChars);
        return static_cast<int>(hr);
    }

    const auto drainDeadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(50);
    while (std::chrono::steady_clock::now() < drainDeadline)
    {
        drainCapturePackets(captureService.Get());
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }

    UINT32 padding = 0;
    renderClient->GetCurrentPadding(&padding);
    UINT32 writableFrames = renderBufferFrames > padding ? renderBufferFrames - padding : renderBufferFrames;
    writableFrames = std::max<UINT32>(writableFrames, 1);

    BYTE* renderData = nullptr;
    hr = renderService->GetBuffer(writableFrames, &renderData);
    if (FAILED(hr))
    {
        captureClient->Stop();
        CoTaskMemFree(renderFormat);
        CoTaskMemFree(captureFormat);
        writeWide(L"Could not acquire WASAPI render packet.", status, statusChars);
        return static_cast<int>(hr);
    }

    writePulseBuffer(renderData, writableFrames, renderFormat);
    renderService->ReleaseBuffer(writableFrames, 0);

    LARGE_INTEGER started {};
    QueryPerformanceCounter(&started);
    hr = renderClient->Start();
    if (FAILED(hr))
    {
        captureClient->Stop();
        CoTaskMemFree(renderFormat);
        CoTaskMemFree(captureFormat);
        writeWide(L"Could not start WASAPI render stream.", status, statusChars);
        return static_cast<int>(hr);
    }

    const int safeTimeout = std::clamp(timeoutMilliseconds, 100, 30000);
    const auto timeout = std::chrono::steady_clock::now() + std::chrono::milliseconds(safeTimeout);
    float peak = 0.0f;
    while (std::chrono::steady_clock::now() < timeout)
    {
        UINT32 packetFrames = 0;
        while (SUCCEEDED(captureService->GetNextPacketSize(&packetFrames)) && packetFrames > 0)
        {
            BYTE* captureData = nullptr;
            DWORD flags = 0;
            UINT32 frames = 0;
            hr = captureService->GetBuffer(&captureData, &frames, &flags, nullptr, nullptr);
            if (FAILED(hr))
                break;

            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
            {
                for (UINT32 frame = 0; frame < frames; ++frame)
                {
                    for (UINT32 channel = 0; channel < captureFormat->nChannels; ++channel)
                    {
                        const float value = std::abs(readAudioSample(captureData, frame, channel, captureFormat));
                        peak = std::max(peak, value);
                        if (value >= ExternalPingThreshold)
                        {
                            LARGE_INTEGER detected {};
                            QueryPerformanceCounter(&detected);
                            result->status = ExternalPingStatusDetected;
                            result->latencyMilliseconds = elapsedMilliseconds(started, detected);
                            result->latencySamples = static_cast<int>(
                                std::lrint((result->latencyMilliseconds * static_cast<double>(captureFormat->nSamplesPerSec)) / 1000.0));
                            result->peakPercent = std::clamp(static_cast<int>(std::lrint(peak * 100.0f)), 0, 1000);

                            captureService->ReleaseBuffer(frames);
                            renderClient->Stop();
                            captureClient->Stop();
                            CoTaskMemFree(renderFormat);
                            CoTaskMemFree(captureFormat);
                            writeWide(L"External WASAPI ping detected.", status, statusChars);
                            return 0;
                        }
                    }
                }
            }

            captureService->ReleaseBuffer(frames);
            if (FAILED(captureService->GetNextPacketSize(&packetFrames)))
                break;
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }

    renderClient->Stop();
    captureClient->Stop();
    CoTaskMemFree(renderFormat);
    CoTaskMemFree(captureFormat);
    result->status = ExternalPingStatusTimeout;
    result->peakPercent = std::clamp(static_cast<int>(std::lrint(peak * 100.0f)), 0, 1000);
    writeWide(L"External WASAPI ping timed out.", status, statusChars);
    return 1;
}

int clampVoiceMeeterSampleRate(int sampleRate) noexcept
{
    return sampleRate > 0 ? std::clamp(sampleRate, 8000, 192000) : 0;
}

int configuredVoiceMeeterSampleRate(NativeHost& target) noexcept
{
    int sampleRate = 0;
    return target.client.getConfiguredSampleRate(sampleRate)
        ? clampVoiceMeeterSampleRate(sampleRate)
        : 0;
}

int activeVoiceMeeterSampleRate(NativeHost& target) noexcept
{
    const auto stats = target.engine.getStats();
    const int liveSampleRate = clampVoiceMeeterSampleRate(stats.sampleRate);
    if (liveSampleRate > 0)
        return liveSampleRate;

    return configuredVoiceMeeterSampleRate(target);
}

int desiredInsertAsioSampleRate(NativeHost& target) noexcept
{
    const int configuredSampleRate = configuredVoiceMeeterSampleRate(target);
    if (configuredSampleRate > 0)
        return configuredSampleRate;

    return activeVoiceMeeterSampleRate(target);
}

int desiredInsertAsioBlockSize(NativeHost& target) noexcept
{
    const auto stats = target.engine.getStats();
    return stats.blockSize > 0 ? stats.blockSize : 512;
}

bool startLocked(NativeHost& target, std::wstring& error)
{
    if (!target.client.connect(error))
        return false;

    const int configuredSampleRate = configuredVoiceMeeterSampleRate(target);
    if (configuredSampleRate > 0 && !target.engine.prepareDelayBuffers(configuredSampleRate))
    {
        error = L"Failed to allocate delay buffers for " + std::to_wstring(configuredSampleRate) + L" Hz";
        return false;
    }

    if (!target.client.start(error))
        return false;

    const int activeSampleRate = activeVoiceMeeterSampleRate(target);
    if (activeSampleRate > 0 &&
        target.engine.getDelayBufferSampleRate() != activeSampleRate &&
        !target.engine.prepareDelayBuffers(activeSampleRate))
    {
        error = L"Failed to allocate delay buffers for " + std::to_wstring(activeSampleRate) + L" Hz";
        return false;
    }

    target.lastStatus = target.client.statusText();
    return true;
}

int restartInsertAsioForFormatChangeLocked(NativeHost& target, int expectedChannelCount, std::wstring& message)
{
    if (!target.insertAsio.isRunning())
    {
        message = L"Insert ASIO host is not running.";
        return 0;
    }

    const int desiredSampleRate = desiredInsertAsioSampleRate(target);
    if (desiredSampleRate <= 0)
    {
        message = L"Insert ASIO format check skipped: VoiceMeeter has not reported a sample rate yet.";
        return 0;
    }

    const int currentSampleRate = target.insertAsio.currentSampleRate();
    const int desiredBlockSize = desiredInsertAsioBlockSize(target);
    const int currentBlockSize = target.insertAsio.currentBlockSize();
    const bool sampleRateChanged = currentSampleRate > 0 && currentSampleRate != desiredSampleRate;
    const bool blockSizeChanged = currentBlockSize > 0 && desiredBlockSize > 0 && currentBlockSize != desiredBlockSize;
    if (!sampleRateChanged && !blockSizeChanged)
    {
        message = L"Insert ASIO format is current.";
        return 0;
    }

    if (!target.engine.prepareDelayBuffers(desiredSampleRate))
    {
        message = L"Insert ASIO restart skipped: failed to allocate delay buffers for " +
            std::to_wstring(desiredSampleRate) + L" Hz.";
        return -1;
    }

    std::wstring stopped;
    target.insertAsio.stop(stopped);

    std::wstring started;
    const bool ok = target.insertAsio.start(desiredSampleRate, desiredBlockSize, expectedChannelCount, started);
    std::wostringstream details;
    details << L"Insert ASIO format changed from "
            << (currentSampleRate > 0 ? std::to_wstring(currentSampleRate) : L"?") << L" Hz / "
            << (currentBlockSize > 0 ? std::to_wstring(currentBlockSize) : L"?") << L" spl to "
            << desiredSampleRate << L" Hz / " << desiredBlockSize << L" spl. ";

    if (!ok)
    {
        details << L"Restart failed: " << started;
        message = details.str();
        return -1;
    }

    details << L"Restarted. " << started;
    message = details.str();
    return 1;
}

bool setModeLocked(NativeHost& target, CallbackMode mode, std::wstring& error)
{
    target.mode = mode;
    const bool wasRunning = target.client.state() == ConnectionState::Running;
    if (wasRunning)
        target.client.stop();

    if (target.client.state() == ConnectionState::Disconnected)
    {
        target.client.setPreferredMode(mode);
    }
    else if (!target.client.registerCallback(mode, error))
    {
        return false;
    }

    return startLocked(target, error);
}

void syncPluginNodeLocked(NativeHost& target, int slot)
{
    if (slot < 0 || slot >= PluginHostLayer::MaxPluginNodes)
        return;

    auto nodes = target.plugins.pluginNodes();
    const auto nodeIt = std::find_if(nodes.begin(), nodes.end(), [slot](const PluginNodeSummary& node) {
        return node.slot == slot;
    });

    if (nodeIt == nodes.end())
    {
        target.engine.clearPluginSlot(slot);
        return;
    }

    int inputRouteCount = 0;
    int outputRouteCount = 0;
    const auto inputRoutes = target.plugins.pluginNodeInputRoutes(slot, inputRouteCount);
    const auto outputRoutes = target.plugins.pluginNodeOutputRoutes(slot, outputRouteCount);
    target.engine.setPluginSlot(
        slot,
        target.plugins.realtimeProcessorForSlot(slot),
        static_cast<CallbackStreamKind>(nodeIt->kind),
        inputRoutes.data(),
        inputRouteCount,
        outputRoutes.data(),
        outputRouteCount,
        true,
        nodeIt->bypassed);
}

void syncAllPluginNodesLocked(NativeHost& target)
{
    const auto nodes = target.plugins.pluginNodes();
    for (const auto& node : nodes)
        syncPluginNodeLocked(target, node.slot);
}

void markGraphChannel(bool* active, int channel) noexcept
{
    constexpr int maxChannels = 64;
    if (channel < 0 || channel >= maxChannels)
        return;

    active[channel] = true;

    const int pairedChannel = (channel % 2 == 0) ? channel + 1 : channel - 1;
    if (pairedChannel >= 0 && pairedChannel < maxChannels)
        active[pairedChannel] = true;
}

void resyncPluginGraphGatesLocked(NativeHost& target)
{
    constexpr int maxChannels = 64;
    bool inputActive[maxChannels] {};
    bool outputActive[maxChannels] {};
    bool mainActive[maxChannels] {};

    const auto nodes = target.plugins.pluginNodes();
    for (const auto& node : nodes)
    {
        if (node.slot < 0)
            continue;

        bool* active = inputActive;
        if (node.kind == static_cast<int>(CallbackStreamKind::OutputInsert))
            active = outputActive;
        else if (node.kind == static_cast<int>(CallbackStreamKind::Main))
            active = mainActive;

        for (int route = 0; route < node.inputRouteCount; ++route)
        {
            const int pluginPin = node.inputRoutes[static_cast<size_t>(route)].to;
            if (pluginPin < 0 || pluginPin >= node.mainInputPins)
                continue;

            const int channel = node.inputRoutes[static_cast<size_t>(route)].from;
            markGraphChannel(active, channel);
        }

        for (int route = 0; route < node.outputRouteCount; ++route)
        {
            const int channel = node.outputRoutes[static_cast<size_t>(route)].to;
            markGraphChannel(active, channel);
        }
    }

    for (int channel = 0; channel < maxChannels; ++channel)
    {
        target.engine.setChannelPluginGraphEnabled(CallbackStreamKind::InputInsert, channel, inputActive[channel]);
        target.engine.setChannelPluginGraphEnabled(CallbackStreamKind::OutputInsert, channel, outputActive[channel]);
        target.engine.setChannelPluginGraphEnabled(CallbackStreamKind::Main, channel, mainActive[channel]);
    }
}

int scanPluginFoldersNative(
    const wchar_t* folders,
    int includeDefaults,
    int formatFlags,
    wchar_t* status,
    int statusChars)
{
    const int safeFormatFlags = normalizePluginScanFlags(formatFlags);
    std::lock_guard scanLock(g_scanMutex);
    setScanProgress(true, "Preparing plugin scan", "", 0, 0);

    NativeHost* target = nullptr;
    std::vector<std::string> paths;
    auto customFolders = splitPluginFolders(folders);

    {
        std::lock_guard lock(g_mutex);
        target = &host();
        if (includeDefaults != 0)
        {
            auto defaults = target->plugins.defaultPluginSearchPaths(safeFormatFlags);
            paths.insert(paths.end(), defaults.begin(), defaults.end());
        }
    }

    paths.insert(paths.end(), customFolders.begin(), customFolders.end());
    std::sort(paths.begin(), paths.end());
    paths.erase(std::unique(paths.begin(), paths.end()), paths.end());

    const int count = target->plugins.scanPluginPaths(
        paths,
        false,
        safeFormatFlags,
        [](const std::string& stage, const std::string& path, int current, int total) {
            setScanProgress(true, stage, path, current, total);
        });

    if (!target->plugins.lastError().empty())
    {
        const auto errorText = target->plugins.lastError();
        setScanProgress(false, "Plugin scan failed", errorText, 0, 0);
        writeWide(widenUtf8(errorText), status, statusChars);
        return -1;
    }

    std::wstring message = L"Plugin scan complete: " + std::to_wstring(count) + L" plugin(s)";
    if (!customFolders.empty())
        message += L" from standard locations + " + std::to_wstring(customFolders.size()) + L" folder(s)";

    message += L" | Formats:";
    if ((safeFormatFlags & ScanFormatVst3) != 0)
        message += L" VST3";
    if ((safeFormatFlags & ScanFormatVst2) != 0)
        message += L" VST2";

#if ELKA_ENABLE_VST2_HOST
    message += L" | VST2 enabled";
#else
    message += L" | VST2 disabled";
#endif

    const auto& report = target->plugins.lastScanReport();
    if (!report.empty())
    {
        message += L"\n" + widenUtf8(report);
    }

    writeWide(message, status, statusChars);
    setScanProgress(false, "Plugin scan complete", "Plugin scan complete: " + std::to_string(count) + " plugin(s)", count, count);
    return count;
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
    std::unique_lock scanLock(g_scanMutex, std::try_to_lock);
    std::lock_guard lock(g_mutex);
    if (g_host)
    {
        std::wstring ignored;
        restoreInsertPatchChannels(*g_host);
        g_host->insertAsio.stop(ignored);
        g_host->client.disconnect();
    }

    if (scanLock.owns_lock())
        g_host.reset();
}

__declspec(dllexport) int __cdecl ElkaFx_ProbeInsertAsio(int expectedChannelCount, wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    std::wstring message;
    const bool ok = target.insertAsio.probe(expectedChannelCount, message);
    writeWide(message, status, statusChars);
    return ok ? 0 : -1;
}

__declspec(dllexport) int __cdecl ElkaFx_StartInsertAsio(int expectedChannelCount, wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    std::wstring error;
    if (!target.client.connect(error))
    {
        writeWide(error.empty() ? L"Could not connect to VoiceMeeter Remote API." : error, status, statusChars);
        return -1;
    }

    const int requestedSampleRate = desiredInsertAsioSampleRate(target);
    const int requestedBlockSize = desiredInsertAsioBlockSize(target);
    if (requestedSampleRate > 0 && !target.engine.prepareDelayBuffers(requestedSampleRate))
    {
        writeWide(
            L"Insert ASIO start failed: failed to allocate delay buffers for " +
                std::to_wstring(requestedSampleRate) + L" Hz.",
            status,
            statusChars);
        return -1;
    }

    std::wstring message;
    const bool ok = target.insertAsio.start(requestedSampleRate, requestedBlockSize, expectedChannelCount, message);
    if (ok)
        message += L" | Patch.insert arming follows the selected input toggles.";

    writeWide(message, status, statusChars);
    return ok ? 0 : -1;
}

__declspec(dllexport) int __cdecl ElkaFx_RestartInsertAsioIfFormatChanged(int expectedChannelCount, wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();

    std::wstring error;
    if (!target.client.connect(error))
    {
        writeWide(error.empty() ? L"Could not connect to VoiceMeeter Remote API." : error, status, statusChars);
        return -1;
    }

    std::wstring message;
    const int result = restartInsertAsioForFormatChangeLocked(target, expectedChannelCount, message);
    writeWide(message, status, statusChars);
    return result;
}

__declspec(dllexport) void __cdecl ElkaFx_StopInsertAsio(wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    std::wstring message;
    const int restored = restoreInsertPatchChannels(target);
    target.insertAsio.stop(message);
    if (restored > 0)
    {
        message += L" Restored Patch.insert ";
        message += std::to_wstring(restored);
        message += L"/";
        message += std::to_wstring(MaxInsertPatchChannels);
        message += L".";
    }

    writeWide(message, status, statusChars);
}

__declspec(dllexport) void __cdecl ElkaFx_GetInsertAsioStatus(wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    std::wstring message;
    target.insertAsio.status(message);
    writeWide(message, status, statusChars);
}

__declspec(dllexport) int __cdecl ElkaFx_IsInsertAsioRunning()
{
    std::lock_guard lock(g_mutex);
    return g_host && g_host->insertAsio.isRunning() ? 1 : 0;
}

__declspec(dllexport) int __cdecl ElkaFx_SetPatchInsertEnabled(int inputChannel, int enabled)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();

    if (inputChannel < 0 || inputChannel >= MaxInsertPatchChannels)
        return -1;

    if (!savePatchInsertStateIfNeeded(target, inputChannel))
        return -1;

    return writePatchInsertState(target, inputChannel, enabled != 0 ? 1 : 0) ? 0 : -1;
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

__declspec(dllexport) int __cdecl ElkaFx_Start(wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    std::wstring error;
    const bool ok = startLocked(target, error);
    writeWide(ok ? target.client.statusText() : error, status, statusChars);
    return ok ? 0 : -1;
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
    const auto streamKind = streamKindFromApi(kind);
    target.engine.setChannelPluginGraphEnabled(streamKind, channel, enabled != 0);
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
    const auto realtimeStats = target.engine.getStats();
    stats->connectionState = static_cast<int>(target.client.state());
    stats->mode = apiModeFromCallbackMode(target.mode);
    stats->sampleRate = realtimeStats.sampleRate;
    stats->blockSize = realtimeStats.blockSize;
    stats->inputChannels = realtimeStats.inputChannels;
    stats->outputChannels = realtimeStats.outputChannels;
    stats->callbackCount = realtimeStats.callbackCount;
    stats->lastProcessUsec = realtimeStats.lastProcessUsec;
    stats->peakProcessUsec = realtimeStats.peakProcessUsec;
    stats->callbackCpuPercent = realtimeStats.callbackCpuPercent;
    stats->delayBufferSampleRate = realtimeStats.delayBufferSampleRate;
    stats->probeInputChannel = realtimeStats.probeInputChannel;
    stats->probeOutputChannel = realtimeStats.probeOutputChannel;
    stats->inputInsertReadPeakPercent = realtimeStats.inputInsertReadPeakPercent;
    stats->inputInsertWritePeakPercent = realtimeStats.inputInsertWritePeakPercent;
    stats->mainInputReadPeakPercent = realtimeStats.mainInputReadPeakPercent;
    stats->mainOutputReadPeakPercent = realtimeStats.mainOutputReadPeakPercent;
    stats->mainWritePeakPercent = realtimeStats.mainWritePeakPercent;
    stats->outputInsertReadPeakPercent = realtimeStats.outputInsertReadPeakPercent;
    stats->outputInsertWritePeakPercent = realtimeStats.outputInsertWritePeakPercent;
    stats->inputInsertMaxReadPeakPercent = realtimeStats.inputInsertMaxReadPeakPercent;
    stats->inputInsertMaxReadChannel = realtimeStats.inputInsertMaxReadChannel;
    stats->inputInsertMaxWritePeakPercent = realtimeStats.inputInsertMaxWritePeakPercent;
    stats->inputInsertMaxWriteChannel = realtimeStats.inputInsertMaxWriteChannel;
    stats->mainSourceMaxReadPeakPercent = realtimeStats.mainSourceMaxReadPeakPercent;
    stats->mainSourceMaxReadChannel = realtimeStats.mainSourceMaxReadChannel;
    stats->mainBusMaxReadPeakPercent = realtimeStats.mainBusMaxReadPeakPercent;
    stats->mainBusMaxReadChannel = realtimeStats.mainBusMaxReadChannel;
    stats->mainMaxWritePeakPercent = realtimeStats.mainMaxWritePeakPercent;
    stats->mainMaxWriteChannel = realtimeStats.mainMaxWriteChannel;
    stats->outputInsertMaxReadPeakPercent = realtimeStats.outputInsertMaxReadPeakPercent;
    stats->outputInsertMaxReadChannel = realtimeStats.outputInsertMaxReadChannel;
    stats->outputInsertMaxWritePeakPercent = realtimeStats.outputInsertMaxWritePeakPercent;
    stats->outputInsertMaxWriteChannel = realtimeStats.outputInsertMaxWriteChannel;
    stats->inputInsertInputChannels = realtimeStats.inputInsertInputChannels;
    stats->inputInsertOutputChannels = realtimeStats.inputInsertOutputChannels;
    stats->mainInputChannels = realtimeStats.mainInputChannels;
    stats->mainOutputChannels = realtimeStats.mainOutputChannels;
    stats->outputInsertInputChannels = realtimeStats.outputInsertInputChannels;
    stats->outputInsertOutputChannels = realtimeStats.outputInsertOutputChannels;

    const auto readLevelPercent = [&target](int type, int channel) noexcept {
        float value = 0.0f;
        if (!target.client.getLevel(type, channel, value))
            return -1;

        return std::clamp(static_cast<int>((std::max(0.0f, value) * 100.0f) + 0.5f), 0, 1000);
    };

    stats->voicemeeterPreFaderLevelPercent = readLevelPercent(0, realtimeStats.probeInputChannel);
    stats->voicemeeterPostFaderLevelPercent = readLevelPercent(1, realtimeStats.probeInputChannel);
    stats->voicemeeterPostMuteLevelPercent = readLevelPercent(2, realtimeStats.probeInputChannel);
    stats->voicemeeterNextPreFaderLevelPercent = readLevelPercent(0, realtimeStats.probeInputChannel + 1);
    stats->voicemeeterNextPostFaderLevelPercent = readLevelPercent(1, realtimeStats.probeInputChannel + 1);
    stats->voicemeeterNextPostMuteLevelPercent = readLevelPercent(2, realtimeStats.probeInputChannel + 1);
    stats->voicemeeterInputMaxLevelPercent = -1;
    stats->voicemeeterInputMaxChannel = -1;
    for (int channel = 0; channel < 64; ++channel)
    {
        const int pre = readLevelPercent(0, channel);
        const int post = readLevelPercent(1, channel);
        const int mute = readLevelPercent(2, channel);
        const int level = std::max(pre, std::max(post, mute));
        if (level > stats->voicemeeterInputMaxLevelPercent)
        {
            stats->voicemeeterInputMaxLevelPercent = level;
            stats->voicemeeterInputMaxChannel = channel;
        }
    }

    stats->voicemeeterOutputLevelPercent = readLevelPercent(3, realtimeStats.probeOutputChannel);
}

__declspec(dllexport) int __cdecl ElkaFx_GetPatchAsioChannel(int inputChannel)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    return readIndexedParameter(target, "patch.asio[%d]", inputChannel);
}

__declspec(dllexport) int __cdecl ElkaFx_RefreshVoicemeeterParameters()
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    return target.client.refreshParameters() ? 0 : -1;
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

__declspec(dllexport) int __cdecl ElkaFx_StartSinglePing(
    int inputChannel,
    int outputChannel,
    int timeoutMilliseconds,
    wchar_t* status,
    int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();

    const int requiredMode =
        static_cast<int>(target.mode) |
        static_cast<int>(CallbackMode::InputInsert) |
        static_cast<int>(CallbackMode::OutputInsert);
    if (target.client.state() != ConnectionState::Running ||
        static_cast<int>(target.mode) != requiredMode)
    {
        std::wstring error;
        if (!setModeLocked(target, static_cast<CallbackMode>(requiredMode), error))
        {
            writeWide(error.empty() ? L"Could not start input/output callback mode for ping." : error, status, statusChars);
            return -1;
        }
    }

    if (!target.engine.startSinglePing(inputChannel, outputChannel, timeoutMilliseconds))
    {
        writeWide(L"Invalid ping channel selection.", status, statusChars);
        return -1;
    }

    writeWide(
        L"Ping armed: channel " + std::to_wstring(inputChannel + 1) +
            L" -> channel " + std::to_wstring(outputChannel + 1) + L".",
        status,
        statusChars);
    return 0;
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
        : 0.0;
    result->elapsedMilliseconds = ping.sampleRate > 0
        ? (static_cast<double>(ping.elapsedSamples) * 1000.0) / static_cast<double>(ping.sampleRate)
        : 0.0;
}

__declspec(dllexport) int __cdecl ElkaFx_ListWasapiRenderDevices(wchar_t* buffer, int bufferChars)
{
    return listWasapiDevices(eRender, buffer, bufferChars);
}

__declspec(dllexport) int __cdecl ElkaFx_ListWasapiCaptureDevices(wchar_t* buffer, int bufferChars)
{
    return listWasapiDevices(eCapture, buffer, bufferChars);
}

__declspec(dllexport) int __cdecl ElkaFx_RunExternalWasapiPing(
    const wchar_t* renderDeviceId,
    const wchar_t* captureDeviceId,
    int timeoutMilliseconds,
    NativeExternalWasapiPingResult* result,
    wchar_t* status,
    int statusChars)
{
    return runExternalWasapiPing(renderDeviceId, captureDeviceId, timeoutMilliseconds, result, status, statusChars);
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginCount()
{
    std::lock_guard lock(g_mutex);
    return static_cast<int>(host().plugins.plugins().size());
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginName(int index, wchar_t* buffer, int bufferChars)
{
    std::lock_guard lock(g_mutex);
    const auto& plugins = host().plugins.plugins();
    if (index < 0 || index >= static_cast<int>(plugins.size()))
    {
        writeWide(L"", buffer, bufferChars);
        return -1;
    }

    const auto& plugin = plugins[static_cast<size_t>(index)];
    std::wstring label = widenUtf8(plugin.name);
    if (!plugin.manufacturer.empty())
        label += L" - " + widenUtf8(plugin.manufacturer);
    if (!plugin.format.empty())
        label += L" [" + widenUtf8(plugin.format) + L"]";

    writeWide(label, buffer, bufferChars);
    return 0;
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginFormat(int index, wchar_t* buffer, int bufferChars)
{
    std::lock_guard lock(g_mutex);
    const auto& plugins = host().plugins.plugins();
    if (index < 0 || index >= static_cast<int>(plugins.size()))
    {
        writeWide(L"", buffer, bufferChars);
        return -1;
    }

    writeWide(widenUtf8(plugins[static_cast<size_t>(index)].format), buffer, bufferChars);
    return 0;
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginIdentifier(int index, wchar_t* buffer, int bufferChars)
{
    std::lock_guard lock(g_mutex);
    const auto& plugins = host().plugins.plugins();
    if (index < 0 || index >= static_cast<int>(plugins.size()))
    {
        writeWide(L"", buffer, bufferChars);
        return -1;
    }

    writeWide(widenUtf8(plugins[static_cast<size_t>(index)].fileOrIdentifier), buffer, bufferChars);
    return 0;
}

__declspec(dllexport) int __cdecl ElkaFx_ScanDefaultVst3(wchar_t* status, int statusChars)
{
    return scanPluginFoldersNative(L"", 1, ScanFormatVst3, status, statusChars);
}

__declspec(dllexport) int __cdecl ElkaFx_ScanPluginFolders(
    const wchar_t* folders,
    int includeDefaults,
    wchar_t* status,
    int statusChars)
{
    return scanPluginFoldersNative(folders, includeDefaults, ScanFormatAll, status, statusChars);
}

__declspec(dllexport) int __cdecl ElkaFx_ScanPluginFoldersEx(
    const wchar_t* folders,
    int includeDefaults,
    int formatFlags,
    wchar_t* status,
    int statusChars)
{
    return scanPluginFoldersNative(folders, includeDefaults, formatFlags, status, statusChars);
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginScanProgress(wchar_t* buffer, int bufferChars)
{
    const auto snapshot = scanProgressSnapshot();
    writeWide(snapshot, buffer, bufferChars);

    std::lock_guard lock(g_scanProgressMutex);
    return g_scanProgress.running ? 1 : 0;
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginLoadProgress(wchar_t* buffer, int bufferChars)
{
    const auto snapshot = pluginLoadProgressSnapshot();
    writeWide(snapshot, buffer, bufferChars);

    std::lock_guard lock(g_pluginLoadProgressMutex);
    return g_pluginLoadProgress.running ? 1 : 0;
}

__declspec(dllexport) int __cdecl ElkaFx_ProbePluginFile(
    const wchar_t* format,
    const wchar_t* fileOrIdentifier,
    int sampleRate,
    int blockSize,
    wchar_t* status,
    int statusChars)
{
    const auto formatText = narrowWide(format);
    const auto identifierText = narrowWide(fileOrIdentifier);
    std::string error;
    setPluginLoadProgress(true, "Probe starting", identifierText);

    const bool ok = probePluginFile(
        formatText,
        identifierText,
        sampleRate > 0 ? sampleRate : 48000,
        blockSize > 0 ? blockSize : 512,
        error,
        [](const std::string& stage, const std::string& detail) {
            setPluginLoadProgress(true, stage, detail);
        });

    if (ok)
    {
        setPluginLoadProgress(false, "Probe complete", identifierText);
        writeWide(L"Plugin probe succeeded.", status, statusChars);
        return 0;
    }

    setPluginLoadProgress(false, "Probe failed", error);
    writeWide(L"Plugin probe failed: " + widenUtf8(error), status, statusChars);
    return -1;
}

__declspec(dllexport) int __cdecl ElkaFx_WorkerCreatePlugin(
    const wchar_t* format,
    const wchar_t* fileOrIdentifier,
    int sampleRate,
    int blockSize,
    int inputPins,
    int outputPins,
    wchar_t* status,
    int statusChars)
{
    std::string error;
    const int handle = createWorkerPluginProcessor(
        narrowWide(format),
        narrowWide(fileOrIdentifier),
        sampleRate,
        blockSize,
        inputPins,
        outputPins,
        error);

    if (handle > 0)
    {
        writeWide(L"Worker plugin loaded.", status, statusChars);
        return handle;
    }

    writeWide(L"Worker plugin load failed: " + widenUtf8(error), status, statusChars);
    return 0;
}

__declspec(dllexport) int __cdecl ElkaFx_WorkerProcessPlugin(
    int handle,
    float* planarData,
    int channelCount,
    int samples)
{
    return processWorkerPluginProcessor(handle, planarData, channelCount, samples) ? 0 : -1;
}

__declspec(dllexport) int __cdecl ElkaFx_WorkerOpenPluginEditor(
    int handle,
    wchar_t* status,
    int statusChars)
{
    std::string error;
    if (openWorkerPluginEditor(handle, error))
    {
        writeWide(L"Worker plugin editor opened.", status, statusChars);
        return 0;
    }

    writeWide(L"Worker plugin editor failed: " + widenUtf8(error), status, statusChars);
    return -1;
}

__declspec(dllexport) void __cdecl ElkaFx_WorkerPollMessages(int milliseconds)
{
    pollWorkerPluginMessages(milliseconds);
}

__declspec(dllexport) void __cdecl ElkaFx_WorkerDestroyPlugin(int handle)
{
    destroyWorkerPluginProcessor(handle);
}

int addPluginNodeNative(
    int pluginIndex,
    int mode,
    int mainInputPins,
    int sidechainInputPins,
    int outputPins,
    int x,
    int y,
    const wchar_t* initialStateBase64,
    const wchar_t* initialPresetBase64,
    int* slotOut,
    int* inputPinsOut,
    int* outputPinsOut,
    wchar_t* status,
    int statusChars,
    bool sandboxed = false)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    setPluginLoadProgress(true, sandboxed ? "Starting sandboxed plugin node load" : "Starting plugin node load", "index " + std::to_string(pluginIndex));

    const auto streamKind = streamKindFromApi(mode);
    const int safeMainInputPins = std::clamp(mainInputPins, 1, 8);
    const int safeSidechainInputPins = std::clamp(sidechainInputPins, 0, RealtimeEngine::MaxPluginPins - safeMainInputPins);
    const int safeInputPins = std::clamp(safeMainInputPins + safeSidechainInputPins, 1, RealtimeEngine::MaxPluginPins);
    const int safeOutputPins = std::clamp(outputPins, 1, 8);
    const int layoutChannels = std::max(safeMainInputPins, safeOutputPins);
    const int layoutId = layoutChannels == 1 ? 0 : layoutChannels == 2 ? 1 : layoutChannels;
    const std::string layoutName =
        layoutChannels == 1 ? "Mono" : layoutChannels == 2 ? "Stereo" : std::to_string(layoutChannels) + " channel";

    int sampleRate = desiredInsertAsioSampleRate(target);
    if (sampleRate <= 0)
    {
        std::wstring startError;
        startLocked(target, startError);
        sampleRate = desiredInsertAsioSampleRate(target);
    }

    if (sampleRate <= 0)
    {
        setPluginLoadProgress(false, "Plugin node load failed", "VoiceMeeter has not reported a sample rate yet.");
        writeWide(L"Plugin node add failed: VoiceMeeter has not reported a sample rate yet.", status, statusChars);
        return -1;
    }

    const auto stats = target.engine.getStats();
    const int maxBlockSize = std::max(4096, stats.blockSize > 0 ? stats.blockSize : 512);

    const int slot = sandboxed
        ? target.plugins.addSandboxedDiscoveredPluginNode(
            static_cast<size_t>(std::max(0, pluginIndex)),
            sampleRate,
            maxBlockSize,
            safeMainInputPins,
            safeSidechainInputPins,
            safeOutputPins,
            layoutId,
            layoutName,
            static_cast<int>(streamKind),
            0,
            layoutChannels,
            [](const std::string& stage, const std::string& detail) {
                setPluginLoadProgress(true, stage, detail);
            })
        : target.plugins.addDiscoveredPluginNode(
            static_cast<size_t>(std::max(0, pluginIndex)),
            sampleRate,
            maxBlockSize,
            safeMainInputPins,
            safeSidechainInputPins,
            safeOutputPins,
            layoutId,
            layoutName,
            static_cast<int>(streamKind),
            0,
            layoutChannels,
            narrowWide(initialStateBase64),
            narrowWide(initialPresetBase64),
            [](const std::string& stage, const std::string& detail) {
                setPluginLoadProgress(true, stage, detail);
            });

    const auto pluginRestoreWarning = target.plugins.lastError();

    if (slot < 0)
    {
        setPluginLoadProgress(false, "Plugin node load failed", target.plugins.lastError());
        writeWide(L"Plugin node add failed: " + widenUtf8(target.plugins.lastError()), status, statusChars);
        return -1;
    }

    target.plugins.setPluginNodePosition(slot, x, y);

    syncPluginNodeLocked(target, slot);

    if (slotOut != nullptr)
        *slotOut = slot;
    int actualInputPins = safeInputPins;
    int actualOutputPins = safeOutputPins;
    const auto nodes = target.plugins.pluginNodes();
    const auto nodeIt = std::find_if(nodes.begin(), nodes.end(), [slot](const PluginNodeSummary& node) {
        return node.slot == slot;
    });
    if (nodeIt != nodes.end())
    {
        actualInputPins = nodeIt->inputPins;
        actualOutputPins = nodeIt->outputPins;
    }

    if (inputPinsOut != nullptr)
        *inputPinsOut = actualInputPins;
    if (outputPinsOut != nullptr)
        *outputPinsOut = actualOutputPins;

    std::wstring error;
    startLocked(target, error);
    auto message = error.empty()
        ? (sandboxed ? L"Sandboxed VST node loaded as free module" : L"VST node loaded as free module")
        : error;
    if (!sandboxed && !pluginRestoreWarning.empty())
        message += L" | Saved state warning: " + widenUtf8(pluginRestoreWarning);
    writeWide(message, status, statusChars);
    setPluginLoadProgress(false, sandboxed ? "Sandboxed plugin node loaded" : "Plugin node loaded", "slot " + std::to_string(slot));
    return 0;
}

__declspec(dllexport) int __cdecl ElkaFx_AddPluginNode(
    int pluginIndex,
    int mode,
    int mainInputPins,
    int sidechainInputPins,
    int outputPins,
    int x,
    int y,
    int* slotOut,
    int* inputPinsOut,
    int* outputPinsOut,
    wchar_t* status,
    int statusChars)
{
    return addPluginNodeNative(
        pluginIndex,
        mode,
        mainInputPins,
        sidechainInputPins,
        outputPins,
        x,
        y,
        nullptr,
        nullptr,
        slotOut,
        inputPinsOut,
        outputPinsOut,
        status,
        statusChars);
}

__declspec(dllexport) int __cdecl ElkaFx_AddSandboxedPluginNode(
    int pluginIndex,
    int mode,
    int mainInputPins,
    int sidechainInputPins,
    int outputPins,
    int x,
    int y,
    int* slotOut,
    int* inputPinsOut,
    int* outputPinsOut,
    wchar_t* status,
    int statusChars)
{
    return addPluginNodeNative(
        pluginIndex,
        mode,
        mainInputPins,
        sidechainInputPins,
        outputPins,
        x,
        y,
        nullptr,
        nullptr,
        slotOut,
        inputPinsOut,
        outputPinsOut,
        status,
        statusChars,
        true);
}

__declspec(dllexport) int __cdecl ElkaFx_AddPluginNodeWithState(
    int pluginIndex,
    int mode,
    int mainInputPins,
    int sidechainInputPins,
    int outputPins,
    int x,
    int y,
    const wchar_t* initialStateBase64,
    const wchar_t* initialPresetBase64,
    int* slotOut,
    int* inputPinsOut,
    int* outputPinsOut,
    wchar_t* status,
    int statusChars)
{
    return addPluginNodeNative(
        pluginIndex,
        mode,
        mainInputPins,
        sidechainInputPins,
        outputPins,
        x,
        y,
        initialStateBase64,
        initialPresetBase64,
        slotOut,
        inputPinsOut,
        outputPinsOut,
        status,
        statusChars);
}

__declspec(dllexport) int __cdecl ElkaFx_SetPluginNodeBypassed(int slot, int bypassed)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    target.plugins.setPluginNodeBypassed(slot, bypassed != 0);
    syncPluginNodeLocked(target, slot);
    return 0;
}

__declspec(dllexport) int __cdecl ElkaFx_TogglePluginNodeInputRoute(int slot, int sourceChannel, int pluginPin)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    const bool active = target.plugins.togglePluginNodeInputRoute(slot, sourceChannel, pluginPin);
    syncPluginNodeLocked(target, slot);
    resyncPluginGraphGatesLocked(target);
    return active ? 1 : 0;
}

__declspec(dllexport) int __cdecl ElkaFx_TogglePluginNodeOutputRoute(int slot, int pluginPin, int destinationChannel)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    const bool active = target.plugins.togglePluginNodeOutputRoute(slot, pluginPin, destinationChannel);
    syncPluginNodeLocked(target, slot);
    resyncPluginGraphGatesLocked(target);
    return active ? 1 : 0;
}

__declspec(dllexport) int __cdecl ElkaFx_TogglePluginNodeModuleRoute(
    int sourceSlot,
    int sourcePin,
    int destinationSlot,
    int destinationPin)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    const bool active = target.plugins.togglePluginNodeModuleRoute(sourceSlot, sourcePin, destinationSlot, destinationPin);
    syncPluginNodeLocked(target, sourceSlot);
    syncPluginNodeLocked(target, destinationSlot);
    resyncPluginGraphGatesLocked(target);
    return active ? 1 : 0;
}

__declspec(dllexport) int __cdecl ElkaFx_OpenPluginEditor(int slot, wchar_t* status, int statusChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    if (!target.plugins.openPluginEditor(slot))
    {
        writeWide(L"Plugin editor failed: " + widenUtf8(target.plugins.lastError()), status, statusChars);
        return -1;
    }

    writeWide(L"Plugin editor opened", status, statusChars);
    return 0;
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginNodeStateLength(int slot)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    const auto state = target.plugins.pluginNodeStateBase64(slot);
    return state.empty() ? 1 : static_cast<int>(state.size()) + 1;
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginNodeState(int slot, wchar_t* buffer, int bufferChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    const auto state = target.plugins.pluginNodeStateBase64(slot);
    writeWide(widenUtf8(state), buffer, bufferChars);
    return state.empty() && !target.plugins.lastError().empty() ? -1 : 0;
}

__declspec(dllexport) int __cdecl ElkaFx_SetPluginNodeState(int slot, const wchar_t* stateBase64)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    return target.plugins.setPluginNodeStateBase64(slot, narrowWide(stateBase64)) ? 0 : -1;
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginNodePresetLength(int slot)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    const auto preset = target.plugins.pluginNodePresetBase64(slot);
    return preset.empty() ? 1 : static_cast<int>(preset.size()) + 1;
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginNodePreset(int slot, wchar_t* buffer, int bufferChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    const auto preset = target.plugins.pluginNodePresetBase64(slot);
    writeWide(widenUtf8(preset), buffer, bufferChars);
    return preset.empty() && !target.plugins.lastError().empty() ? -1 : 0;
}

__declspec(dllexport) int __cdecl ElkaFx_SetPluginNodePreset(int slot, const wchar_t* presetBase64)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    return target.plugins.setPluginNodePresetBase64(slot, narrowWide(presetBase64)) ? 0 : -1;
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginNodeParameterStateLength(int slot)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    const auto parameterState = target.plugins.pluginNodeParameterStateBase64(slot);
    return parameterState.empty() ? 1 : static_cast<int>(parameterState.size()) + 1;
}

__declspec(dllexport) int __cdecl ElkaFx_GetPluginNodeParameterState(int slot, wchar_t* buffer, int bufferChars)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    const auto parameterState = target.plugins.pluginNodeParameterStateBase64(slot);
    writeWide(widenUtf8(parameterState), buffer, bufferChars);
    return parameterState.empty() && !target.plugins.lastError().empty() ? -1 : 0;
}

__declspec(dllexport) int __cdecl ElkaFx_SetPluginNodeParameterState(int slot, const wchar_t* parameterStateBase64)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    return target.plugins.setPluginNodeParameterStateBase64(slot, narrowWide(parameterStateBase64)) ? 0 : -1;
}

__declspec(dllexport) int __cdecl ElkaFx_RemovePluginNode(int slot)
{
    std::lock_guard lock(g_mutex);
    auto& target = host();
    target.engine.clearPluginSlot(slot);
    target.plugins.removePluginNode(slot);
    syncAllPluginNodesLocked(target);
    resyncPluginGraphGatesLocked(target);
    return 0;
}
}
