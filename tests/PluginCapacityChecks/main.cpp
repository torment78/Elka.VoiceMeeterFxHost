#include "engine/RealtimeEngine.h"
#include "plugins/PluginHostLayer.h"
#include <juce_audio_processors/juce_audio_processors.h>
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <new>
#include <windows.h>

thread_local bool countAllocations = false;
thread_local size_t allocationCount = 0;
void* operator new(size_t size)
{
    if (countAllocations) ++allocationCount;
    if (auto* memory = std::malloc(size ? size : 1)) return memory;
    throw std::bad_alloc();
}
void* operator new[](size_t size) { return ::operator new(size); }
void operator delete(void* memory) noexcept { std::free(memory); }
void operator delete[](void* memory) noexcept { std::free(memory); }
void operator delete(void* memory, size_t) noexcept { std::free(memory); }
void operator delete[](void* memory, size_t) noexcept { std::free(memory); }

using namespace elka;
namespace
{
constexpr int Capacity = HostedPluginCapacity;
constexpr int Frames = 128;
int checks = 0;
void check(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
    ++checks;
}

class Probe final : public RealtimePluginProcessor
{
public:
    float gain = 1;
    int calls = 0;
    bool process(AudioBufferView buffer, const PluginRoutingView& routing) noexcept override
    {
        ++calls;
        std::array<std::array<float, Frames>, 2> result {};
        for (int r = 0; r < routing.inputRouteCount; ++r)
        {
            const auto& route = routing.inputRoutes[r];
            for (int s = 0; s < buffer.samplesPerFrame; ++s) result[route.pluginPin][s] += route.source[s] * gain;
        }
        if (routing.outputLevels)
            for (int p = 0; p < 2; ++p) routing.outputLevels->capture(p, result[p].data(), buffer.samplesPerFrame);
        for (int r = 0; r < routing.outputRouteCount; ++r)
        {
            const auto& route = routing.outputRoutes[r];
            for (int s = 0; s < buffer.samplesPerFrame; ++s)
                route.destination[s] = (route.clearDestination ? 0 : route.destination[s]) + result[route.pluginPin][s];
        }
        return true;
    }
};

struct Graph
{
    std::unique_ptr<RealtimeEngine> engine = std::make_unique<RealtimeEngine>();
    std::array<std::array<float, Frames>, 2> input {}, output {};
    std::array<float*, 2> read { input[0].data(), input[1].data() }, write { output[0].data(), output[1].data() };
    AudioBufferView buffer {48000, Frames, 2, 2, read.data(), write.data()};
    CallbackStreamKind kind = CallbackStreamKind::InputInsert;
    int stream = 0;
    explicit Graph(int streamIn) : stream(streamIn)
    {
        kind = stream == 3 ? CallbackStreamKind::InputInsert : static_cast<CallbackStreamKind>(stream);
        for (int s = 0; s < Frames; ++s) { input[0][s] = (s - 64) / 512.f; input[1][s] = (64 - s) / 1024.f; }
    }
    void process() { if (stream == 3) engine->processInsertAsio(buffer); else engine->process(buffer, kind); }
    void install(int slot, RealtimePluginProcessor* processor, int previous, int next, bool bypass = false, bool power = true)
    {
        PluginInputRoute inputs[2]; PluginOutputRoute outputs[2];
        for (int p = 0; p < 2; ++p)
        {
            inputs[p] = previous < 0 ? PluginInputRoute {0, p, -1, -1, p} : PluginInputRoute {1, -1, previous, p, p};
            outputs[p] = next < 0 ? PluginOutputRoute {0, p, p, -1, -1} : PluginOutputRoute {1, p, -1, next, p};
        }
        engine->setPluginSlot(slot, processor, kind, inputs, 2, outputs, 2, true, bypass, power);
    }
};

DWORD WINAPI smallStackProcess(void* argument)
{
    auto& graph = *static_cast<Graph*>(argument);
    allocationCount = 0; countAllocations = true;
    for (int i = 0; i < 32; ++i) graph.process();
    countAllocations = false;
    return allocationCount == 0 ? 0 : 1;
}

void engineChecks()
{
    static_assert(Capacity == PluginHostLayer::MaxPluginNodes && Capacity == RealtimeEngine::MaxPluginSlots);
    for (int stream = 0; stream < 4; ++stream)
    {
        Graph graph(stream);
        auto nodes = std::make_unique<std::array<Probe, Capacity>>();
        // Reverse slot order forces the deepest possible dependency traversal.
        for (int position = 0; position < Capacity; ++position)
        {
            int slot = Capacity - 1 - position;
            (*nodes)[slot].gain = position % 2 == 0 ? 2.f : .5f;
            graph.install(slot, &(*nodes)[slot], position == 0 ? -1 : slot + 1, position == Capacity - 1 ? -1 : slot - 1);
        }
        graph.engine->dspPerformance.setEnabled(true);
        std::array<uint64_t, Capacity> masks {}; masks.fill(0x300000003ULL);
        check(graph.engine->pluginLevels.watches(masks.data(), Capacity), "All slot level watches accepted");
        HANDLE thread = CreateThread(nullptr, 256 * 1024, smallStackProcess, &graph, STACK_SIZE_PARAM_IS_A_RESERVATION, nullptr);
        check(thread != nullptr, "Small-stack callback test thread created");
        auto wait = WaitForSingleObject(thread, 30000);
        DWORD result = 1; GetExitCodeThread(thread, &result); CloseHandle(thread);
        check(wait == WAIT_OBJECT_0 && result == 0, "Full-depth chain runs on 256 KB stack with no C++ heap allocation");
        check(graph.input == graph.output, "Full chain output is bit-identical on every callback/ASIO path");
        check(std::all_of(nodes->begin(), nodes->end(), [](const Probe& node) { return node.calls == 32; }), "Every node processed once per block");
        std::vector<float> levels(PluginLevels::Values);
        graph.engine->pluginLevels.read(levels.data(), static_cast<int>(levels.size()));
        check(levels[(Capacity - 1) * 64] > 0 && levels[(Capacity - 1) * 64 + 32] > 0, "Highest slot input/output levels readable");
        std::vector<DspTimingSnapshot> timings(DspPerformance::Rows);
        std::vector<DspOverrunEvent> events(1024);
        int eventCount = 0; uint64_t dropped = 0, skipped = 0;
        const int rows = graph.engine->dspPerformance.read(timings.data(), static_cast<int>(timings.size()), events.data(), 1024, eventCount, dropped, skipped);
        check(rows == Capacity + 1 && std::all_of(timings.begin() + 1, timings.begin() + rows,
            [](const auto& row) { return row.token != 0 && row.calls > 0; }), "DSP includes every loaded node, including highest slot");
        graph.engine->clearPluginSlot(Capacity - 1);
        check(graph.engine->pluginPerformanceToken(Capacity - 1) == 0, "Last slot can be removed");
        graph.install(Capacity - 1, &(*nodes)[Capacity - 1], -1, Capacity - 2);
        graph.process(); check(graph.input == graph.output, "Slot reuse restores the full chain");
        graph.engine->clearPluginSlots();
        for (int s = 0; s < Capacity; ++s)
        {
            (*nodes)[s].gain = 1.f / 1024;
            graph.install(s, &(*nodes)[s], -1, -1);
        }
        graph.process();
        for (int p = 0; p < 2; ++p) for (int s = 0; s < Frames; ++s)
        {
            float expected = 0;
            for (int n = 0; n < Capacity; ++n) expected += graph.input[p][s] / 1024;
            check(graph.output[p][s] == expected, "Parallel branches keep existing summing semantics");
        }
        graph.engine->clearPluginSlots();
        Graph lowSlotReference(stream);
        Probe referenceNode; referenceNode.gain = 1.f / 1024;
        for (bool power : {false, true}) for (bool bypass : {false, true})
        {
            graph.install(Capacity - 1, &(*nodes)[Capacity - 1], -1, -1, bypass, power);
            lowSlotReference.install(0, &referenceNode, -1, -1, bypass, power);
            graph.process();
            lowSlotReference.process();
            check(graph.output == lowSlotReference.output, "Highest slot preserves existing low-slot bypass/power behavior");
        }
    }
    std::cout << "Engine storage: " << sizeof(RealtimeEngine) << " bytes; DSP storage: " << sizeof(DspPerformance)
        << "; widget level storage: " << sizeof(PluginLevels) << ". Audio scratch is allocated for connected pins only.\n";
}

void realHostChecks()
{
    const auto folder = juce::File::getSpecialLocation(juce::File::currentExecutableFile).getParentDirectory();
    const auto pluginFile = folder.getChildFile("CapacityProbeVst.dll");
    auto module = LoadLibraryW(pluginFile.getFullPathName().toWideCharPointer());
    check(module != nullptr, "Synthetic VST2 module loaded");
    auto live = reinterpret_cast<int(*)()>(GetProcAddress(module, "ProbeLive"));
    auto calls = reinterpret_cast<int(*)()>(GetProcAddress(module, "ProbeCalls"));
    check(live && calls, "Test VST counters available");
    juce::VSTPluginFormat format;
    juce::OwnedArray<juce::PluginDescription> descriptions;
    format.findAllTypesForFile(descriptions, pluginFile.getFullPathName());
    check(descriptions.size() == 1, "Real JUCE VST scan resolves synthetic module");
    juce::XmlElement cache("ELKA_PLUGIN_CACHE"); cache.addChildElement(descriptions[0]->createXml().release());
    const auto cacheFile = folder.getChildFile("capacity-test-data/ElkaSoft/VoiceMeeterFxHost/plugin-cache.xml");
    cacheFile.getParentDirectory().createDirectory();
    check(cache.writeTo(cacheFile), "Test-only plugin cache written");
    {
        PluginHostLayer host;
        check(host.plugins().size() == 1, "Production host reads only isolated synthetic plugin cache");
        auto add = [&] { return host.addDiscoveredPluginNode(0, 48000, Frames, 2, 0, 2, 1, "Stereo", 1, "Stereo", 0, 0, 2); };
        for (int slot = 0; slot < Capacity; ++slot) check(add() == slot, "Native host creates each actual VST instance up to capacity");
        check(live() == Capacity && host.pluginNodes().size() == Capacity, "All VST instances really exist simultaneously");
        check(add() == -1 && live() == Capacity, "One beyond capacity rejected without removing a VST");
        Graph graph(0);
        for (int slot = 0; slot < Capacity; ++slot)
            graph.install(slot, host.realtimeProcessorForSlot(slot), slot == Capacity - 1 ? -1 : slot + 1, slot == 0 ? -1 : slot - 1);
        const int before = calls(); graph.process();
        check(calls() == before + Capacity && graph.input == graph.output, "All actual VST instances process in order with unchanged audio");
        graph.engine->clearPluginSlots();
        host.removePluginNode(Capacity - 1);
        check(live() == Capacity - 1 && add() == Capacity - 1 && live() == Capacity, "Remove/recreate highest real slot works");
        check(!host.isPluginNodeFailed(Capacity - 1), "Highest real slot failure query works");
        host.clearPluginNodes(); check(live() == 0, "All actual instances released");
    }
    FreeLibrary(module);
    std::cout << Capacity << " actual synthetic VST2 instances loaded, processed and released through JUCE and PluginHostLayer.\n";
}
}
int main()
{
    juce::ScopedJuceInitialiser_GUI juce;
    try
    {
        engineChecks(); realHostChecks();
        std::cout << "PASS: " << checks << " plugin capacity checks. No VoiceMeeter connection or audio device.\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << "FAIL: " << error.what() << '\n'; return 1; }
}
