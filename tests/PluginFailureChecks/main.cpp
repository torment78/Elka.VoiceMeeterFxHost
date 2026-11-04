#include "PluginFailureProductionFixture.h"
#include <chrono>
#include <future>
#include <iostream>
#include <thread>

namespace
{
int checks = 0;
void check(bool value, const char* message)
{
    if (!value) throw std::runtime_error(message);
    ++checks;
}

class ThrowingPlugin final : public juce::AudioPluginInstance
{
public:
    bool throws = false;
    int calls = 0;
    const juce::String getName() const override { return "Failure test"; }
    void fillInPluginDescription(juce::PluginDescription&) const override {}
    void prepareToPlay(double, int) override {}
    void releaseResources() override {}
    void processBlock(juce::AudioBuffer<float>& buffer, juce::MidiBuffer&) override
    {
        ++calls;
        if (throws) throw std::runtime_error("Test callback failure");
        buffer.applyGain(0.5f);
    }
    double getTailLengthSeconds() const override { return 0; }
    bool acceptsMidi() const override { return false; }
    bool producesMidi() const override { return false; }
    bool hasEditor() const override { return false; }
    juce::AudioProcessorEditor* createEditor() override { return nullptr; }
    int getNumPrograms() override { return 1; }
    int getCurrentProgram() override { return 0; }
    void setCurrentProgram(int) override {}
    const juce::String getProgramName(int) override { return {}; }
    void changeProgramName(int, const juce::String&) override {}
    void getStateInformation(juce::MemoryBlock&) override {}
    void setStateInformation(const void*, int) override {}
};

struct Child
{
    PROCESS_INFORMATION process {};
    Child()
    {
        wchar_t path[32768] {};
        GetModuleFileNameW(nullptr, path, 32768);
        std::wstring command = L"\"" + std::wstring(path) + L"\" --worker";
        STARTUPINFOW startup {}; startup.cb = sizeof(startup);
        if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process))
            throw std::runtime_error("Could not start test-owned worker");
    }
    ~Child()
    {
        TerminateProcess(process.hProcess, 0);
        WaitForSingleObject(process.hProcess, 5000);
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
    }
};
}

int main(int argc, char** argv)
{
    if (argc > 1 && std::string(argv[1]) == "--worker") { Sleep(60000); return 0; }
    try
    {
        static_assert(std::atomic<bool>::is_always_lock_free);
        constexpr int slots = elka::PluginHostLayer::MaxPluginNodes;
        std::array<int, slots + 2> flags; flags.fill(42);
        check(ElkaFx_GetPluginNodeFailures(flags.data(), slots) == 1 && flags[0] == 42 && !g_host, "No host is unknown, without initialization");
        check(ElkaFx_GetPluginNodeFailures(nullptr, slots) == -1 && ElkaFx_GetPluginNodeFailures(flags.data(), slots - 1) == -1, "Invalid buffers rejected");
        g_host = std::make_unique<TestHost>();
        auto instance = std::make_unique<ThrowingPlugin>();
        auto* plugin = instance.get();
        auto hosted = std::make_shared<elka::HostedVst3Processor>(std::move(instance), 2, 2, 256);
        g_host->plugins.impl->nodeProcessors[3] = hosted;
        float input[32], output[32] {};
        std::fill_n(input, 32, 0.25f);
        float* writes[] { output };
        elka::AudioBufferView buffer {}; buffer.write = writes; buffer.samplesPerFrame = 32;
        elka::PluginAudioInputRoute inputRoute { input, 0 };
        elka::PluginAudioOutputRoute outputRoute { output, 0 };
        elka::PluginRoutingView routing { &inputRoute, 1, &outputRoute, 1 };
        check(hosted->process(buffer, routing) && !hosted->hasFailed() && output[0] == 0.125f, "Healthy processing unchanged");
        plugin->throws = true;
        std::thread audio([&] { hosted->process(buffer, routing); }); audio.join();
        check(hosted->hasFailed() && g_host->plugins.isPluginNodeFailed(3), "Callback exception is visible through atomic flag and host layer");
        check(output[0] == input[0], "Existing failure pass-through unchanged");
        auto priorCalls = plugin->calls;
        hosted->process(buffer, routing);
        check(plugin->calls == priorCalls && output[0] == input[0], "Failed plugin not processed again");
        check(!g_host->plugins.isPluginNodeFailed(-1) && !g_host->plugins.isPluginNodeFailed(slots) && !g_host->plugins.isPluginNodeFailed(0), "Empty and invalid slots not failed");

        Child child;
        auto sandbox = std::make_shared<elka::SandboxFailureProbe>();
        sandbox->workerProcess = child.process.hProcess;
        check(sandbox->hasFailed(), "Unready worker failed");
        sandbox->ready.store(true, std::memory_order_relaxed);
        check(!sandbox->hasFailed(), "Running ready worker healthy");
        g_host->plugins.impl->nodeProcessors[slots - 1] = sandbox;
        check(ElkaFx_GetPluginNodeFailures(flags.data(), slots + 2) == 0 && flags[3] == 1 && flags[slots - 1] == 0 && flags[slots] == 42 && flags[slots + 1] == 42, "Bulk snapshot fills exactly the supported slots");
        check(TerminateProcess(child.process.hProcess, 0) && WaitForSingleObject(child.process.hProcess, 5000) == WAIT_OBJECT_0, "Test-owned worker exited");
        check(ElkaFx_GetPluginNodeFailures(flags.data(), slots) == 0 && flags[slots - 1] == 1, "Exited worker detected by zero-time poll");

        std::promise<void> locked, release;
        auto canRelease = release.get_future();
        std::thread loader([&] { std::lock_guard lock(g_mutex); locked.set_value(); canRelease.wait_for(std::chrono::seconds(1)); });
        locked.get_future().wait();
        flags.fill(77);
        const auto start = std::chrono::steady_clock::now();
        auto busy = ElkaFx_GetPluginNodeFailures(flags.data(), slots);
        const auto elapsed = std::chrono::steady_clock::now() - start;
        release.set_value(); loader.join();
        check(busy == 1 && std::all_of(flags.begin(), flags.end(), [](int value) { return value == 77; }), "Busy lock leaves entire caller buffer untouched");
        check(elapsed < std::chrono::milliseconds(100), "Busy query does not wait for loading/saving lock");
        std::cout << "Busy export query: " << std::chrono::duration<double, std::micro>(elapsed).count() << " us.\n";
        g_host->plugins.impl->nodeProcessors[3] = std::make_shared<elka::HostedVst3Processor>(std::make_unique<ThrowingPlugin>(), 2, 2, 256);
        g_host->plugins.impl->nodeProcessors[slots - 1].reset();
        check(ElkaFx_GetPluginNodeFailures(flags.data(), slots) == 0 && std::all_of(flags.begin(), flags.begin() + slots, [](int value) { return value == 0; }), "Reload/removal produces healthy flags");
        g_host.reset();
        std::cout << "PASS: " << checks << " native failure checks; production processor/query bodies, synthetic plugin and test-owned worker. No audio device.\n";
        return 0;
    }
    catch (const std::exception& ex) { std::cerr << ex.what() << '\n'; return 1; }
}
