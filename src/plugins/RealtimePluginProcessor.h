#pragma once

#include "engine/AudioBufferView.h"
#include <atomic>
#include <cstdint>

namespace elka
{
enum class PluginRouteEndpointKind
{
    VoiceMeeterChannel = 0,
    PluginPin = 1
};

struct PluginInputRoute
{
    int sourceKind = static_cast<int>(PluginRouteEndpointKind::VoiceMeeterChannel);
    int sourceChannel = -1;
    int sourceSlot = -1;
    int sourcePin = -1;
    int pluginPin = -1;
};

struct PluginOutputRoute
{
    int destinationKind = static_cast<int>(PluginRouteEndpointKind::VoiceMeeterChannel);
    int pluginPin = -1;
    int destinationChannel = -1;
    int destinationSlot = -1;
    int destinationPin = -1;
};

struct PluginAudioInputRoute
{
    const float* source = nullptr;
    int pluginPin = -1;
};

struct PluginAudioOutputRoute
{
    float* destination = nullptr;
    int pluginPin = -1;
    int destinationChannel = -1;
    int destinationSlot = -1;
    int destinationPin = -1;
    bool clearDestination = true;
};

struct PluginRoutingView
{
    const PluginAudioInputRoute* inputRoutes = nullptr;
    int inputRouteCount = 0;
    const PluginAudioOutputRoute* outputRoutes = nullptr;
    int outputRouteCount = 0;
};

class RealtimePluginProcessor
{
public:
    virtual ~RealtimePluginProcessor() = default;

    const uint64_t performanceToken = nextPerformanceToken.fetch_add(1, std::memory_order_relaxed);
    virtual bool usesWorkerProcess() const noexcept { return false; }

    virtual bool process(AudioBufferView buffer, const PluginRoutingView& routing) noexcept = 0;
private:
    inline static std::atomic<uint64_t> nextPerformanceToken { 1 };
};
}
