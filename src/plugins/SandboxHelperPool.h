#pragma once

// Included inside the plugin host's private implementation namespace.
std::atomic<bool> sandboxLoadCancelled { false };

class SandboxHelper
{
public:
    ~SandboxHelper()
    {
        if (shutdown) SetEvent(shutdown);
        if (process && WaitForSingleObject(process, 1500) != WAIT_OBJECT_0)
        {
            TerminateProcess(process, 2);
            WaitForSingleObject(process, 1500);
        }
        if (header) UnmapViewOfFile(header);
        closeHandleIfValid(process);
        closeHandleIfValid(mapping);
        closeHandleIfValid(request);
        closeHandleIfValid(response);
        closeHandleIfValid(shutdown);
    }

    bool alive() const noexcept { return process && WaitForSingleObject(process, 0) == WAIT_TIMEOUT; }
    HANDLE processHandle() const noexcept { return process; }

    static std::shared_ptr<SandboxHelper> acquire(const std::string& key, std::string& error)
    {
        static std::mutex registryMutex;
        static std::map<std::string, std::weak_ptr<SandboxHelper>> registry;
        std::lock_guard lock(registryMutex);
        for (auto it = registry.begin(); it != registry.end();)
            it = it->second.expired() ? registry.erase(it) : std::next(it);
        if (auto existing = registry[key].lock(); existing && existing->alive()) return existing;
        auto helper = std::shared_ptr<SandboxHelper>(new SandboxHelper());
        if (!helper->start(key, error)) return {};
        registry[key] = helper;
        return helper;
    }

    bool add(LONG id, const std::wstring& base, const std::string& format, const std::string& identifier,
             int rate, int samples, int in, int out, int inLayout, int outLayout,
             const PluginLoadProgressCallback& progress, std::string& error)
    {
        std::lock_guard lock(controlMutex);
        if (!alive()) { error = "Sandbox helper has exited."; return false; }
        if (!copy(header->format, fromUtf8(format)) || !copy(header->identifier, fromUtf8(identifier)) ||
            !copy(header->instanceBase, base))
        {
            error = "Plugin identifier is too long for pooled-helper IPC.";
            return false;
        }
        header->sampleRate = rate; header->maxSamples = samples;
        header->inputPins = in; header->outputPins = out;
        header->inputLayout = inLayout; header->outputLayout = outLayout;
        header->instanceId = id;
        InterlockedExchange(&header->command, PoolAddInstance);
        const auto sequence = ++requestCounter;
        InterlockedExchange(&header->requestId, sequence);
        SetEvent(request);
        // WaveShell's cold constructor can exceed 30 seconds during Windows startup.
        // This is a control-thread load deadline, not an audio callback timeout.
        auto lowerIdentifier = identifier;
        std::transform(lowerIdentifier.begin(), lowerIdentifier.end(), lowerIdentifier.begin(),
                       [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        const DWORD loadTimeout = lowerIdentifier.find("waveshell") != std::string::npos ? 90000 : SandboxReadyTimeoutMs;
        if (!wait(sequence, loadTimeout, true, progress, error))
        {
            if (alive() && InterlockedCompareExchange(&header->responseId, 0, 0) == sequence)
                return false; // A completed, rejected ADD must not terminate existing peers.
            // A blocked plugin constructor cannot be safely interrupted in-process.
            // Its helper is the containment boundary; peers report failure as well.
            TerminateProcess(process, 2);
            WaitForSingleObject(process, 1500);
            return false;
        }
        return true;
    }

    void remove(LONG id) noexcept
    {
        if (!alive()) return;
        std::lock_guard lock(controlMutex);
        header->instanceId = id;
        InterlockedExchange(&header->command, PoolRemoveInstance);
        const auto sequence = ++requestCounter;
        InterlockedExchange(&header->requestId, sequence);
        SetEvent(request);
        std::string error;
        if (!wait(sequence, 3000, false, {}, error))
        {
            TerminateProcess(process, 2);
            WaitForSingleObject(process, 1500);
        }
    }

private:
    bool start(const std::string& key, std::string& error)
    {
        const auto worker = configuredWorkerPath();
        if (worker.empty() || !std::filesystem::exists(worker))
        {
            error = "Elka.PluginWorker.exe was not found.";
            return false;
        }
        const auto id = InterlockedIncrement(&sandboxGlobalSequence);
        const auto base = L"Local\\ElkaFxPool_" + std::to_wstring(GetCurrentProcessId()) + L"_" + std::to_wstring(id);
        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(PoolHeader), (base + L"_map").c_str());
        request = CreateEventW(nullptr, FALSE, FALSE, (base + L"_request").c_str());
        response = CreateEventW(nullptr, FALSE, FALSE, (base + L"_response").c_str());
        shutdown = CreateEventW(nullptr, TRUE, FALSE, (base + L"_shutdown").c_str());
        if (!mapping || !request || !response || !shutdown)
        {
            error = "Could not create pooled-helper IPC.";
            return false;
        }
        header = static_cast<PoolHeader*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(PoolHeader)));
        if (!header) { error = "Could not map pooled-helper control data."; return false; }
        new (header) PoolHeader {};
        std::wstring command = quoteWindowsArg(worker) + L" host-v2 " + quoteWindowsArg(fromUtf8(key)) + L" " +
            quoteWindowsArg(base + L"_map") + L" " + quoteWindowsArg(base + L"_request") + L" " +
            quoteWindowsArg(base + L"_response") + L" " + quoteWindowsArg(base + L"_shutdown") + L" " +
            std::to_wstring(GetCurrentProcessId());
        STARTUPINFOW startup {}; startup.cb = sizeof(startup);
        PROCESS_INFORMATION launched {};
        if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | NORMAL_PRIORITY_CLASS,
                            nullptr, nullptr, &startup, &launched))
        {
            error = "Could not start pooled plugin helper.";
            return false;
        }
        process = launched.hProcess;
        CloseHandle(launched.hThread);
        AllowSetForegroundWindow(launched.dwProcessId);
        return wait(0, SandboxReadyTimeoutMs, false, {}, error);
    }

    bool wait(LONG expected, DWORD timeout, bool loading, const PluginLoadProgressCallback& progress, std::string& error)
    {
        auto deadline = GetTickCount64() + timeout;
        bool waitingForWindow = false;
        for (;;)
        {
            if (InterlockedCompareExchange(&header->responseId, 0, 0) == expected &&
                (expected != 0 || InterlockedCompareExchange(&header->status, 0, 0) != 0))
            {
                if (header->status > 0) return true;
                error = header->status == -2 ? "Sandbox pool protocol v2 mismatch." : std::string(header->error, strnlen_s(header->error, sizeof(header->error)));
                return false;
            }
            if (!alive()) { error = "Sandbox helper exited while loading a plugin."; return false; }
            if (loading && sandboxLoadCancelled.load(std::memory_order_relaxed))
            {
                error = "Plugin load cancelled; its shared helper was stopped. Reload affected plugins to recover.";
                return false;
            }
            if (GetTickCount64() >= deadline)
            {
                if (loading && hasVisibleWindow())
                {
                    if (!waitingForWindow && progress)
                        progress("Waiting for plugin licence/activation", "Finish the plugin dialog or cancel the load.");
                    waitingForWindow = true;
                    deadline = GetTickCount64() + 1000;
                }
                else if (waitingForWindow)
                {
                    waitingForWindow = false;
                    deadline = GetTickCount64() + timeout;
                }
                else { error = "Sandbox helper did not finish its request before timeout (" + std::to_string(timeout) + " ms)."; return false; }
            }
            const HANDLE handles[] { response, process };
            if (WaitForMultipleObjects(2, handles, FALSE, 100) == WAIT_FAILED)
            { error = "Sandbox helper wait failed."; return false; }
        }
    }

    bool hasVisibleWindow() const
    {
        struct Search { DWORD pid; bool found; } search { GetProcessId(process), false };
        EnumWindows([](HWND window, LPARAM data) -> BOOL {
            auto& search = *reinterpret_cast<Search*>(data);
            DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
            if (pid == search.pid && IsWindowVisible(window)) { search.found = true; return FALSE; }
            return TRUE;
        }, reinterpret_cast<LPARAM>(&search));
        return search.found;
    }
    template<size_t N> static bool copy(wchar_t (&to)[N], const std::wstring& from)
    {
        if (from.size() >= N) return false;
        std::copy(from.begin(), from.end(), to); to[from.size()] = 0; return true;
    }
    std::mutex controlMutex;
    HANDLE process = nullptr, mapping = nullptr, request = nullptr, response = nullptr, shutdown = nullptr;
    PoolHeader* header = nullptr;
    LONG requestCounter = 0;
};
