#include "plugins/PluginHostLayer.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdlib>
#include <filesystem>
#include <memory>
#include <set>
#include <sstream>
#include <stdexcept>
#include <windows.h>

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
#include <juce_audio_processors/juce_audio_processors.h>
#endif

namespace elka
{
namespace
{
constexpr int ScanFormatVst3 = 1;
constexpr int ScanFormatVst2 = 2;
constexpr int ScanFormatAll = ScanFormatVst3 | ScanFormatVst2;

std::string toUtf8(const std::wstring& value)
{
    if (value.empty())
        return {};

    const int bytes = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (bytes <= 0)
        return {};

    std::string result(static_cast<size_t>(bytes - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, result.data(), bytes, nullptr, nullptr);
    return result;
}

std::wstring envVar(const wchar_t* name)
{
    const DWORD length = GetEnvironmentVariableW(name, nullptr, 0);
    if (length == 0)
        return {};

    std::wstring value(length, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), length);
    if (written == 0)
        return {};

    value.resize(written);
    return value;
}

void addIfDirectoryExists(std::vector<std::string>& paths, const std::wstring& path)
{
    if (path.empty())
        return;

    if (std::filesystem::is_directory(path))
        paths.push_back(toUtf8(path));
}

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
juce::File pluginCacheFile()
{
    return juce::File::getSpecialLocation(juce::File::userApplicationDataDirectory)
        .getChildFile("Elka")
        .getChildFile("VoiceMeeterFxHost")
        .getChildFile("plugin-cache.xml");
}

juce::File legacyPluginCacheFile()
{
    return juce::File::getSpecialLocation(juce::File::userApplicationDataDirectory)
        .getChildFile("Elka")
        .getChildFile("VoiceMeeterFxHost")
        .getChildFile("vst3-cache.xml");
}

bool pluginFileExists(const juce::PluginDescription& description)
{
    return juce::File(description.fileOrIdentifier).exists();
}

bool pluginIsInAnyPath(const juce::PluginDescription& description, const std::vector<std::string>& paths)
{
    const juce::File pluginFile(description.fileOrIdentifier);

    for (const auto& path : paths)
    {
        const juce::File folder(juce::String::fromUTF8(path.c_str()));
        if (pluginFile == folder || pluginFile.isAChildOf(folder))
            return true;
    }

    return false;
}

PluginSummary toSummary(const juce::PluginDescription& description)
{
    return PluginSummary {
        description.name.toStdString(),
        description.manufacturerName.toStdString(),
        description.pluginFormatName.toStdString(),
        description.category.toStdString(),
        description.fileOrIdentifier.toStdString(),
        description.numInputChannels,
        description.numOutputChannels,
        description.isInstrument
    };
}

std::string pluginKey(const PluginSummary& summary)
{
    return summary.format + "|" + summary.name + "|" + summary.fileOrIdentifier;
}

std::string pluginKey(const juce::PluginDescription& description)
{
    return description.pluginFormatName.toStdString() + "|" +
           description.name.toStdString() + "|" +
           description.fileOrIdentifier.toStdString();
}

void scanFormatIntoList(
    juce::AudioPluginFormat& format,
    const std::vector<std::string>& paths,
    std::set<std::string>& seen,
    std::vector<PluginSummary>& summaries,
    std::vector<juce::PluginDescription>& descriptions,
    std::ostringstream& report)
{
    for (const auto& path : paths)
    {
        if (!std::filesystem::is_directory(std::filesystem::path(path)))
        {
            report << "Skip missing folder: " << path << "\n";
            continue;
        }

        report << "Scanning " << format.getName().toStdString() << ": " << path << "\n";
        juce::FileSearchPath searchPath(juce::String::fromUTF8(path.c_str()));

        juce::StringArray candidates;
        try
        {
            candidates = format.searchPathsForPlugins(searchPath, true, false);
        }
        catch (...)
        {
            report << "  Could not enumerate this folder.\n";
            continue;
        }

        if (candidates.isEmpty())
            report << "  No plugin candidates found.\n";

        for (const auto& fileOrIdentifier : candidates)
        {
            report << "  Candidate: " << fileOrIdentifier.toStdString() << "\n";

            juce::OwnedArray<juce::PluginDescription> found;
            try
            {
                format.findAllTypesForFile(found, fileOrIdentifier);
            }
            catch (...)
            {
                report << "    Failed while reading plugin metadata.\n";
                continue;
            }

            if (found.isEmpty())
                report << "    No loadable plugin types reported.\n";

            for (const auto* description : found)
            {
                if (description == nullptr)
                    continue;

                const auto key = pluginKey(*description);
                if (!seen.insert(key).second)
                {
                    report << "    Duplicate skipped: " << description->name.toStdString() << "\n";
                    continue;
                }

                summaries.push_back(toSummary(*description));
                descriptions.push_back(*description);
                report << "    Added: " << description->name.toStdString();
                if (description->manufacturerName.isNotEmpty())
                    report << " - " << description->manufacturerName.toStdString();
                report << " [" << description->pluginFormatName.toStdString() << "]"
                       << " in " << description->numInputChannels
                       << " / out " << description->numOutputChannels << "\n";
            }
        }
    }
}

std::unique_ptr<juce::AudioPluginInstance> createPluginInstanceForDescription(
    const juce::PluginDescription& description,
    double sampleRate,
    int blockSize,
    juce::String& creationError)
{
    if (description.pluginFormatName.equalsIgnoreCase("VST3"))
    {
        juce::VST3PluginFormat vst3Format;
        return vst3Format.createInstanceFromDescription(description, sampleRate, blockSize, creationError);
    }

#if ELKA_ENABLE_VST2_HOST && JUCE_INTERNAL_HAS_VST
    if (description.pluginFormatName.equalsIgnoreCase("VST"))
    {
        juce::VSTPluginFormat vstFormat;
        return vstFormat.createInstanceFromDescription(description, sampleRate, blockSize, creationError);
    }
#else
    if (description.pluginFormatName.equalsIgnoreCase("VST"))
    {
        creationError = "VST2 hosting is not enabled. Configure ELKA_VST2_SDK_PATH and rebuild.";
        return nullptr;
    }
#endif

    creationError = "Unsupported plugin format: " + description.pluginFormatName;
    return nullptr;
}

int clampPluginChannels(int requested, const juce::PluginDescription& description)
{
    const int describedChannels = std::max(description.numInputChannels, description.numOutputChannels);
    const int preferredChannels = describedChannels > 0 ? std::min(requested, describedChannels) : requested;
    return std::clamp(preferredChannels, 1, 32);
}

juce::AudioChannelSet layoutForId(int layoutId, int fallbackChannels)
{
    switch (layoutId)
    {
    case 0: return juce::AudioChannelSet::mono();
    case 1: return juce::AudioChannelSet::stereo();
    case 2: return juce::AudioChannelSet::quadraphonic();
    case 3: return juce::AudioChannelSet::create5point1();
    case 4: return juce::AudioChannelSet::create7point1();
    case 5: return juce::AudioChannelSet::create7point1point4();
    case 6: return juce::AudioChannelSet::discreteChannels(24);
    default: return juce::AudioChannelSet::discreteChannels(std::clamp(fallbackChannels, 1, 32));
    }
}

float sanitizePluginSample(float sample) noexcept
{
    if (!std::isfinite(sample))
        return 0.0f;

    return std::clamp(sample, -8.0f, 8.0f);
}

class HostedVst3Processor final : public RealtimePluginProcessor
{
public:
    HostedVst3Processor(std::unique_ptr<juce::AudioPluginInstance> pluginInstance, int inputPins, int outputPins, int blockSize)
        : plugin(std::move(pluginInstance)),
          inputPinCount(std::clamp(inputPins, 1, 32)),
          outputPinCount(std::clamp(outputPins, 1, 32)),
          processChannels(std::max(inputPinCount, outputPinCount)),
          maxBlockSize(std::max(1, blockSize))
    {
        scratch.setSize(processChannels, maxBlockSize, false, true, true);
        scratch.clear();
    }

    ~HostedVst3Processor() override
    {
        if (plugin != nullptr)
            plugin->releaseResources();
    }

    bool process(AudioBufferView buffer, const PluginRoutingView& routing) noexcept override
    {
        if (failed)
            return false;

        if (plugin == nullptr || buffer.write == nullptr || buffer.samplesPerFrame <= 0)
            return false;

        if (buffer.samplesPerFrame > maxBlockSize)
            return false;

        std::array<float*, 32> processPointers {};
        for (int ch = 0; ch < processChannels; ++ch)
        {
            auto* destination = scratch.getWritePointer(ch);
            processPointers[static_cast<size_t>(ch)] = destination;
            std::fill_n(destination, buffer.samplesPerFrame, 0.0f);
        }

        for (int route = 0; route < routing.inputRouteCount; ++route)
        {
            const auto& inputRoute = routing.inputRoutes[route];
            if (inputRoute.pluginPin < 0 || inputRoute.pluginPin >= inputPinCount)
                continue;

            const auto* source = inputRoute.source;
            auto* destination = scratch.getWritePointer(inputRoute.pluginPin);
            if (source == nullptr || destination == nullptr)
                continue;

            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
            {
                const float mixed = destination[sample] + source[sample];
                destination[sample] = sanitizePluginSample(mixed);
            }
        }

        midi.clear();
        juce::AudioBuffer<float> block(processPointers.data(), processChannels, buffer.samplesPerFrame);

        try
        {
            plugin->processBlock(block, midi);
        }
        catch (...)
        {
            failed = true;
            return false;
        }

        std::array<float*, 64> clearedDestinations {};
        int clearedCount = 0;

        for (int route = 0; route < routing.outputRouteCount; ++route)
        {
            const auto& outputRoute = routing.outputRoutes[route];
            if (outputRoute.pluginPin < 0 || outputRoute.pluginPin >= outputPinCount)
                continue;

            auto* destination = outputRoute.destination;
            if (destination == nullptr)
                continue;

            bool alreadyCleared = false;
            for (int i = 0; i < clearedCount; ++i)
                alreadyCleared = alreadyCleared || clearedDestinations[static_cast<size_t>(i)] == destination;

            if (!alreadyCleared)
            {
                std::fill_n(destination, buffer.samplesPerFrame, 0.0f);
                if (clearedCount < static_cast<int>(clearedDestinations.size()))
                    clearedDestinations[static_cast<size_t>(clearedCount++)] = destination;
            }

            const auto* source = scratch.getReadPointer(outputRoute.pluginPin);
            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                destination[sample] = sanitizePluginSample(destination[sample] + source[sample]);
        }

        return true;
    }

    bool hasFailed() const noexcept { return failed; }
    juce::AudioPluginInstance* pluginInstance() noexcept { return plugin.get(); }
    int channelCount() const noexcept { return processChannels; }

private:
    std::unique_ptr<juce::AudioPluginInstance> plugin;
    juce::AudioBuffer<float> scratch;
    juce::MidiBuffer midi;
    int inputPinCount = 2;
    int outputPinCount = 2;
    int processChannels = 2;
    int maxBlockSize = 4096;
    bool failed = false;
};

class PluginEditorWindow final : public juce::DocumentWindow
{
public:
    PluginEditorWindow(juce::AudioPluginInstance& plugin, const juce::String& title)
        : juce::DocumentWindow(
              title,
              juce::Colours::black,
              juce::DocumentWindow::closeButton | juce::DocumentWindow::minimiseButton,
              true)
    {
        auto* editor = plugin.createEditorAndMakeActive();
        if (editor == nullptr)
            throw std::runtime_error("The loaded plugin did not provide an editor window.");

        setUsingNativeTitleBar(true);
        setResizable(true, false);
        setContentOwned(editor, true);

        const int width = std::max(420, editor->getWidth());
        const int height = std::max(280, editor->getHeight());
        centreWithSize(width, height);
        setVisible(true);
        toFront(true);
    }

    void closeButtonPressed() override
    {
        setVisible(false);
    }
};
#endif
}

struct PluginHostLayer::Impl
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    juce::ScopedJuceInitialiser_GUI juceInitialiser;
    std::vector<juce::PluginDescription> descriptions;
    std::unique_ptr<HostedVst3Processor> activeProcessor;
    std::unique_ptr<PluginEditorWindow> editorWindow;
    std::array<std::unique_ptr<HostedVst3Processor>, PluginHostLayer::MaxPluginNodes> nodeProcessors {};
    std::array<std::unique_ptr<PluginEditorWindow>, PluginHostLayer::MaxPluginNodes> nodeEditors {};
    std::array<PluginNodeSummary, PluginHostLayer::MaxPluginNodes> nodeSummaries {};
    std::string loadedName;
#endif
};

PluginHostLayer::PluginHostLayer()
    : impl(std::make_unique<Impl>())
{
    loadCachedPlugins();
}

PluginHostLayer::~PluginHostLayer() = default;

bool PluginHostLayer::isAvailable() const noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    return true;
#else
    return false;
#endif
}

std::string PluginHostLayer::backendName() const
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
  #if ELKA_ENABLE_VST2_HOST
    return "JUCE VST3/VST2";
  #else
    return "JUCE VST3";
  #endif
#else
    return "Unavailable";
#endif
}

std::vector<std::string> PluginHostLayer::defaultVst3SearchPaths() const
{
    std::vector<std::string> paths;

    addIfDirectoryExists(paths, envVar(L"CommonProgramFiles") + L"\\VST3");
    addIfDirectoryExists(paths, envVar(L"ProgramFiles") + L"\\Common Files\\VST3");
    addIfDirectoryExists(paths, envVar(L"ProgramFiles(x86)") + L"\\Common Files\\VST3");
    addIfDirectoryExists(paths, envVar(L"LOCALAPPDATA") + L"\\Programs\\Common\\VST3");

    std::sort(paths.begin(), paths.end());
    paths.erase(std::unique(paths.begin(), paths.end()), paths.end());
    return paths;
}

std::vector<std::string> PluginHostLayer::defaultVst2SearchPaths() const
{
    std::vector<std::string> paths;

#if ELKA_ENABLE_JUCE_PLUGIN_HOST && ELKA_ENABLE_VST2_HOST
    addIfDirectoryExists(paths, envVar(L"ProgramFiles") + L"\\Steinberg\\VstPlugins");
    addIfDirectoryExists(paths, envVar(L"ProgramFiles") + L"\\VstPlugins");
    addIfDirectoryExists(paths, envVar(L"ProgramFiles") + L"\\Common Files\\VST2");
    addIfDirectoryExists(paths, envVar(L"ProgramFiles(x86)") + L"\\Steinberg\\VstPlugins");
    addIfDirectoryExists(paths, envVar(L"ProgramFiles(x86)") + L"\\VstPlugins");
    addIfDirectoryExists(paths, envVar(L"ProgramFiles(x86)") + L"\\Common Files\\VST2");
#endif

    std::sort(paths.begin(), paths.end());
    paths.erase(std::unique(paths.begin(), paths.end()), paths.end());
    return paths;
}

std::vector<std::string> PluginHostLayer::defaultPluginSearchPaths() const
{
    return defaultPluginSearchPaths(ScanFormatAll);
}

std::vector<std::string> PluginHostLayer::defaultPluginSearchPaths(int formatFlags) const
{
    std::vector<std::string> paths;

    if ((formatFlags & ScanFormatVst3) != 0)
    {
        auto vst3Paths = defaultVst3SearchPaths();
        paths.insert(paths.end(), vst3Paths.begin(), vst3Paths.end());
    }

    if ((formatFlags & ScanFormatVst2) != 0)
    {
        auto vst2Paths = defaultVst2SearchPaths();
        paths.insert(paths.end(), vst2Paths.begin(), vst2Paths.end());
    }

    std::sort(paths.begin(), paths.end());
    paths.erase(std::unique(paths.begin(), paths.end()), paths.end());
    return paths;
}

int PluginHostLayer::scanDefaultVst3Locations()
{
    return scanPluginPaths(defaultVst3SearchPaths(), false);
}

int PluginHostLayer::scanDefaultPluginLocations()
{
    return scanPluginPaths(defaultPluginSearchPaths(), false);
}

int PluginHostLayer::scanVst3Folder(const std::string& folder)
{
    if (folder.empty())
    {
        error = "No folder selected.";
        return static_cast<int>(discoveredPlugins.size());
    }

    return scanPluginPaths(std::vector<std::string> { folder }, true);
}

int PluginHostLayer::scanPluginPaths(const std::vector<std::string>& paths, bool append)
{
    return scanPluginPaths(paths, append, ScanFormatAll);
}

int PluginHostLayer::scanPluginPaths(const std::vector<std::string>& paths, bool append, int formatFlags)
{
    error.clear();
    scanReport.clear();

    std::ostringstream report;
    report << "Plugin scan started.\n";
    const bool scanVst3 = (formatFlags & ScanFormatVst3) != 0;
    const bool scanVst2 = (formatFlags & ScanFormatVst2) != 0;

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
  #if ELKA_ENABLE_VST2_HOST
    report << "Backend: JUCE VST3/VST2\n";
  #else
    report << "Backend: JUCE VST3 only\n";
    report << "VST2 disabled in this build. Legacy .dll plugins will not appear until ELKA_VST2_SDK_PATH is configured and the app is rebuilt.\n";
  #endif
#else
    report << "Backend: unavailable\n";
#endif

    report << "Selected formats:";
    if (scanVst3)
        report << " VST3";
    if (scanVst2)
        report << " VST2";
    if (!scanVst3 && !scanVst2)
        report << " none";
    report << "\n";

    if (paths.empty())
    {
        error = "No plugin folders were found to scan.";
        report << error << "\n";
        scanReport = report.str();
        return static_cast<int>(discoveredPlugins.size());
    }

    report << "Folders:\n";
    for (const auto& path : paths)
        report << "  " << path << "\n";

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::vector<PluginSummary> keptSummaries;
    std::vector<juce::PluginDescription> keptDescriptions;

    if (append)
    {
        for (size_t index = 0; index < impl->descriptions.size(); ++index)
        {
            const auto& description = impl->descriptions[index];
            if (pluginIsInAnyPath(description, paths))
                continue;

            if (!pluginFileExists(description))
                continue;

            keptDescriptions.push_back(description);
            keptSummaries.push_back(toSummary(description));
        }
    }

    impl->descriptions = std::move(keptDescriptions);
    discoveredPlugins = std::move(keptSummaries);

    juce::VST3PluginFormatHeadless vst3Format;
    std::set<std::string> seen;

    for (const auto& existing : discoveredPlugins)
        seen.insert(pluginKey(existing));

    if (scanVst3)
        scanFormatIntoList(vst3Format, paths, seen, discoveredPlugins, impl->descriptions, report);

#if ELKA_ENABLE_VST2_HOST && JUCE_INTERNAL_HAS_VST
    if (scanVst2)
    {
        juce::VSTPluginFormatHeadless vstFormat;
        scanFormatIntoList(vstFormat, paths, seen, discoveredPlugins, impl->descriptions, report);
    }
#endif

    saveCachedPlugins();

    report << "Plugin scan complete: " << discoveredPlugins.size() << " plugin(s).\n";
    scanReport = report.str();
    return static_cast<int>(discoveredPlugins.size());
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    report << error << "\n";
    scanReport = report.str();
    return 0;
#endif
}

void PluginHostLayer::loadCachedPlugins()
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    discoveredPlugins.clear();
    impl->descriptions.clear();

    auto file = pluginCacheFile();
    if (!file.existsAsFile())
        file = legacyPluginCacheFile();

    if (!file.existsAsFile())
        return;

    auto root = juce::XmlDocument::parse(file);
    if (root == nullptr || (!root->hasTagName("ELKA_PLUGIN_CACHE") && !root->hasTagName("ELKA_VST3_CACHE")))
        return;

    std::set<std::string> seen;
    for (auto* child = root->getFirstChildElement(); child != nullptr; child = child->getNextElement())
    {
        juce::PluginDescription description;
        if (!description.loadFromXml(*child))
            continue;

        if (!pluginFileExists(description))
            continue;

        const auto summary = toSummary(description);
        const auto key = pluginKey(summary);
        if (!seen.insert(key).second)
            continue;

        impl->descriptions.push_back(description);
        discoveredPlugins.push_back(summary);
    }
#endif
}

void PluginHostLayer::saveCachedPlugins() const
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    auto file = pluginCacheFile();
    file.getParentDirectory().createDirectory();

    juce::XmlElement root("ELKA_PLUGIN_CACHE");
    for (const auto& description : impl->descriptions)
    {
        auto xml = description.createXml();
        if (xml != nullptr)
            root.addChildElement(xml.release());
    }

    root.writeTo(file);
#endif
}

bool PluginHostLayer::loadDiscoveredPlugin(size_t index, int sampleRate, int maxBlockSize, int routeChannelCount)
{
    error.clear();

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (index >= impl->descriptions.size())
    {
        error = "No plugin selected.";
        return false;
    }

    const auto& description = impl->descriptions[index];
    const int preparedSampleRate = std::clamp(sampleRate, 8000, 192000);
    const int preparedBlockSize = std::clamp(maxBlockSize, 64, 8192);
    const int channels = clampPluginChannels(routeChannelCount, description);

    impl->editorWindow.reset();

    juce::String creationError;
    auto instance = createPluginInstanceForDescription(
        description,
        static_cast<double>(preparedSampleRate),
        preparedBlockSize,
        creationError);

    if (instance == nullptr)
    {
        error = creationError.isNotEmpty()
            ? creationError.toStdString()
            : "JUCE could not create the selected plugin instance.";
        return false;
    }

    try
    {
        instance->setPlayConfigDetails(channels, channels, static_cast<double>(preparedSampleRate), preparedBlockSize);
        instance->setRateAndBufferSizeDetails(static_cast<double>(preparedSampleRate), preparedBlockSize);
        instance->prepareToPlay(static_cast<double>(preparedSampleRate), preparedBlockSize);
        impl->activeProcessor = std::make_unique<HostedVst3Processor>(std::move(instance), channels, channels, preparedBlockSize);
        impl->loadedName = discoveredPlugins[index].name;
    }
    catch (const std::exception& ex)
    {
        error = ex.what();
        impl->activeProcessor.reset();
        impl->loadedName.clear();
        return false;
    }
    catch (...)
    {
        error = "The selected plugin failed while preparing.";
        impl->activeProcessor.reset();
        impl->loadedName.clear();
        return false;
    }

    return true;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return false;
#endif
}

void PluginHostLayer::unloadPlugin() noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    impl->editorWindow.reset();
    impl->activeProcessor.reset();
    impl->loadedName.clear();
#endif
}

int PluginHostLayer::addDiscoveredPluginNode(
    size_t index,
    int sampleRate,
    int maxBlockSize,
    int mainInputPins,
    int sidechainInputPins,
    int outputPins,
    int layoutId,
    const std::string& layoutName,
    int kind,
    int sourceStart,
    int sourceCount)
{
    error.clear();

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (index >= impl->descriptions.size())
    {
        error = "No plugin selected.";
        return -1;
    }

    int slot = -1;
    for (int candidate = 0; candidate < MaxPluginNodes; ++candidate)
    {
        if (impl->nodeProcessors[static_cast<size_t>(candidate)] == nullptr)
        {
            slot = candidate;
            break;
        }
    }

    if (slot < 0)
    {
        error = "The prototype node graph is full.";
        return -1;
    }

    const auto& description = impl->descriptions[index];
    const int preparedSampleRate = std::clamp(sampleRate, 8000, 192000);
    const int preparedBlockSize = std::clamp(maxBlockSize, 64, 8192);
    const int requestedMainInputPins = std::clamp(mainInputPins, 1, 32);
    const int requestedSidechainPins = std::clamp(sidechainInputPins, 0, 32 - requestedMainInputPins);
    const int requestedOutputPins = std::clamp(outputPins, 1, 32);

    juce::String creationError;
    auto instance = createPluginInstanceForDescription(
        description,
        static_cast<double>(preparedSampleRate),
        preparedBlockSize,
        creationError);

    if (instance == nullptr)
    {
        error = creationError.isNotEmpty()
            ? creationError.toStdString()
            : "JUCE could not create the selected plugin instance.";
        return -1;
    }

    try
    {
        const auto inputLayout = layoutForId(layoutId, requestedMainInputPins);
        const int sidechainLayoutId = requestedSidechainPins == 1 ? 0 : requestedSidechainPins == 2 ? 1 : 99;
        const auto sidechainLayout = layoutForId(sidechainLayoutId, requestedSidechainPins);
        const auto outputLayout = layoutForId(layoutId, requestedOutputPins);
        bool layoutAccepted = true;
        int effectiveSidechainPins = requestedSidechainPins;

        if (requestedSidechainPins > 0)
            layoutAccepted = instance->enableAllBuses() && layoutAccepted;
        else
            instance->disableNonMainBuses();

        if (instance->getBusCount(true) > 0)
            layoutAccepted = instance->setChannelLayoutOfBus(true, 0, inputLayout) && layoutAccepted;

        if (requestedSidechainPins > 0)
        {
            if (instance->getBusCount(true) > 1)
            {
                const bool sidechainAccepted = instance->setChannelLayoutOfBus(true, 1, sidechainLayout);
                layoutAccepted = sidechainAccepted && layoutAccepted;
                if (!sidechainAccepted)
                    effectiveSidechainPins = 0;
            }
            else
            {
                layoutAccepted = false;
                effectiveSidechainPins = 0;
            }
        }

        if (effectiveSidechainPins == 0 && requestedSidechainPins > 0)
            instance->disableNonMainBuses();

        if (instance->getBusCount(false) > 0)
            layoutAccepted = instance->setChannelLayoutOfBus(false, 0, outputLayout) && layoutAccepted;

        // setPlayConfigDetails() disables non-main buses in JUCE. Keep sidechain
        // buses alive by using the explicit bus layouts configured above.
        instance->setRateAndBufferSizeDetails(static_cast<double>(preparedSampleRate), preparedBlockSize);
        instance->prepareToPlay(static_cast<double>(preparedSampleRate), preparedBlockSize);
        const int effectiveInputPins = std::clamp(requestedMainInputPins + effectiveSidechainPins, 1, 32);

        const auto slotIndex = static_cast<size_t>(slot);
        impl->nodeEditors[slotIndex].reset();
        impl->nodeProcessors[slotIndex] =
            std::make_unique<HostedVst3Processor>(std::move(instance), effectiveInputPins, requestedOutputPins, preparedBlockSize);
        impl->nodeSummaries[slotIndex] = PluginNodeSummary {
            slot,
            layoutAccepted ? discoveredPlugins[index].name : discoveredPlugins[index].name + " (layout fallback)",
            kind,
            false,
            effectiveInputPins,
            requestedMainInputPins,
            effectiveSidechainPins,
            requestedOutputPins,
            layoutId,
            layoutName,
            std::max(0, sourceStart),
            std::clamp(sourceCount, 1, 64),
            250 + (slot * 28),
            26 + (slot * 10),
            {},
            0,
            {},
            0,
            {},
            0
        };
        impl->loadedName = discoveredPlugins[index].name;
    }
    catch (const std::exception& ex)
    {
        error = ex.what();
        removePluginNode(slot);
        return -1;
    }
    catch (...)
    {
        error = "The selected plugin failed while preparing.";
        removePluginNode(slot);
        return -1;
    }

    return slot;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return -1;
#endif
}

void PluginHostLayer::removePluginNode(int slot) noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
        return;

    for (auto& summary : impl->nodeSummaries)
    {
        if (summary.slot < 0)
            continue;

        for (int route = 0; route < summary.moduleRouteCount;)
        {
            const auto& moduleRoute = summary.moduleRoutes[static_cast<size_t>(route)];
            if (moduleRoute.fromSlot == slot || moduleRoute.toSlot == slot)
            {
                for (int move = route; move + 1 < summary.moduleRouteCount; ++move)
                    summary.moduleRoutes[static_cast<size_t>(move)] = summary.moduleRoutes[static_cast<size_t>(move + 1)];

                --summary.moduleRouteCount;
                continue;
            }

            ++route;
        }
    }

    const auto index = static_cast<size_t>(slot);
    impl->nodeEditors[index].reset();
    impl->nodeProcessors[index].reset();
    impl->nodeSummaries[index] = {};
#endif
}

void PluginHostLayer::clearPluginNodes() noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    for (int slot = 0; slot < MaxPluginNodes; ++slot)
        removePluginNode(slot);
#endif
}

bool PluginHostLayer::openPluginEditor(int slot)
{
    error.clear();

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
    {
        error = "No VST node is selected.";
        return false;
    }

    const auto index = static_cast<size_t>(slot);
    auto* processor = impl->nodeProcessors[index].get();
    if (processor == nullptr || processor->pluginInstance() == nullptr)
    {
        error = "No VST node is selected.";
        return false;
    }

    try
    {
        if (impl->nodeEditors[index] == nullptr)
        {
            impl->nodeEditors[index] = std::make_unique<PluginEditorWindow>(
                *processor->pluginInstance(),
                juce::String::fromUTF8(impl->nodeSummaries[index].name.c_str()));
        }
        else
        {
            impl->nodeEditors[index]->setVisible(true);
            impl->nodeEditors[index]->toFront(true);
        }
    }
    catch (const std::exception& ex)
    {
        error = ex.what();
        impl->nodeEditors[index].reset();
        return false;
    }
    catch (...)
    {
        error = "The plugin editor could not be opened.";
        impl->nodeEditors[index].reset();
        return false;
    }

    return true;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return false;
#endif
}

void PluginHostLayer::closePluginEditor(int slot) noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
        return;

    impl->nodeEditors[static_cast<size_t>(slot)].reset();
#endif
}

bool PluginHostLayer::togglePluginNodeInputRoute(int slot, int sourceChannel, int pluginPin) noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
        return false;

    auto& summary = impl->nodeSummaries[static_cast<size_t>(slot)];
    if (sourceChannel < 0 || pluginPin < 0 || pluginPin >= summary.inputPins)
        return false;

    for (int i = 0; i < summary.inputRouteCount; ++i)
    {
        auto& route = summary.inputRoutes[static_cast<size_t>(i)];
        if (route.from == sourceChannel && route.to == pluginPin)
        {
            for (int move = i; move + 1 < summary.inputRouteCount; ++move)
                summary.inputRoutes[static_cast<size_t>(move)] = summary.inputRoutes[static_cast<size_t>(move + 1)];

            --summary.inputRouteCount;
            return false;
        }
    }

    if (summary.inputRouteCount >= MaxPluginNodeRoutes)
        return false;

    summary.inputRoutes[static_cast<size_t>(summary.inputRouteCount++)] = PluginRouteSummary { sourceChannel, pluginPin };
    return true;
#else
    return false;
#endif
}

bool PluginHostLayer::togglePluginNodeOutputRoute(int slot, int pluginPin, int destinationChannel) noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
        return false;

    auto& summary = impl->nodeSummaries[static_cast<size_t>(slot)];
    if (destinationChannel < 0 || pluginPin < 0 || pluginPin >= summary.outputPins)
        return false;

    for (int i = 0; i < summary.outputRouteCount; ++i)
    {
        auto& route = summary.outputRoutes[static_cast<size_t>(i)];
        if (route.from == pluginPin && route.to == destinationChannel)
        {
            for (int move = i; move + 1 < summary.outputRouteCount; ++move)
                summary.outputRoutes[static_cast<size_t>(move)] = summary.outputRoutes[static_cast<size_t>(move + 1)];

            --summary.outputRouteCount;
            return false;
        }
    }

    if (summary.outputRouteCount >= MaxPluginNodeRoutes)
        return false;

    summary.outputRoutes[static_cast<size_t>(summary.outputRouteCount++)] = PluginRouteSummary { pluginPin, destinationChannel };
    return true;
#else
    return false;
#endif
}

bool PluginHostLayer::togglePluginNodeModuleRoute(int sourceSlot, int sourcePin, int destinationSlot, int destinationPin) noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (sourceSlot < 0 || sourceSlot >= MaxPluginNodes || destinationSlot < 0 || destinationSlot >= MaxPluginNodes)
        return false;

    if (sourceSlot == destinationSlot)
        return false;

    auto& source = impl->nodeSummaries[static_cast<size_t>(sourceSlot)];
    const auto& destination = impl->nodeSummaries[static_cast<size_t>(destinationSlot)];
    if (source.slot < 0 || destination.slot < 0)
        return false;

    if (source.kind != destination.kind)
        return false;

    if (sourcePin < 0 || sourcePin >= source.outputPins || destinationPin < 0 || destinationPin >= destination.inputPins)
        return false;

    for (int i = 0; i < source.moduleRouteCount; ++i)
    {
        auto& route = source.moduleRoutes[static_cast<size_t>(i)];
        if (route.fromSlot == sourceSlot &&
            route.fromPin == sourcePin &&
            route.toSlot == destinationSlot &&
            route.toPin == destinationPin)
        {
            for (int move = i; move + 1 < source.moduleRouteCount; ++move)
                source.moduleRoutes[static_cast<size_t>(move)] = source.moduleRoutes[static_cast<size_t>(move + 1)];

            --source.moduleRouteCount;
            return false;
        }
    }

    if (source.moduleRouteCount >= MaxPluginNodeRoutes)
        return false;

    std::array<bool, MaxPluginNodes> visiting {};
    const auto reaches = [&](auto&& self, int currentSlot, int targetSlot) noexcept -> bool {
        if (currentSlot == targetSlot)
            return true;

        if (currentSlot < 0 || currentSlot >= MaxPluginNodes)
            return false;

        if (visiting[static_cast<size_t>(currentSlot)])
            return false;

        visiting[static_cast<size_t>(currentSlot)] = true;
        const auto& current = impl->nodeSummaries[static_cast<size_t>(currentSlot)];
        for (int routeIndex = 0; routeIndex < current.moduleRouteCount; ++routeIndex)
        {
            const auto& route = current.moduleRoutes[static_cast<size_t>(routeIndex)];
            if (self(self, route.toSlot, targetSlot))
                return true;
        }

        visiting[static_cast<size_t>(currentSlot)] = false;
        return false;
    };

    if (reaches(reaches, destinationSlot, sourceSlot))
        return false;

    source.moduleRoutes[static_cast<size_t>(source.moduleRouteCount++)] =
        PluginModuleRouteSummary { sourceSlot, sourcePin, destinationSlot, destinationPin };
    return true;
#else
    return false;
#endif
}

void PluginHostLayer::setPluginNodePosition(int slot, int x, int y) noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
        return;

    auto& summary = impl->nodeSummaries[static_cast<size_t>(slot)];
    summary.x = x;
    summary.y = y;
#endif
}

std::array<PluginInputRoute, MaxPluginNodeRoutes> PluginHostLayer::pluginNodeInputRoutes(int slot, int& count) const noexcept
{
    std::array<PluginInputRoute, MaxPluginNodeRoutes> routes {};
    count = 0;

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
        return routes;

    const auto& summary = impl->nodeSummaries[static_cast<size_t>(slot)];
    count = std::clamp(summary.inputRouteCount, 0, MaxPluginNodeRoutes);
    for (int i = 0; i < count; ++i)
    {
        const auto& route = summary.inputRoutes[static_cast<size_t>(i)];
        routes[static_cast<size_t>(i)] = PluginInputRoute {
            static_cast<int>(PluginRouteEndpointKind::VoiceMeeterChannel),
            route.from,
            -1,
            -1,
            route.to
        };
    }

    for (int sourceSlot = 0; sourceSlot < MaxPluginNodes && count < MaxPluginNodeRoutes; ++sourceSlot)
    {
        const auto& source = impl->nodeSummaries[static_cast<size_t>(sourceSlot)];
        if (source.slot < 0)
            continue;

        for (int routeIndex = 0; routeIndex < source.moduleRouteCount && count < MaxPluginNodeRoutes; ++routeIndex)
        {
            const auto& route = source.moduleRoutes[static_cast<size_t>(routeIndex)];
            if (route.toSlot != slot)
                continue;

            routes[static_cast<size_t>(count++)] = PluginInputRoute {
                static_cast<int>(PluginRouteEndpointKind::PluginPin),
                -1,
                route.fromSlot,
                route.fromPin,
                route.toPin
            };
        }
    }
#endif

    return routes;
}

std::array<PluginOutputRoute, MaxPluginNodeRoutes> PluginHostLayer::pluginNodeOutputRoutes(int slot, int& count) const noexcept
{
    std::array<PluginOutputRoute, MaxPluginNodeRoutes> routes {};
    count = 0;

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
        return routes;

    const auto& summary = impl->nodeSummaries[static_cast<size_t>(slot)];
    count = std::clamp(summary.outputRouteCount, 0, MaxPluginNodeRoutes);
    for (int i = 0; i < count; ++i)
    {
        const auto& route = summary.outputRoutes[static_cast<size_t>(i)];
        routes[static_cast<size_t>(i)] = PluginOutputRoute {
            static_cast<int>(PluginRouteEndpointKind::VoiceMeeterChannel),
            route.from,
            route.to,
            -1,
            -1
        };
    }

    for (int i = 0; i < summary.moduleRouteCount && count < MaxPluginNodeRoutes; ++i)
    {
        const auto& route = summary.moduleRoutes[static_cast<size_t>(i)];
        routes[static_cast<size_t>(count++)] = PluginOutputRoute {
            static_cast<int>(PluginRouteEndpointKind::PluginPin),
            route.fromPin,
            -1,
            route.toSlot,
            route.toPin
        };
    }
#endif

    return routes;
}

void PluginHostLayer::setPluginNodeBypassed(int slot, bool bypassed) noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
        return;

    impl->nodeSummaries[static_cast<size_t>(slot)].bypassed = bypassed;
#endif
}

bool PluginHostLayer::isPluginNodeBypassed(int slot) const noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
        return false;

    return impl->nodeSummaries[static_cast<size_t>(slot)].bypassed;
#else
    return false;
#endif
}

RealtimePluginProcessor* PluginHostLayer::realtimeProcessor() noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    return impl->activeProcessor.get();
#else
    return nullptr;
#endif
}

RealtimePluginProcessor* PluginHostLayer::realtimeProcessorForSlot(int slot) noexcept
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
        return nullptr;

    return impl->nodeProcessors[static_cast<size_t>(slot)].get();
#else
    return nullptr;
#endif
}

std::string PluginHostLayer::loadedPluginName() const
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    return impl->loadedName;
#else
    return {};
#endif
}

std::vector<PluginNodeSummary> PluginHostLayer::pluginNodes() const
{
    std::vector<PluginNodeSummary> nodes;

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    for (int slot = 0; slot < MaxPluginNodes; ++slot)
    {
        const auto& summary = impl->nodeSummaries[static_cast<size_t>(slot)];
        if (summary.slot >= 0)
            nodes.push_back(summary);
    }
#endif

    return nodes;
}

const std::vector<PluginSummary>& PluginHostLayer::plugins() const noexcept
{
    return discoveredPlugins;
}

const std::string& PluginHostLayer::lastError() const noexcept
{
    return error;
}

const std::string& PluginHostLayer::lastScanReport() const noexcept
{
    return scanReport;
}
}
