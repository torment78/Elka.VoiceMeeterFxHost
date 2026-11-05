#include "plugins/PluginHostLayer.h"
#include "plugins/SandboxProtocol.h"
#include "plugins/SandboxPoolProtocol.h"
#include <map>

#include <algorithm>
#include <atomic>
#include <avrt.h>
#include <cerrno>
#include <climits>
#include <cstring>
#include <memory>
#include <objbase.h>
#include <shellapi.h>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include <juce_events/juce_events.h>

namespace
{
using namespace elka;
using namespace elka::sandbox;

struct Handle
{
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle() = default;
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

std::string utf8(const wchar_t* value)
{
    const int length = static_cast<int>(std::wcslen(value));
    const int bytes = WideCharToMultiByte(CP_UTF8, 0, value, length, nullptr, 0, nullptr, nullptr);
    std::string text(static_cast<size_t>(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, length, text.data(), bytes, nullptr, nullptr);
    return text;
}

void report(const std::string& text, bool error = false)
{
    // WinExe has no console, but probe/scan callers inherit redirected pipe handles.
    const auto output = GetStdHandle(error ? STD_ERROR_HANDLE : STD_OUTPUT_HANDLE);
    if (!output || output == INVALID_HANDLE_VALUE) return;
    const auto line = text + "\n";
    DWORD written = 0;
    WriteFile(output, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
}

int number(const wchar_t* text, int fallback)
{
    wchar_t* end = nullptr;
    errno = 0;
    const auto value = std::wcstol(text, &end, 10);
    return errno || end == text || *end != L'\0' || value < INT_MIN || value > INT_MAX
        ? fallback : static_cast<int>(value);
}

class DialogOwner
{
public:
    static HWND handle() noexcept { return owner; }
    DialogOwner()
    {
        WNDCLASSW definition {};
        definition.lpfnWndProc = DefWindowProcW;
        definition.hInstance = GetModuleHandleW(nullptr);
        definition.lpszClassName = L"ElkaNativePluginOwner";
        RegisterClassW(&definition);
        owner = CreateWindowExW(WS_EX_TOOLWINDOW, definition.lpszClassName,
            L"Elka VoiceMeeter FX Host - Plugin", WS_OVERLAPPED,
            0, 0, 0, 0, nullptr, nullptr, definition.hInstance, nullptr);
        if (!owner) throw std::runtime_error("Could not create the plugin dialog owner.");
        hook = SetWindowsHookExW(WH_CBT, onCreate, nullptr, GetCurrentThreadId());
        events = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, nullptr, onShow,
            GetCurrentProcessId(), 0, WINEVENT_OUTOFCONTEXT);
    }
    ~DialogOwner()
    {
        if (events) UnhookWinEvent(events);
        if (hook) UnhookWindowsHookEx(hook);
        if (owner) DestroyWindow(owner);
        owner = nullptr;
    }
private:
    static LRESULT CALLBACK onCreate(int code, WPARAM w, LPARAM l)
    {
        if (code == HCBT_CREATEWND && owner && reinterpret_cast<HWND>(w) != owner)
        {
            auto* create = reinterpret_cast<CBT_CREATEWNDW*>(l)->lpcs;
            if (!(create->style & WS_CHILD) && !create->hwndParent)
                create->hwndParent = owner;
        }
        return CallNextHookEx(nullptr, code, w, l);
    }
    static void CALLBACK onShow(HWINEVENTHOOK, DWORD, HWND window, LONG object, LONG child, DWORD, DWORD)
    {
        if (object != OBJID_WINDOW || child != CHILDID_SELF || !window || window == owner ||
            !owner || (GetWindowLongPtrW(window, GWL_STYLE) & WS_CHILD) || GetParent(window)) return;
        DWORD pid = 0;
        GetWindowThreadProcessId(window, &pid);
        if (pid == GetCurrentProcessId() && !GetWindow(window, GW_OWNER))
            SetWindowLongPtrW(window, GWLP_HWNDPARENT, reinterpret_cast<LONG_PTR>(owner));
    }
    inline static HWND owner = nullptr;
    HHOOK hook = nullptr;
    HWINEVENTHOOK events = nullptr;
};

struct AudioPriority
{
    HANDLE registration = nullptr;
    AudioPriority() noexcept
    {
        DWORD index = 0;
        registration = AvSetMmThreadCharacteristicsW(L"Pro Audio", &index);
        if (!registration) registration = AvSetMmThreadCharacteristicsW(L"Audio", &index);
        if (registration) AvSetMmThreadPriority(registration, AVRT_PRIORITY_HIGH);
    }
    ~AudioPriority() { if (registration) AvRevertMmThreadCharacteristics(registration); }
};

struct ControlStatusTimer
{
    UINT_PTR id = SetTimer(DialogOwner::handle(), 1, 1000, nullptr);
    ~ControlStatusTimer() { if (id) KillTimer(DialogOwner::handle(), id); }
};

class HostV1
{
public:
    ~HostV1()
    {
        if (controlWait) UnregisterWaitEx(controlWait, INVALID_HANDLE_VALUE);
        if (shutdown.value) SetEvent(shutdown.value);
        // Never unmap buffers or destroy the plugin while its audio call is still running.
        if (audioThread.joinable()) audioThread.join();
        if (plugin > 0) destroyWorkerPluginProcessor(plugin);
        if (view) UnmapViewOfFile(view);
    }

    int initialize(const std::vector<std::wstring>& args)
    {
        if (args.size() != 13 && args.size() != 15)
        {
            report("Sandbox protocol v2 requires a separate control response event.", true);
            return 64;
        }
        const bool layouts = args.size() == 15;
        const size_t base = layouts ? 9 : 7;
        mapping.value = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, args[base].c_str());
        request.value = OpenEventW(SYNCHRONIZE, FALSE, args[base + 1].c_str());
        response.value = OpenEventW(EVENT_MODIFY_STATE, FALSE, args[base + 2].c_str());
        shutdown.value = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, args[base + 3].c_str());
        control.value = OpenEventW(SYNCHRONIZE, FALSE, args[base + 4].c_str());
        controlResponse.value = OpenEventW(EVENT_MODIFY_STATE, FALSE, args[base + 5].c_str());
        if (!mapping.value || !request.value || !response.value || !shutdown.value || !control.value || !controlResponse.value)
            throw std::runtime_error("Could not open sandbox mapping/events.");
        view = static_cast<unsigned char*>(MapViewOfFile(mapping.value, FILE_MAP_ALL_ACCESS, 0, 0, 0));
        MEMORY_BASIC_INFORMATION region {};
        if (!view || !VirtualQuery(view, &region, sizeof(region)) || region.RegionSize < SandboxHeaderBytes)
            throw std::runtime_error("Invalid sandbox mapping.");
        header = reinterpret_cast<SandboxAudioHeader*>(view);
        const int in = std::clamp(number(args[5].c_str(), 2), 1, SandboxMaxPins);
        const int out = std::clamp(number(args[6].c_str(), 2), 1, SandboxMaxPins);
        const int block = std::clamp(number(args[4].c_str(), 512), 64, 8192);
        channels = std::max(in, out);
        blockSize = block;
        const auto audioBytes = static_cast<size_t>(channels) * block * sizeof(float);
        if (header->magic != SandboxMagic || header->version != SandboxVersion ||
            header->maxChannels != channels || header->maxSamples != block ||
            header->inputPins != in || header->outputPins != out || header->textCapacity <= 1 ||
            header->textCapacity > 64 * 1024 * 1024 ||
            region.RegionSize < SandboxHeaderBytes + audioBytes + header->textCapacity)
            return fail("Sandbox protocol version or mapping layout mismatch.", -2);
        audio = reinterpret_cast<float*>(view + SandboxHeaderBytes);
        text = reinterpret_cast<char*>(view + SandboxHeaderBytes + audioBytes);
        textCapacity = header->textCapacity;
        std::string error;
        plugin = createWorkerPluginProcessor(utf8(args[1].c_str()), utf8(args[2].c_str()),
            number(args[3].c_str(), 48000), block, in, out,
            layouts ? number(args[7].c_str(), in == 1 ? 0 : 1) : (in == 1 ? 0 : 1),
            layouts ? number(args[8].c_str(), out == 1 ? 0 : 1) : (out == 1 ? 0 : 1), error);
        if (plugin <= 0) return fail(error);
        audioContext = workerPluginAudioContext(plugin);
        if (!audioContext) return fail("Worker audio context was not created.");
        // The existing native session uses an 8192-sample planar stride. Older
        // probe clients may supply smaller mappings; stage those before starting.
        if (blockSize < 8192) staging.resize(static_cast<size_t>(channels) * 8192);
        audioThread = std::thread([this] { runAudio(); });
        complete(0, true);
        return 0;
    }

    int run(const std::vector<std::wstring>& args)
    {
        const auto result = initialize(args);
        if (result != 0) return result;
        ControlStatusTimer statusTimer;
        const HANDLE handles[] { shutdown.value, control.value };
        for (;;)
        {
            const auto signaled = MsgWaitForMultipleObjectsEx(2, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (signaled == WAIT_OBJECT_0) return 0;
            if (signaled == WAIT_FAILED) return fail("Sandbox UI wait failed.");
            if (signaled == WAIT_OBJECT_0 + 1)
            {
                if (WaitForSingleObject(shutdown.value, 0) == WAIT_OBJECT_0) return 0;
                dispatchControl();
            }
            MSG message {};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                if (message.message == WM_QUIT) return 0;
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            updateEditorStatus();
        }
    }

    void dispatchControl()
    {
        const auto id = InterlockedCompareExchange(&header->controlRequestId, 0, 0);
        bool ok = false;
        try { ok = runControl(); }
        catch (const std::exception& ex) { report(ex.what(), true); }
        catch (...) { report("Plugin control command threw.", true); }
        updateEditorStatus();
        InterlockedExchange(&header->controlStatus, ok ? 1 : -1);
        InterlockedExchange(&header->controlResponseId, id);
        SetEvent(controlResponse.value);
    }

    bool registerControl(DWORD thread, LONG id)
    {
        controlThread = thread;
        instanceId = id;
        return RegisterWaitForSingleObject(&controlWait, control.value, [](void* context, BOOLEAN) {
            auto* self = static_cast<HostV1*>(context);
            self->controlPending.store(true, std::memory_order_release);
            PostThreadMessageW(self->controlThread, WM_APP + 180, self->instanceId, 0);
        }, this, INFINITE, WT_EXECUTEDEFAULT) != FALSE;
    }

    void updateEditorStatus()
    {
        InterlockedExchange(&header->editorOpen, workerPluginEditorOpen(plugin) ? 1 : 0);
        if (workerPluginFailed(plugin)) InterlockedExchange(&header->audioFailed, 1);
    }

    bool hasPendingControl() const noexcept { return controlPending.load(std::memory_order_acquire); }
    void dispatchPendingControl()
    {
        if (controlPending.exchange(false, std::memory_order_acq_rel)) dispatchControl();
    }

private:
    int fail(const std::string& error, LONG status = -1)
    {
        report(error, true);
        if (header && response.value)
        {
            InterlockedExchange(&header->status, status);
            InterlockedExchange(&header->responseId, 0);
            SetEvent(response.value);
        }
        return 2;
    }
    void complete(LONG id, bool ok) noexcept
    {
        InterlockedExchange(&header->status, ok ? 1 : -1);
        InterlockedExchange(&header->responseId, id);
        SetEvent(response.value);
    }
    void runAudio() noexcept
    {
        const auto com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        {
            AudioPriority priority;
            const HANDLE handles[] { shutdown.value, request.value };
            for (;;)
            {
                const auto signaled = WaitForMultipleObjects(2, handles, FALSE, INFINITE);
                if (signaled != WAIT_OBJECT_0 + 1 || WaitForSingleObject(shutdown.value, 0) == WAIT_OBJECT_0) break;
                const auto id = header->requestId;
                bool ok = false;
                try
                {
                    const auto samples = std::clamp(static_cast<int>(header->sampleCount), 0, blockSize);
                    auto* data = audio;
                    if (!staging.empty())
                    {
                        data = staging.data();
                        for (int ch = 0; ch < channels; ++ch)
                            std::copy_n(audio + ch * blockSize, samples, data + ch * 8192);
                    }
                    ok = processWorkerPluginAudioContext(audioContext, data, channels, samples);
                    if (ok && !staging.empty())
                        for (int ch = 0; ch < channels; ++ch)
                            std::copy_n(data + ch * 8192, samples, audio + ch * blockSize);
                }
                catch (...) { /* Failure is reported through IPC, never logged on this thread. */ }
                if (!ok)
                    InterlockedExchange(&header->audioFailed, 1);
                complete(id, ok);
            }
        }
        if (SUCCEEDED(com)) CoUninitialize();
    }
    bool runControl()
    {
        const auto bytes = std::clamp(static_cast<int>(header->textByteCount), 0, textCapacity - 1);
        std::string error;
        std::string value;
        bool ok = false;
        bool capture = false;
        switch (header->controlCommand)
        {
            case SandboxCommandOpenEditor: ok = openWorkerPluginEditor(plugin, {text, static_cast<size_t>(bytes)}, error); break;
            case SandboxCommandCloseEditor: ok = closeWorkerPluginEditor(plugin, error); break;
            case SandboxCommandGetState: value = workerPluginStateBase64(plugin, error); capture = true; break;
            case SandboxCommandGetPreset: value = workerPluginPresetBase64(plugin, error); capture = true; break;
            case SandboxCommandGetParameters: value = workerPluginParameterStateBase64(plugin, error); capture = true; break;
            case SandboxCommandGetParameterInfo: value = workerPluginParameterInfo(plugin, error); capture = true; break;
            case SandboxCommandSetState: ok = setWorkerPluginStateBase64(plugin, {text, static_cast<size_t>(bytes)}, error); break;
            case SandboxCommandSetPreset: ok = setWorkerPluginPresetBase64(plugin, {text, static_cast<size_t>(bytes)}, error); break;
            case SandboxCommandSetParameters: ok = setWorkerPluginParameterStateBase64(plugin, {text, static_cast<size_t>(bytes)}, error); break;
            case SandboxCommandSetControl:
            {
                const std::string payload(text, static_cast<size_t>(bytes));
                const auto separator = payload.find('\n');
                if (separator == std::string::npos || separator == 0) break;
                const auto name = juce::String::fromUTF8(payload.substr(0, separator).c_str()).trim().toStdString();
                const auto setting = juce::String::fromUTF8(payload.substr(separator + 1).c_str()).trim().toStdString();
                ok = setWorkerPluginNamedControl(plugin, name, setting, value, error);
                break;
            }
            default: error = "Unknown worker control command."; break;
        }
        if (capture)
        {
            ok = error.empty() && value.size() < static_cast<size_t>(textCapacity);
            if (ok)
            {
                std::memcpy(text, value.data(), value.size());
                text[value.size()] = '\0';
            }
            InterlockedExchange(&header->textByteCount, ok ? static_cast<LONG>(value.size()) : 0);
        }
        if (!error.empty()) report(error, true);
        return ok;
    }

    Handle mapping, request, response, shutdown, control, controlResponse;
    void* audioContext = nullptr;
    HANDLE controlWait = nullptr;
    DWORD controlThread = 0;
    LONG instanceId = 0;
    std::atomic<bool> controlPending { false };
    unsigned char* view = nullptr;
    SandboxAudioHeader* header = nullptr;
    float* audio = nullptr;
    char* text = nullptr;
    int textCapacity = 0, channels = 0, blockSize = 0, plugin = 0;
    std::vector<float> staging;
    std::thread audioThread;
};

class HostPool
{
public:
    ~HostPool()
    {
        instances.clear();
        if (view) UnmapViewOfFile(view);
    }

    int run(const std::vector<std::wstring>& args)
    {
        if (args.size() != 7) return 64;
        mapping.value = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, args[2].c_str());
        request.value = OpenEventW(SYNCHRONIZE, FALSE, args[3].c_str());
        response.value = OpenEventW(EVENT_MODIFY_STATE, FALSE, args[4].c_str());
        shutdown.value = OpenEventW(SYNCHRONIZE, FALSE, args[5].c_str());
        parent.value = OpenProcess(SYNCHRONIZE, FALSE, number(args[6].c_str(), 0));
        if (!mapping.value || !request.value || !response.value || !shutdown.value || !parent.value)
            throw std::runtime_error("Could not open pooled-helper IPC or parent process.");
        view = MapViewOfFile(mapping.value, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(PoolHeader));
        if (!view) throw std::runtime_error("Invalid pooled-helper mapping.");
        header = static_cast<PoolHeader*>(view);
        if (header->magic != PoolMagic || header->version != SandboxVersion)
        {
            complete(0, -2, "Pooled helper protocol v2 mismatch.");
            return 2;
        }
        MSG message {};
        PeekMessageW(&message, nullptr, 0, 0, PM_NOREMOVE);
        complete(0, 1, "");
        ControlStatusTimer statusTimer;
        const HANDLE handles[] { shutdown.value, parent.value, request.value };
        for (;;)
        {
            // Plugin modal loops may consume thread wake messages. Keep the
            // request independently latched until the outer UI loop handles it.
            for (auto& [id, instance] : instances) instance->dispatchPendingControl();
            if (std::any_of(instances.begin(), instances.end(), [](const auto& entry) { return entry.second->hasPendingControl(); }))
                continue;
            // Failure state is sampled on the control thread, never the audio thread.
            const auto signaled = MsgWaitForMultipleObjectsEx(3, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (signaled == WAIT_OBJECT_0 || signaled == WAIT_OBJECT_0 + 1) return 0;
            if (signaled == WAIT_FAILED) return 2;
            if (signaled == WAIT_OBJECT_0 + 2) command();
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                if (message.message == WM_QUIT) return 0;
                if (!message.hwnd && message.message == WM_APP + 180)
                {
                    auto found = instances.find(static_cast<LONG>(message.wParam));
                    if (found != instances.end()) found->second->dispatchPendingControl();
                }
                else { TranslateMessage(&message); DispatchMessageW(&message); }
            }
            for (auto& [id, instance] : instances) instance->updateEditorStatus();
        }
    }

private:
    void complete(LONG id, LONG status, const char* error)
    {
        strncpy_s(header->error, error, _TRUNCATE);
        InterlockedExchange(&header->status, status);
        InterlockedExchange(&header->responseId, id);
        SetEvent(response.value);
    }
    void command()
    {
        const auto id = InterlockedCompareExchange(&header->requestId, 0, 0);
        const auto instanceId = header->instanceId;
        try
        {
            if (instanceId <= 0) throw std::runtime_error("Invalid helper instance ID.");
            if (header->command == PoolRemoveInstance)
            {
                instances.erase(instanceId);
            }
            else if (header->command == PoolAddInstance)
            {
                if (instances.size() >= HostedPluginCapacity || instances.contains(instanceId))
                    throw std::runtime_error("Helper capacity exceeded or instance already exists.");
                if (!terminated(header->format) || !terminated(header->identifier) || !terminated(header->instanceBase))
                    throw std::runtime_error("Invalid pooled-helper text field.");
                const std::wstring base(header->instanceBase);
                std::vector<std::wstring> arguments {
                    L"host-v1", header->format, header->identifier,
                    std::to_wstring(header->sampleRate), std::to_wstring(header->maxSamples),
                    std::to_wstring(header->inputPins), std::to_wstring(header->outputPins),
                    std::to_wstring(header->inputLayout), std::to_wstring(header->outputLayout),
                    base + L"_map", base + L"_request", base + L"_response",
                    base + L"_shutdown", base + L"_control", base + L"_control_response"
                };
                auto instance = std::make_unique<HostV1>();
                if (instance->initialize(arguments) != 0)
                    throw std::runtime_error("Plugin instance could not be initialized in helper.");
                if (!instance->registerControl(GetCurrentThreadId(), instanceId))
                    throw std::runtime_error("Could not register instance control event.");
                instances.emplace(instanceId, std::move(instance));
            }
            else throw std::runtime_error("Unknown pooled-helper command.");
            complete(id, 1, "");
        }
        catch (const std::exception& ex) { complete(id, -1, ex.what()); }
        catch (...) { complete(id, -1, "Pooled-helper command failed."); }
    }
    template <size_t N> static bool terminated(const wchar_t (&text)[N])
    {
        return std::find(std::begin(text), std::end(text), L'\0') != std::end(text);
    }
    Handle mapping, request, response, shutdown, parent;
    void* view = nullptr;
    PoolHeader* header = nullptr;
    std::map<LONG, std::unique_ptr<HostV1>> instances;
};
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    SetPriorityClass(GetCurrentProcess(), NORMAL_PRIORITY_CLASS);
    const auto com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    int result = 5;
    try
    {
        juce::ScopedJuceInitialiser_GUI juce;
        DialogOwner owner;
        int count = 0;
        auto** raw = CommandLineToArgvW(GetCommandLineW(), &count);
        if (!raw) throw std::runtime_error("Could not parse worker arguments.");
        std::vector<std::wstring> args;
        for (int i = 1; i < count; ++i) args.emplace_back(raw[i]);
        LocalFree(raw);
        if (args.empty()) { report("Elka Plugin Worker: probe, scan-shell, host-v1, host-v2"); result = 0; }
        else if (_wcsicmp(args[0].c_str(), L"host-v1") == 0) result = HostV1().run(args);
        else if (_wcsicmp(args[0].c_str(), L"host-v2") == 0) result = HostPool().run(args);
        else if (_wcsicmp(args[0].c_str(), L"probe") == 0 && args.size() >= 3)
        {
            std::string error;
            const bool ok = probePluginFile(utf8(args[1].c_str()), utf8(args[2].c_str()),
                args.size() >= 4 ? number(args[3].c_str(), 48000) : 48000,
                args.size() >= 5 ? number(args[4].c_str(), 512) : 512, error);
            report(ok ? "Plugin probe succeeded." : "Plugin probe failed: " + error);
            result = ok ? 0 : 2;
        }
        else if (_wcsicmp(args[0].c_str(), L"scan-shell") == 0 && args.size() == 4)
        {
            std::string error;
            const bool ok = scanPluginContainerToFile(utf8(args[1].c_str()), utf8(args[2].c_str()), utf8(args[3].c_str()), error);
            report(ok ? "Plugin container scan complete." : error);
            result = ok ? 0 : 2;
        }
        else { report("Invalid worker command or arguments.", true); result = 64; }
    }
    catch (const std::exception& ex) { report(ex.what(), true); }
    catch (...) { report("Plugin worker failed.", true); }
    if (SUCCEEDED(com)) CoUninitialize();
    return result;
}
