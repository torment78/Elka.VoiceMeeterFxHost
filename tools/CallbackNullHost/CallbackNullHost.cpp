#include <windows.h>
#include <avrt.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <iostream>
#include <string>
#include <string_view>

namespace
{
constexpr long CommandStarting = 1;
constexpr long CommandEnding = 2;
constexpr long CommandChange = 3;
constexpr long CommandBufferIn = 10;
constexpr long CommandBufferOut = 11;
constexpr long CommandBufferMain = 20;

constexpr long ModeInputInsert = 0x00000001;
constexpr long ModeOutputInsert = 0x00000002;
constexpr long ModeMain = 0x00000004;

struct AudioInfo
{
    long sampleRate;
    long samplesPerFrame;
};

struct AudioBuffer
{
    long sampleRate;
    long samplesPerFrame;
    long inputChannels;
    long outputChannels;
    float* read[128];
    float* write[128];
};

using AudioCallback = long(__stdcall*)(void* user, long command, void* data, long reserved);
using LoginFn = long(__stdcall*)();
using LogoutFn = long(__stdcall*)();
using AudioCallbackRegisterFn = long(__stdcall*)(long, AudioCallback, void*, char[64]);
using AudioCallbackStartFn = long(__stdcall*)();
using AudioCallbackStopFn = long(__stdcall*)();
using AudioCallbackUnregisterFn = long(__stdcall*)();

enum class BufferAction
{
    Passthrough,
    Zero,
    TouchOnly
};

struct CallbackCounters
{
    std::atomic<uint64_t> total { 0 };
    std::atomic<uint64_t> starting { 0 };
    std::atomic<uint64_t> ending { 0 };
    std::atomic<uint64_t> change { 0 };
    std::atomic<uint64_t> inBuffers { 0 };
    std::atomic<uint64_t> outBuffers { 0 };
    std::atomic<uint64_t> mainBuffers { 0 };
    std::atomic<uint64_t> maxCallbackUs { 0 };
    std::atomic<uint64_t> minIntervalUs { std::numeric_limits<uint64_t>::max() };
    std::atomic<uint64_t> maxIntervalUs { 0 };
    std::atomic<uint64_t> late25 { 0 };
    std::atomic<uint64_t> late50 { 0 };
    std::atomic<uint64_t> late100 { 0 };
    std::atomic<uint64_t> early50 { 0 };
    std::atomic<uint64_t> samePointerChannels { 0 };
    std::atomic<uint64_t> overlapChannels { 0 };
    std::atomic<uint64_t> nullReadChannels { 0 };
    std::atomic<uint64_t> nullWriteChannels { 0 };
    std::atomic<uint64_t> otherThreadCalls { 0 };
    std::atomic<uint64_t> threadSwitches { 0 };
    std::atomic<DWORD> primaryThreadId { 0 };
    std::atomic<DWORD> lastThreadId { 0 };
    std::atomic<long> lastCommand { 0 };
    std::atomic<long> sampleRate { 0 };
    std::atomic<long> samplesPerFrame { 0 };
    std::atomic<long> inputChannels { 0 };
    std::atomic<long> outputChannels { 0 };
};

struct CallbackState
{
    CallbackCounters counters;
    BufferAction action = BufferAction::Passthrough;
    bool measureCallbackTime = false;
    bool useMmcss = false;
};

std::atomic<bool> g_shouldStop { false };

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

std::wstring directoryOf(std::wstring path)
{
    if (path.size() >= 2 && path.front() == L'"' && path.back() == L'"')
        path = path.substr(1, path.size() - 2);

    const auto slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring {} : path.substr(0, slash);
}

bool readInstallPathFromRegistry(std::wstring& installPath)
{
    constexpr wchar_t uninstallKey[] =
        L"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\VB:Voicemeeter {17359A74-1236-5467}";

    HKEY key = nullptr;
    const LSTATUS openStatus = RegOpenKeyExW(HKEY_LOCAL_MACHINE, uninstallKey, 0, KEY_READ, &key);
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

bool findRemoteDll(std::wstring& path, std::wstring& error)
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

template <typename Fn>
bool loadFunction(HMODULE module, Fn& out, const char* name, std::wstring& error)
{
    out = reinterpret_cast<Fn>(GetProcAddress(module, name));
    if (out != nullptr)
        return true;

    error = L"Missing VoiceMeeter Remote API export: ";
    error += std::wstring(name, name + std::strlen(name));
    return false;
}

class RemoteApi
{
public:
    ~RemoteApi()
    {
        unload();
    }

    bool load(std::wstring dllOverride, std::wstring& error)
    {
        if (!dllOverride.empty())
        {
            dllPath_ = std::move(dllOverride);
        }
        else if (!findRemoteDll(dllPath_, error))
        {
            return false;
        }

        module_ = LoadLibraryW(dllPath_.c_str());
        if (module_ == nullptr)
        {
            error = L"Failed to load VoiceMeeter Remote DLL: " + dllPath_ + L"\n" + systemErrorMessage(GetLastError());
            return false;
        }

        return loadFunction(module_, login, "VBVMR_Login", error) &&
            loadFunction(module_, logout, "VBVMR_Logout", error) &&
            loadFunction(module_, callbackRegister, "VBVMR_AudioCallbackRegister", error) &&
            loadFunction(module_, callbackStart, "VBVMR_AudioCallbackStart", error) &&
            loadFunction(module_, callbackStop, "VBVMR_AudioCallbackStop", error) &&
            loadFunction(module_, callbackUnregister, "VBVMR_AudioCallbackUnregister", error);
    }

    void unload() noexcept
    {
        if (module_ != nullptr)
        {
            FreeLibrary(module_);
            module_ = nullptr;
        }
    }

    const std::wstring& dllPath() const noexcept
    {
        return dllPath_;
    }

    LoginFn login = nullptr;
    LogoutFn logout = nullptr;
    AudioCallbackRegisterFn callbackRegister = nullptr;
    AudioCallbackStartFn callbackStart = nullptr;
    AudioCallbackStopFn callbackStop = nullptr;
    AudioCallbackUnregisterFn callbackUnregister = nullptr;

private:
    HMODULE module_ = nullptr;
    std::wstring dllPath_;
};

void zeroWrites(const AudioBuffer& buffer)
{
    const int samples = std::max(0L, buffer.samplesPerFrame);
    const int channels = std::max(0L, std::min(buffer.outputChannels, 128L));
    for (int channel = 0; channel < channels; ++channel)
    {
        if (buffer.write[channel] != nullptr)
            std::fill_n(buffer.write[channel], samples, 0.0f);
    }
}

bool rangesOverlap(const float* input, const float* output, size_t bytes) noexcept
{
    const auto inBegin = reinterpret_cast<uintptr_t>(input);
    const auto outBegin = reinterpret_cast<uintptr_t>(output);
    const auto inEnd = inBegin + bytes;
    const auto outEnd = outBegin + bytes;
    return inBegin < outEnd && outBegin < inEnd;
}

void inspectBufferPointers(const AudioBuffer& buffer, CallbackCounters& counters)
{
    const int samples = std::max(0L, buffer.samplesPerFrame);
    const size_t bytes = static_cast<size_t>(samples) * sizeof(float);
    const int channels = std::max(0L, std::min(std::max(buffer.inputChannels, buffer.outputChannels), 128L));

    for (int channel = 0; channel < channels; ++channel)
    {
        const float* input = channel < buffer.inputChannels ? buffer.read[channel] : nullptr;
        float* output = channel < buffer.outputChannels ? buffer.write[channel] : nullptr;

        if (input == nullptr)
            counters.nullReadChannels.fetch_add(1, std::memory_order_relaxed);
        if (output == nullptr)
            counters.nullWriteChannels.fetch_add(1, std::memory_order_relaxed);

        if (input == nullptr || output == nullptr)
            continue;

        if (input == output)
            counters.samePointerChannels.fetch_add(1, std::memory_order_relaxed);
        else if (rangesOverlap(input, output, bytes))
            counters.overlapChannels.fetch_add(1, std::memory_order_relaxed);
    }
}

void passthrough(const AudioBuffer& buffer)
{
    const int samples = std::max(0L, buffer.samplesPerFrame);
    const int channels = std::max(0L, std::min(std::min(buffer.inputChannels, buffer.outputChannels), 128L));
    const size_t bytes = static_cast<size_t>(samples) * sizeof(float);
    for (int channel = 0; channel < channels; ++channel)
    {
        float* output = buffer.write[channel];
        const float* input = buffer.read[channel];
        if (output == nullptr)
            continue;

        if (input == nullptr)
        {
            std::fill_n(output, samples, 0.0f);
            continue;
        }

        if (input == output)
            continue;

        if (rangesOverlap(input, output, bytes))
            std::memmove(output, input, bytes);
        else
            std::memcpy(output, input, bytes);
    }

    const int outputChannels = std::max(0L, std::min(buffer.outputChannels, 128L));
    for (int channel = channels; channel < outputChannels; ++channel)
    {
        if (buffer.write[channel] != nullptr)
            std::fill_n(buffer.write[channel], samples, 0.0f);
    }
}

void updateMax(std::atomic<uint64_t>& target, uint64_t value)
{
    uint64_t current = target.load(std::memory_order_relaxed);
    while (value > current && !target.compare_exchange_weak(current, value, std::memory_order_relaxed))
    {
    }
}

void updateMin(std::atomic<uint64_t>& target, uint64_t value)
{
    uint64_t current = target.load(std::memory_order_relaxed);
    while (value < current && !target.compare_exchange_weak(current, value, std::memory_order_relaxed))
    {
    }
}

void trackBufferCadence(CallbackState& state, const AudioBuffer& buffer, const LARGE_INTEGER& now, const LARGE_INTEGER& frequency)
{
    if (!state.measureCallbackTime || now.QuadPart == 0 || frequency.QuadPart <= 0)
        return;

    thread_local LONGLONG previousTicks = 0;
    thread_local long previousSampleRate = 0;
    thread_local long previousSamplesPerFrame = 0;

    const long sampleRate = buffer.sampleRate;
    const long samplesPerFrame = buffer.samplesPerFrame;
    if (sampleRate != previousSampleRate || samplesPerFrame != previousSamplesPerFrame)
    {
        previousTicks = now.QuadPart;
        previousSampleRate = sampleRate;
        previousSamplesPerFrame = samplesPerFrame;
        return;
    }

    if (previousTicks == 0 || sampleRate <= 0 || samplesPerFrame <= 0)
    {
        previousTicks = now.QuadPart;
        return;
    }

    const auto elapsedTicks = static_cast<uint64_t>(now.QuadPart - previousTicks);
    previousTicks = now.QuadPart;

    const auto intervalUs = (elapsedTicks * 1000000ULL) / static_cast<uint64_t>(frequency.QuadPart);
    const auto expectedUs = (static_cast<uint64_t>(samplesPerFrame) * 1000000ULL) / static_cast<uint64_t>(sampleRate);
    updateMin(state.counters.minIntervalUs, intervalUs);
    updateMax(state.counters.maxIntervalUs, intervalUs);

    if (expectedUs == 0)
        return;

    if (intervalUs * 4ULL > expectedUs * 5ULL)
        state.counters.late25.fetch_add(1, std::memory_order_relaxed);
    if (intervalUs * 2ULL > expectedUs * 3ULL)
        state.counters.late50.fetch_add(1, std::memory_order_relaxed);
    if (intervalUs > expectedUs * 2ULL)
        state.counters.late100.fetch_add(1, std::memory_order_relaxed);
    if (intervalUs * 2ULL < expectedUs)
        state.counters.early50.fetch_add(1, std::memory_order_relaxed);
}

void trackCallbackThread(CallbackCounters& counters)
{
    const DWORD current = GetCurrentThreadId();
    DWORD expected = 0;
    counters.primaryThreadId.compare_exchange_strong(expected, current, std::memory_order_relaxed);

    const DWORD primary = counters.primaryThreadId.load(std::memory_order_relaxed);
    if (primary != 0 && current != primary)
        counters.otherThreadCalls.fetch_add(1, std::memory_order_relaxed);

    const DWORD previous = counters.lastThreadId.exchange(current, std::memory_order_relaxed);
    if (previous != 0 && previous != current)
        counters.threadSwitches.fetch_add(1, std::memory_order_relaxed);
}

void maybeEnableMmcss(bool enabled)
{
    if (!enabled)
        return;

    thread_local bool done = false;
    thread_local HANDLE task = nullptr;
    if (done)
        return;

    DWORD taskIndex = 0;
    task = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
    if (task != nullptr)
        AvSetMmThreadPriority(task, AVRT_PRIORITY_CRITICAL);

    done = true;
}

long __stdcall audioCallback(void* user, long command, void* data, long) noexcept
{
    auto* state = static_cast<CallbackState*>(user);
    if (state == nullptr)
        return 0;

    maybeEnableMmcss(state->useMmcss);

    LARGE_INTEGER start {};
    LARGE_INTEGER end {};
    LARGE_INTEGER frequency {};
    if (state->measureCallbackTime)
    {
        QueryPerformanceFrequency(&frequency);
        QueryPerformanceCounter(&start);
    }

    auto& counters = state->counters;
    trackCallbackThread(counters);
    counters.total.fetch_add(1, std::memory_order_relaxed);
    counters.lastCommand.store(command, std::memory_order_relaxed);

    switch (command)
    {
    case CommandStarting:
        counters.starting.fetch_add(1, std::memory_order_relaxed);
        if (auto* info = static_cast<AudioInfo*>(data))
        {
            counters.sampleRate.store(info->sampleRate, std::memory_order_relaxed);
            counters.samplesPerFrame.store(info->samplesPerFrame, std::memory_order_relaxed);
        }
        break;

    case CommandEnding:
        counters.ending.fetch_add(1, std::memory_order_relaxed);
        break;

    case CommandChange:
        counters.change.fetch_add(1, std::memory_order_relaxed);
        if (auto* info = static_cast<AudioInfo*>(data))
        {
            counters.sampleRate.store(info->sampleRate, std::memory_order_relaxed);
            counters.samplesPerFrame.store(info->samplesPerFrame, std::memory_order_relaxed);
        }
        break;

    case CommandBufferIn:
    case CommandBufferOut:
    case CommandBufferMain:
        if (command == CommandBufferIn)
            counters.inBuffers.fetch_add(1, std::memory_order_relaxed);
        else if (command == CommandBufferOut)
            counters.outBuffers.fetch_add(1, std::memory_order_relaxed);
        else
            counters.mainBuffers.fetch_add(1, std::memory_order_relaxed);

        if (auto* buffer = static_cast<AudioBuffer*>(data))
        {
            counters.sampleRate.store(buffer->sampleRate, std::memory_order_relaxed);
            counters.samplesPerFrame.store(buffer->samplesPerFrame, std::memory_order_relaxed);
            counters.inputChannels.store(buffer->inputChannels, std::memory_order_relaxed);
            counters.outputChannels.store(buffer->outputChannels, std::memory_order_relaxed);

            inspectBufferPointers(*buffer, counters);
            trackBufferCadence(*state, *buffer, start, frequency);

            if (state->action == BufferAction::Passthrough)
                passthrough(*buffer);
            else if (state->action == BufferAction::Zero)
                zeroWrites(*buffer);
        }
        break;

    default:
        break;
    }

    if (state->measureCallbackTime)
    {
        QueryPerformanceCounter(&end);
        const auto ticks = static_cast<uint64_t>(end.QuadPart - start.QuadPart);
        const auto freq = static_cast<uint64_t>(std::max<LONGLONG>(1, frequency.QuadPart));
        updateMax(counters.maxCallbackUs, (ticks * 1000000ULL) / freq);
    }

    return 0;
}

BOOL WINAPI consoleHandler(DWORD controlType)
{
    switch (controlType)
    {
    case CTRL_C_EVENT:
    case CTRL_BREAK_EVENT:
    case CTRL_CLOSE_EVENT:
        g_shouldStop.store(true, std::memory_order_release);
        return TRUE;
    default:
        return FALSE;
    }
}

long parseMode(std::wstring_view text)
{
    if (text == L"input" || text == L"in")
        return ModeInputInsert;
    if (text == L"output" || text == L"out")
        return ModeOutputInsert;
    if (text == L"main" || text == L"bus")
        return ModeMain;
    if (text == L"all")
        return ModeInputInsert | ModeOutputInsert | ModeMain;

    wchar_t* end = nullptr;
    const long value = std::wcstol(std::wstring(text).c_str(), &end, 0);
    return end != nullptr && *end == L'\0' ? value : 0;
}

std::wstring modeName(long mode)
{
    if (mode == ModeInputInsert)
        return L"input";
    if (mode == ModeOutputInsert)
        return L"output";
    if (mode == ModeMain)
        return L"main";
    if (mode == (ModeInputInsert | ModeOutputInsert | ModeMain))
        return L"all";
    return L"0x" + std::to_wstring(mode);
}

void printUsage()
{
    std::wcout << L"Elka VoiceMeeter Callback Null Host\n"
        << L"Usage:\n"
        << L"  ElkaVoiceMeeterCallbackNullHost.exe [input|output|main|all] [--passthrough|--zero|--touch-only] [--measure] [--mmcss] [--dll <path>]\n\n"
        << L"Defaults: output --passthrough. Press Ctrl+C to stop.\n";
}
}

int wmain(int argc, wchar_t** argv)
{
    long mode = ModeOutputInsert;
    CallbackState state;
    std::wstring dllOverride;

    for (int i = 1; i < argc; ++i)
    {
        const std::wstring_view arg(argv[i]);
        if (arg == L"--help" || arg == L"-h" || arg == L"/?")
        {
            printUsage();
            return 0;
        }
        if (arg == L"--passthrough")
        {
            state.action = BufferAction::Passthrough;
            continue;
        }
        if (arg == L"--zero")
        {
            state.action = BufferAction::Zero;
            continue;
        }
        if (arg == L"--touch-only")
        {
            state.action = BufferAction::TouchOnly;
            continue;
        }
        if (arg == L"--measure")
        {
            state.measureCallbackTime = true;
            continue;
        }
        if (arg == L"--mmcss")
        {
            state.useMmcss = true;
            continue;
        }
        if (arg == L"--dll" && i + 1 < argc)
        {
            dllOverride = argv[++i];
            continue;
        }

        const long parsedMode = parseMode(arg);
        if (parsedMode != 0)
        {
            mode = parsedMode;
            continue;
        }

        std::wcerr << L"Unknown argument: " << arg << L"\n";
        printUsage();
        return 2;
    }

    SetConsoleCtrlHandler(consoleHandler, TRUE);

    std::wstring error;
    RemoteApi api;
    if (!api.load(dllOverride, error))
    {
        std::wcerr << error << L"\n";
        return 1;
    }

    std::wcout << L"Loaded: " << api.dllPath() << L"\n";

    const long loginResult = api.login();
    std::wcout << L"VBVMR_Login returned " << loginResult << L"\n";

    char clientName[64] {};
    strcpy_s(clientName, "ElkaCallbackNullHost");
    const long registerResult = api.callbackRegister(mode, audioCallback, &state, clientName);
    if (registerResult != 0)
    {
        std::wcerr << L"VBVMR_AudioCallbackRegister failed: " << registerResult << L"\n";
        api.logout();
        return 1;
    }

    const long startResult = api.callbackStart();
    if (startResult != 0)
    {
        std::wcerr << L"VBVMR_AudioCallbackStart failed: " << startResult << L"\n";
        api.callbackUnregister();
        api.logout();
        return 1;
    }

    const wchar_t* actionName = state.action == BufferAction::Passthrough
        ? L"passthrough"
        : (state.action == BufferAction::Zero ? L"zero" : L"touch-only");

    std::wcout << L"Running null callback host: mode=" << modeName(mode)
        << L" action=" << actionName
        << L" measure=" << (state.measureCallbackTime ? L"on" : L"off")
        << L" mmcss=" << (state.useMmcss ? L"on" : L"off") << L"\n";
    std::wcout << L"Press Ctrl+C to stop.\n";

    uint64_t lastTotal = 0;
    uint64_t lastIn = 0;
    uint64_t lastOut = 0;
    uint64_t lastMain = 0;

    while (!g_shouldStop.load(std::memory_order_acquire))
    {
        Sleep(1000);

        const uint64_t total = state.counters.total.load(std::memory_order_relaxed);
        const uint64_t inCount = state.counters.inBuffers.load(std::memory_order_relaxed);
        const uint64_t outCount = state.counters.outBuffers.load(std::memory_order_relaxed);
        const uint64_t mainCount = state.counters.mainBuffers.load(std::memory_order_relaxed);
        const uint64_t deltaTotal = total - lastTotal;
        const uint64_t deltaIn = inCount - lastIn;
        const uint64_t deltaOut = outCount - lastOut;
        const uint64_t deltaMain = mainCount - lastMain;
        lastTotal = total;
        lastIn = inCount;
        lastOut = outCount;
        lastMain = mainCount;

        const uint64_t maxUs = state.counters.maxCallbackUs.exchange(0, std::memory_order_relaxed);
        const uint64_t minGapUs = state.counters.minIntervalUs.exchange(std::numeric_limits<uint64_t>::max(), std::memory_order_relaxed);
        const uint64_t maxGapUs = state.counters.maxIntervalUs.exchange(0, std::memory_order_relaxed);
        const uint64_t late25 = state.counters.late25.exchange(0, std::memory_order_relaxed);
        const uint64_t late50 = state.counters.late50.exchange(0, std::memory_order_relaxed);
        const uint64_t late100 = state.counters.late100.exchange(0, std::memory_order_relaxed);
        const uint64_t early50 = state.counters.early50.exchange(0, std::memory_order_relaxed);
        const uint64_t samePtr = state.counters.samePointerChannels.exchange(0, std::memory_order_relaxed);
        const uint64_t overlap = state.counters.overlapChannels.exchange(0, std::memory_order_relaxed);
        const uint64_t nullRead = state.counters.nullReadChannels.exchange(0, std::memory_order_relaxed);
        const uint64_t nullWrite = state.counters.nullWriteChannels.exchange(0, std::memory_order_relaxed);
        const uint64_t otherThreadCalls = state.counters.otherThreadCalls.exchange(0, std::memory_order_relaxed);
        const uint64_t threadSwitches = state.counters.threadSwitches.exchange(0, std::memory_order_relaxed);
        const DWORD primaryThreadId = state.counters.primaryThreadId.load(std::memory_order_relaxed);
        const long printedSampleRate = state.counters.sampleRate.load(std::memory_order_relaxed);
        const long printedSamplesPerFrame = state.counters.samplesPerFrame.load(std::memory_order_relaxed);
        const uint64_t expectedGapUs = printedSampleRate > 0 && printedSamplesPerFrame > 0
            ? (static_cast<uint64_t>(printedSamplesPerFrame) * 1000000ULL) / static_cast<uint64_t>(printedSampleRate)
            : 0;
        std::wcout << L"fmt " << printedSampleRate
            << L" Hz / " << printedSamplesPerFrame
            << L" spl | exp " << expectedGapUs << L" us | ch " << state.counters.inputChannels.load(std::memory_order_relaxed)
            << L"/" << state.counters.outputChannels.load(std::memory_order_relaxed)
            << L" | calls/s " << deltaTotal
            << L" in/out/main " << deltaIn << L"/" << deltaOut << L"/" << deltaMain
            << L" | cmd " << state.counters.lastCommand.load(std::memory_order_relaxed)
            << L" | start/change/end "
            << state.counters.starting.load(std::memory_order_relaxed) << L"/"
            << state.counters.change.load(std::memory_order_relaxed) << L"/"
            << state.counters.ending.load(std::memory_order_relaxed);
        if (state.measureCallbackTime)
        {
            std::wcout << L" | max " << maxUs << L" us";
            if (maxGapUs > 0 && minGapUs != std::numeric_limits<uint64_t>::max())
            {
                std::wcout << L" | gap us " << minGapUs << L"/" << maxGapUs
                    << L" late25/50/100 " << late25 << L"/" << late50 << L"/" << late100
                    << L" early50 " << early50;
            }
        }
        std::wcout << L" | ptr same/ov nullR/W " << samePtr << L"/" << overlap
            << L" " << nullRead << L"/" << nullWrite
            << L" | tid " << primaryThreadId
            << L" other/switch " << otherThreadCalls << L"/" << threadSwitches;
        std::wcout << L"\n";
    }

    std::wcout << L"Stopping...\n";
    api.callbackStop();
    api.callbackUnregister();
    api.logout();
    return 0;
}





