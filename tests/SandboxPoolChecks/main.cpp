// Exercise the production pool/wrapper without constructing a host, registering
// callbacks, scanning installed plugins, or reading/writing application settings.
#include "plugins/PluginHostLayer.cpp"
#include <psapi.h>
#include <future>
#include <iostream>
#include <thread>

using namespace elka;
using sandbox::Mode;
int checks = 0;
void check(bool ok, const char* what) { if (!ok) throw std::runtime_error(what); ++checks; }
using Instance = std::unique_ptr<SandboxedPluginProcessor>;
Instance load(const std::string& plugin, const std::string& format, const std::string& vendor, Mode mode)
{
    sandboxLoadCancelled.store(false);
    std::string error;
    auto node = std::make_unique<SandboxedPluginProcessor>(format, plugin, 48000, 8192, 8192, 2, 2, 1, 1, error, vendor, mode);
    if (!node->isReady()) throw std::runtime_error("Load failed: " + error);
    check(!node->hasFailed(), "New node must not be failed");
    return node;
}
const SandboxHelper* helper(const Instance& node) { return static_cast<const SandboxHelper*>(node->helperIdentity()); }
double memory(const std::vector<Instance>& nodes)
{
    std::set<const SandboxHelper*> unique;
    double bytes = 0;
    for (const auto& node : nodes) if (unique.insert(helper(node)).second)
    {
        PROCESS_MEMORY_COUNTERS_EX info {}; info.cb = sizeof(info);
        check(GetProcessMemoryInfo(helper(node)->processHandle(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&info), sizeof(info)), "Memory query");
        bytes += info.PrivateUsage;
    }
    std::cout << "helpers=" << unique.size() << " privateMiB=" << bytes / (1024 * 1024) << std::endl;
    return bytes;
}
bool audio(SandboxedPluginProcessor& node, float gain, bool verify = true)
{
    constexpr int samples = 256;
    std::array<float, samples> left {}, right {}, outL {}, outR {};
    for (int i = 0; i < samples; ++i) { left[i] = float(i - 128) / 1024; right[i] = -left[i]; }
    PluginAudioInputRoute inputs[] { {left.data(), 0}, {right.data(), 1} };
    PluginAudioOutputRoute outputs[] { {outL.data(), 0, -1, -1, -1}, {outR.data(), 1, -1, -1, -1} };
    AudioBufferView view {}; view.samplesPerFrame = samples; view.sampleRate = 48000;
    if (!node.process(view, {inputs, 2, outputs, 2})) return false;
    for (int i = 0; i < samples; ++i)
        if (!std::isfinite(outL[i]) || !std::isfinite(outR[i]) ||
            (verify && (outL[i] != left[i] * gain || outR[i] != right[i] * gain))) return false;
    return true;
}
void synthetic(const std::string& plugin)
{
    using sandbox::groupKey;
    check(groupKey(Mode::Auto, "C:/A.dll|elka-shell-v1|00000001", "Waves", "1") ==
        groupKey(Mode::Auto, "c:\\a.dll|elka-shell-v1|00000002", "Waves", "2"), "Shell UIDs share container key");
    check(groupKey(Mode::Auto, "a", " Vendor ", "1") == groupKey(Mode::Auto, "b", "vendor", "2"), "Manufacturer key");
    check(groupKey(Mode::Shared, "a", "A", "1") == groupKey(Mode::Shared, "b", "B", "2"), "Shared key");
    check(groupKey(Mode::OwnProcess, "a", "A", "1") != groupKey(Mode::OwnProcess, "a", "A", "2"), "Own process key");
    std::vector<Instance> nodes;
    nodes.push_back(load(plugin, "VST2", "vendor-a", Mode::Auto));
    nodes.push_back(load(plugin, "VST2", "vendor-a", Mode::Auto));
    nodes.push_back(load(plugin, "VST2", "vendor-b", Mode::Auto));
    nodes.push_back(load(plugin, "VST2", "vendor-a", Mode::OwnProcess));
    check(helper(nodes[0]) == helper(nodes[1]), "Auto joins helper");
    check(helper(nodes[0]) != helper(nodes[2]) && helper(nodes[0]) != helper(nodes[3]), "Vendor and own process separate");
    for (auto& node : nodes) for (int i = 0; i < 100; ++i) check(audio(*node, .5f), "Pooled audio exact");
    std::string error, applied;
    check(nodes[0]->setNamedControl("mix", "0.25", applied, error), "Independent control");
    check(audio(*nodes[0], .25f) && audio(*nodes[1], .5f), "No state leakage between instances");
    auto state = nodes[0]->stateBase64(error);
    check(!state.empty() && error.empty(), "State capture");
    check(nodes[0]->openEditor("Pool test A", error), "First editor opens");
    check(nodes[0]->isEditorOpen() && !nodes[1]->isEditorOpen(), "Editor tracking is per instance");
    check(nodes[1]->openEditor("Pool test B", error), "Second editor opens");
    check(nodes[0]->closeEditor(error), "Close first editor");
    check(!nodes[0]->isEditorOpen() && nodes[1]->isEditorOpen(), "Close does not affect peer editor");
    check(nodes[1]->closeEditor(error), "Close second editor");
    memory(nodes);
    auto process = helper(nodes[0])->processHandle();
    check(TerminateProcess(process, 77), "Kill test helper only");
    check(WaitForSingleObject(process, 5000) == WAIT_OBJECT_0, "Helper exits");
    check(nodes[0]->hasFailed() && nodes[1]->hasFailed() && !nodes[2]->hasFailed() && !nodes[3]->hasFailed(), "Failure isolated to helper peers");
    check(audio(*nodes[0], 1) && audio(*nodes[1], 1), "Dead helper passes through");
    nodes[0] = load(plugin, "VST2", "vendor-a", Mode::Auto);
    nodes[1] = load(plugin, "VST2", "vendor-a", Mode::Auto);
    check(helper(nodes[0]) == helper(nodes[1]), "Reloaded peers join replacement helper");
    check(nodes[0]->setStateBase64(state, error) && audio(*nodes[0], .25f), "Reload restores captured state");
    HANDLE last = nullptr;
    check(DuplicateHandle(GetCurrentProcess(), helper(nodes[0])->processHandle(), GetCurrentProcess(), &last, SYNCHRONIZE, FALSE, 0), "Observe helper shutdown");
    nodes[0].reset();
    check(WaitForSingleObject(last, 0) == WAIT_TIMEOUT && audio(*nodes[1], .5f), "Removing one instance keeps peer alive");
    nodes[1].reset();
    check(WaitForSingleObject(last, 5000) == WAIT_OBJECT_0, "Last instance shuts down helper"); CloseHandle(last);
    nodes.clear();
    nodes.push_back(load(plugin, "VST2", "a", Mode::Shared));
    nodes.push_back(load(plugin, "VST2", "b", Mode::Shared));
    check(helper(nodes[0]) == helper(nodes[1]), "Shared ignores manufacturer");
    std::string rejected;
    auto invalid = std::make_unique<SandboxedPluginProcessor>("VST2", "C:/missing-plugin.dll", 48000, 8192, 8192,
        2, 2, 1, 1, rejected, "a", Mode::Shared);
    check(!invalid->isReady() && !rejected.empty(), "Bad ADD rejected");
    check(!nodes[0]->hasFailed() && audio(*nodes[0], .5f), "Rejected ADD leaves existing peers intact");
    nodes.clear();

    const auto eventName = L"Local\\PoolCapture_" + std::to_wstring(GetCurrentProcessId());
    auto started = CreateEventW(nullptr, FALSE, FALSE, eventName.c_str());
    SetEnvironmentVariableW(L"ELKA_TEST_CAPTURE_DELAY_MS", L"1000");
    SetEnvironmentVariableW(L"ELKA_TEST_CAPTURE_STARTED", eventName.c_str());
    nodes.push_back(load(plugin, "VST2", "slow", Mode::Auto));
    nodes.push_back(load(plugin, "VST2", "slow", Mode::Auto));
    auto capture = std::async(std::launch::async, [&] { std::string e; return nodes[0]->stateBase64(e); });
    check(WaitForSingleObject(started, 5000) == WAIT_OBJECT_0, "Slow pooled capture entered");
    auto peerControl = std::async(std::launch::async, [&] {
        std::string e, applied;
        return nodes[1]->setNamedControl("mix", "0.5", applied, e);
    });
    for (int i = 0; i < 100; ++i)
        check(audio(*nodes[0], .5f) && audio(*nodes[1], .5f), "Both pooled audio threads continue during state capture");
    check(!capture.get().empty(), "Slow pooled state capture completes");
    check(peerControl.get(), "Peer control survives a nested plugin message loop");
    nodes.clear(); CloseHandle(started);
    SetEnvironmentVariableW(L"ELKA_TEST_CAPTURE_DELAY_MS", nullptr);
    SetEnvironmentVariableW(L"ELKA_TEST_CAPTURE_STARTED", nullptr);

    SetEnvironmentVariableW(L"ELKA_TEST_ACTIVATION_DELAY_MS", L"32000");
    auto activation = load(plugin, "VST2", "licence", Mode::OwnProcess);
    check(activation->isReady(), "Visible activation window survives 30-second deadline");
    activation.reset();
    sandboxLoadCancelled.store(false);
    auto loading = std::async(std::launch::async, [&] {
        std::string e;
        auto node = std::make_unique<SandboxedPluginProcessor>("VST2", plugin, 48000, 8192, 8192,
            2, 2, 1, 1, e, "cancel", Mode::OwnProcess);
        return !node->isReady() && e.find("cancelled") != std::string::npos;
    });
    Sleep(1000); cancelSandboxPluginLoad();
    check(loading.wait_for(std::chrono::seconds(5)) == std::future_status::ready && loading.get(), "User cancellation interrupts activation wait");
    SetEnvironmentVariableW(L"ELKA_TEST_ACTIVATION_DELAY_MS", nullptr);
    sandboxLoadCancelled.store(false);
    SetEnvironmentVariableW(L"ELKA_TEST_THROW_ON_AUDIO", L"1");
    auto failing = load(plugin, "VST2", "throw", Mode::OwnProcess);
    check(audio(*failing, 1), "Throwing worker plugin falls back to pass-through");
    Sleep(1500);
    check(failing->hasFailed(), "Worker control thread publishes caught plugin failure");
    SetEnvironmentVariableW(L"ELKA_TEST_THROW_ON_AUDIO", nullptr);
}
void mismatch(const std::wstring& worker)
{
    const auto base = L"Local\\PoolMismatch_" + std::to_wstring(GetCurrentProcessId());
    auto mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(sandbox::PoolHeader), (base + L"_map").c_str());
    auto* header = static_cast<sandbox::PoolHeader*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(sandbox::PoolHeader)));
    new (header) sandbox::PoolHeader {};
    header->version = 99;
    auto request = CreateEventW(nullptr, FALSE, FALSE, (base + L"_request").c_str());
    auto response = CreateEventW(nullptr, FALSE, FALSE, (base + L"_response").c_str());
    auto shutdown = CreateEventW(nullptr, TRUE, FALSE, (base + L"_shutdown").c_str());
    auto command = quoteWindowsArg(worker) + L" host-v2 mismatch " + quoteWindowsArg(base + L"_map") + L" " +
        quoteWindowsArg(base + L"_request") + L" " + quoteWindowsArg(base + L"_response") + L" " +
        quoteWindowsArg(base + L"_shutdown") + L" " + std::to_wstring(GetCurrentProcessId());
    STARTUPINFOW startup {}; startup.cb = sizeof(startup); PROCESS_INFORMATION process {};
    check(CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process), "Start mismatched pool client");
    const bool rejected = WaitForSingleObject(response, 5000) == WAIT_OBJECT_0 && header->status == -2;
    SetEvent(shutdown); WaitForSingleObject(process.hProcess, 5000);
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    UnmapViewOfFile(header); CloseHandle(mapping); CloseHandle(request); CloseHandle(response); CloseHandle(shutdown);
    check(rejected, "Pooled protocol version mismatch refused");
}

uint32_t presetClassHash(const std::string& preset)
{
    juce::MemoryBlock bytes;
    check(bytes.fromBase64Encoding(juce::String::fromUTF8(preset.c_str())), "Preset decodes completely");
    check(bytes.getSize() >= 48 && std::memcmp(bytes.getData(), "VST3", 4) == 0, "VST3 preset header");
    const auto* classId = static_cast<const char*>(bytes.getData()) + 8;
    uint32_t hash = 0;
    for (int part = 0; part < 4; ++part)
    {
        uint32_t word = 0;
        const auto* start = classId + part * 8;
        const auto parsed = std::from_chars(start, start + 8, word, 16);
        check(parsed.ec == std::errc() && parsed.ptr == start + 8, "Complete preset class ID");
        hash = hash * 31 + word;
    }
    return hash;
}

void shellIdentity(const std::string& shell)
{
    juce::VST3PluginFormat format;
    juce::OwnedArray<juce::PluginDescription> found;
    format.findAllTypesForFile(found, juce::String::fromUTF8(shell.c_str()));
    check(found.size() > 1, "Shared container has multiple plugin classes");
    auto verify = [&](SandboxedPluginProcessor& node, const juce::PluginDescription& expected)
    {
        std::string error;
        auto preset = node.presetBase64(error);
        check(!preset.empty() && error.empty(), "Capture loaded component identity");
        check(presetClassHash(preset) == static_cast<uint32_t>(expected.uniqueId), "Actual component class matches selected shell member");
        const auto info = node.parameterInfo(error);
        check(!info.empty() && error.empty(), "Loaded controller exposes parameters");
        if (expected.name.startsWith("Vocal Bender"))
        {
            check(info.find("\tPitch\t") != std::string::npos && info.find("\tFormant\t") != std::string::npos,
                "Vocal Bender component has its own controller, not Clarity's");
            check(info.find("Reset Neural Network") == std::string::npos, "Clarity controller is not substituted for Vocal Bender");
        }
        std::cout << "Verified " << expected.name << " class=" << std::hex << expected.uniqueId << std::dec << std::endl;
    };
    auto loadMember = [&](const juce::PluginDescription& description, Mode mode)
    {
        return load(pluginContainerIdentifier(shell, static_cast<uint32_t>(description.uniqueId)), "VST3", "Waves", mode);
    };
    for (auto* description : found)
    {
        auto node = loadMember(*description, Mode::OwnProcess);
        verify(*node, *description);
    }
    for (const bool reverse : {true, false})
    {
        std::vector<Instance> nodes;
        std::vector<juce::PluginDescription*> order;
        for (int i = 0; i < found.size(); ++i)
        {
            auto* description = found[reverse ? found.size() - 1 - i : i];
            nodes.push_back(loadMember(*description, Mode::Auto));
            order.push_back(description);
            check(helper(nodes.front()) == helper(nodes.back()), "All shell members share one Auto helper");
            verify(*nodes.back(), *description);
        }
        nodes[0].reset();
        nodes[0] = loadMember(*order[0], Mode::Auto);
        verify(*nodes[0], *order[0]);
        std::string error;
        check(nodes[0]->openEditor("Shell identity check", error), "Correct member editor opens");
        check(nodes[0]->closeEditor(error), "Correct member editor closes");
        verify(*nodes[0], *order[0]);
        for (size_t i = 1; i < nodes.size(); ++i) verify(*nodes[i], *order[i]);
        HANDLE process = nullptr;
        check(DuplicateHandle(GetCurrentProcess(), helper(nodes[0])->processHandle(), GetCurrentProcess(), &process, SYNCHRONIZE, FALSE, 0), "Observe shared helper lifetime");
        nodes.clear();
        const auto exited = WaitForSingleObject(process, 5000) == WAIT_OBJECT_0;
        CloseHandle(process);
        check(exited, "Shared helper exits after last shell member removal");
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 3 && argc != 4 && argc != 7) return 64;
    if (argc == 4 && std::wstring(argv[2]) != L"--shell") return 64;
    try
    {
        juce::ScopedJuceInitialiser_GUI gui;
        const auto scratch = std::filesystem::absolute("artifacts/sandbox-pool-checks/profile").wstring();
        std::filesystem::create_directories(scratch);
        SetEnvironmentVariableW(L"APPDATA", scratch.c_str());
        SetEnvironmentVariableW(L"LOCALAPPDATA", scratch.c_str());
        SetEnvironmentVariableW(L"ELKA_PLUGIN_WORKER_EXE", argv[1]);
        if (argc == 4 && std::wstring(argv[2]) == L"--shell") shellIdentity(toUtf8(argv[3]));
        else if (argc == 3) { mismatch(argv[1]); synthetic(toUtf8(argv[2])); }
        else
        {
            // Four shell instances, two instances of another maker, one own.
            std::vector<std::string> plugins {toUtf8(argv[2]), toUtf8(argv[3]), toUtf8(argv[4]), toUtf8(argv[2]),
                toUtf8(argv[5]), toUtf8(argv[5]), toUtf8(argv[6])};
            for (const bool pooled : {false, true})
            {
                std::vector<Instance> nodes;
                std::vector<std::string> saved;
                for (size_t i = 0; i < plugins.size(); ++i)
                    nodes.push_back(load(plugins[i], "VST3", i < 4 ? "Waves" : "Universal Audio", pooled && i != 6 ? Mode::Auto : Mode::OwnProcess));
                for (auto& node : nodes)
                {
                    for (int block = 0; block < 50; ++block) check(audio(*node, 0, false), "Vendor processing");
                    std::string error;
                    auto state = node->stateBase64(error);
                    check(!state.empty() && error.empty() && node->setStateBase64(state, error), "Vendor state round trip");
                    saved.push_back(state);
                    check(node->openEditor("Pooled helper test", error), "Vendor editor");
                    check(node->closeEditor(error), "Vendor editor close");
                }
                std::cout << (pooled ? "pooled " : "own "); memory(nodes);
                std::set<const void*> helpers; for (const auto& node : nodes) helpers.insert(node->helperIdentity());
                check(helpers.size() == (pooled ? 3 : 7), "Expected real-plugin helper count");
                if (pooled)
                {
                    const auto crashed = helper(nodes[0])->processHandle();
                    check(TerminateProcess(crashed, 78) && WaitForSingleObject(crashed, 5000) == WAIT_OBJECT_0, "Kill only Waves test helper");
                    for (size_t i = 0; i < nodes.size(); ++i)
                        check(nodes[i]->hasFailed() == (i < 4), "Real helper failure stays inside its group");
                    for (size_t i = 0; i < 4; ++i)
                    {
                        check(audio(*nodes[i], 1), "Crashed vendor instance passes through");
                        nodes[i] = load(plugins[i], "VST3", "Waves", Mode::Auto);
                        std::string error;
                        check(nodes[i]->setStateBase64(saved[i], error) && audio(*nodes[i], 0, false), "Real vendor reload restores saved state and processing");
                    }
                    check(helper(nodes[0]) == helper(nodes[3]) && !nodes[4]->hasFailed() && !nodes[6]->hasFailed(), "Recovered Waves share a helper; UAD/own stay alive");
                }
            }
        }
        std::cout << "PASS: " << checks << " sandbox pool checks\n";
        return 0;
    }
    catch (const std::exception& ex) { std::cerr << "FAIL: " << ex.what() << '\n'; return 1; }
}
