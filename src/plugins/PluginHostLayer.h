#pragma once

#include <array>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

#include "plugins/RealtimePluginProcessor.h"

namespace elka
{
static constexpr int MaxPluginNodeRoutes = 64;

using PluginScanProgressCallback = std::function<void(const std::string& stage, const std::string& path, int current, int total)>;
using PluginLoadProgressCallback = std::function<void(const std::string& stage, const std::string& detail)>;

bool probePluginFile(const std::string& format, const std::string& fileOrIdentifier, int sampleRate, int blockSize, std::string& error, PluginLoadProgressCallback progress = {});
int createWorkerPluginProcessor(const std::string& format, const std::string& fileOrIdentifier, int sampleRate, int blockSize, int inputPins, int outputPins, int inputLayoutId, int outputLayoutId, std::string& error);
bool processWorkerPluginProcessor(int handle, float* planarData, int channelCount, int samples);
bool openWorkerPluginEditor(int handle, std::string& error);
std::string workerPluginStateBase64(int handle, std::string& error);
bool setWorkerPluginStateBase64(int handle, const std::string& stateBase64, std::string& error);
std::string workerPluginPresetBase64(int handle, std::string& error);
bool setWorkerPluginPresetBase64(int handle, const std::string& presetBase64, std::string& error);
std::string workerPluginParameterStateBase64(int handle, std::string& error);
bool setWorkerPluginParameterStateBase64(int handle, const std::string& parameterStateBase64, std::string& error);
void pollWorkerPluginMessages(int milliseconds);
void destroyWorkerPluginProcessor(int handle);

struct PluginSummary
{
    std::string name;
    std::string manufacturer;
    std::string format;
    std::string category;
    std::string fileOrIdentifier;
    int inputChannels = 0;
    int outputChannels = 0;
    bool isInstrument = false;
};

struct PluginRouteSummary
{
    int from = -1;
    int to = -1;
};

struct PluginModuleRouteSummary
{
    int fromSlot = -1;
    int fromPin = -1;
    int toSlot = -1;
    int toPin = -1;
};

struct PluginNodeSummary
{
    int slot = -1;
    std::string name;
    int kind = 0;
    bool bypassed = false;
    int inputPins = 2;
    int mainInputPins = 2;
    int sidechainInputPins = 0;
    int outputPins = 2;
    int layoutId = 1;
    std::string layoutName = "Stereo";
    int inputLayoutId = 1;
    std::string inputLayoutName = "Stereo";
    int outputLayoutId = 1;
    std::string outputLayoutName = "Stereo";
    std::string supportedInputLayouts = "1:Stereo:2";
    std::string supportedOutputLayouts = "1:Stereo:2";
    int sourceStart = 0;
    int sourceCount = 2;
    int x = 250;
    int y = 26;
    std::array<PluginRouteSummary, MaxPluginNodeRoutes> inputRoutes {};
    int inputRouteCount = 0;
    std::array<PluginRouteSummary, MaxPluginNodeRoutes> outputRoutes {};
    int outputRouteCount = 0;
    std::array<PluginModuleRouteSummary, MaxPluginNodeRoutes> moduleRoutes {};
    int moduleRouteCount = 0;
};

class PluginHostLayer
{
public:
    static constexpr int MaxPluginNodes = 16;

    PluginHostLayer();
    ~PluginHostLayer();

    PluginHostLayer(const PluginHostLayer&) = delete;
    PluginHostLayer& operator=(const PluginHostLayer&) = delete;

    bool isAvailable() const noexcept;
    std::string backendName() const;
    std::vector<std::string> defaultVst3SearchPaths() const;
    std::vector<std::string> defaultVst2SearchPaths() const;
    std::vector<std::string> defaultPluginSearchPaths() const;
    std::vector<std::string> defaultPluginSearchPaths(int formatFlags) const;
    int scanDefaultVst3Locations();
    int scanDefaultPluginLocations();
    int scanVst3Folder(const std::string& folder);
    int scanPluginPaths(const std::vector<std::string>& paths, bool append);
    int scanPluginPaths(const std::vector<std::string>& paths, bool append, int formatFlags);
    int scanPluginPaths(const std::vector<std::string>& paths, bool append, int formatFlags, PluginScanProgressCallback progress);
    bool loadDiscoveredPlugin(size_t index, int sampleRate, int maxBlockSize, int routeChannelCount);
    void unloadPlugin() noexcept;
    int addDiscoveredPluginNode(size_t index, int sampleRate, int maxBlockSize, int mainInputPins, int sidechainInputPins, int outputPins, int inputLayoutId, const std::string& inputLayoutName, int outputLayoutId, const std::string& outputLayoutName, int kind, int sourceStart, int sourceCount, const std::string& initialStateBase64 = {}, const std::string& initialPresetBase64 = {}, PluginLoadProgressCallback progress = {});
    int addSandboxedDiscoveredPluginNode(size_t index, int sampleRate, int maxBlockSize, int mainInputPins, int sidechainInputPins, int outputPins, int inputLayoutId, const std::string& inputLayoutName, int outputLayoutId, const std::string& outputLayoutName, int kind, int sourceStart, int sourceCount, const std::string& initialStateBase64 = {}, const std::string& initialPresetBase64 = {}, PluginLoadProgressCallback progress = {});
    void removePluginNode(int slot) noexcept;
    void clearPluginNodes() noexcept;
    bool openPluginEditor(int slot);
    void closePluginEditor(int slot) noexcept;
    std::string pluginNodeStateBase64(int slot);
    bool setPluginNodeStateBase64(int slot, const std::string& stateBase64);
    std::string pluginNodePresetBase64(int slot);
    bool setPluginNodePresetBase64(int slot, const std::string& presetBase64);
    std::string pluginNodeParameterStateBase64(int slot);
    bool setPluginNodeParameterStateBase64(int slot, const std::string& parameterStateBase64);
    bool togglePluginNodeInputRoute(int slot, int sourceChannel, int pluginPin) noexcept;
    bool togglePluginNodeOutputRoute(int slot, int pluginPin, int destinationChannel) noexcept;
    bool togglePluginNodeModuleRoute(int sourceSlot, int sourcePin, int destinationSlot, int destinationPin) noexcept;
    void setPluginNodePosition(int slot, int x, int y) noexcept;
    std::array<PluginInputRoute, MaxPluginNodeRoutes> pluginNodeInputRoutes(int slot, int& count) const noexcept;
    std::array<PluginOutputRoute, MaxPluginNodeRoutes> pluginNodeOutputRoutes(int slot, int& count) const noexcept;
    void setPluginNodeBypassed(int slot, bool bypassed) noexcept;
    bool isPluginNodeBypassed(int slot) const noexcept;
    RealtimePluginProcessor* realtimeProcessor() noexcept;
    RealtimePluginProcessor* realtimeProcessorForSlot(int slot) noexcept;
    std::string loadedPluginName() const;
    std::vector<PluginNodeSummary> pluginNodes() const;
    const std::vector<PluginSummary>& plugins() const noexcept;
    const std::string& lastError() const noexcept;
    const std::string& lastScanReport() const noexcept;

private:
    struct Impl;

    void loadCachedPlugins();
    void saveCachedPlugins() const;
    bool resolvePluginDescription(size_t index);

    std::unique_ptr<Impl> impl;
    std::vector<PluginSummary> discoveredPlugins;
    std::string error;
    std::string scanReport;
};
}
