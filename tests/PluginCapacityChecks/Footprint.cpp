#include "engine/RealtimeEngine.h"
#include "plugins/PluginHostLayer.h"
#include <iostream>
int main()
{
    static_assert(elka::HostedPluginCapacity == 16);
    std::cout << "16-slot reference storage: engine=" << sizeof(elka::RealtimeEngine)
        << ", DSP=" << sizeof(elka::DspPerformance) << ", widget=" << sizeof(elka::PluginLevels)
        << ", per-slot host summary=" << sizeof(elka::PluginNodeSummary) << " bytes.\n";
}
