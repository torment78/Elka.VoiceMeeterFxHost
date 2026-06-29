#include "plugins/PluginHostLayer.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <iomanip>
#include <limits>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <sstream>
#include <stdexcept>
#include <utility>
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
constexpr int PluginRuntimeMaxBlockSize = 8192;

int realtimePluginBlockSize(int blockSize) noexcept
{
    return std::clamp(std::max(blockSize, PluginRuntimeMaxBlockSize), 64, PluginRuntimeMaxBlockSize);
}

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

std::wstring fromUtf8(const std::string& value)
{
    if (value.empty())
        return {};

    const int chars = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, nullptr, 0);
    if (chars <= 0)
        return {};

    std::wstring result(static_cast<size_t>(chars - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, result.data(), chars);
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
        .getChildFile("ElkaSoft")
        .getChildFile("VoiceMeeterFxHost")
        .getChildFile("plugin-cache.xml");
}

juce::File legacyPluginCacheFile()
{
    return juce::File::getSpecialLocation(juce::File::userApplicationDataDirectory)
        .getChildFile("Elka")
        .getChildFile("VoiceMeeterFxHost")
        .getChildFile("plugin-cache.xml");
}

juce::File legacyVst3PluginCacheFile()
{
    return juce::File::getSpecialLocation(juce::File::userApplicationDataDirectory)
        .getChildFile("Elka")
        .getChildFile("VoiceMeeterFxHost")
        .getChildFile("vst3-cache.xml");
}

struct CachedPluginCandidate
{
    std::string path;
    int64_t size = -1;
    int64_t modified = -1;
};

template <typename Callback>
bool runOnJuceMessageThread(Callback&& callback)
{
    auto* messageManager = juce::MessageManager::getInstance();
    if (messageManager->isThisTheMessageThread())
        return callback();

    auto result = juce::MessageManager::callSync([&callback]() -> bool
    {
        return callback();
    });

    return result.has_value() && *result;
}

bool capturePluginStateBase64(juce::AudioPluginInstance& plugin, std::string& stateBase64, std::string& stateError)
{
    stateBase64.clear();
    stateError.clear();

    const auto captured = runOnJuceMessageThread([&]() -> bool
    {
        try
        {
            juce::MemoryBlock state;
            plugin.getStateInformation(state);
            stateBase64 = state.toBase64Encoding().toStdString();
            return true;
        }
        catch (const std::exception& ex)
        {
            stateError = ex.what();
        }
        catch (...)
        {
            stateError = "The plugin state could not be saved.";
        }

        return false;
    });

    if (!captured && stateError.empty())
        stateError = "The plugin state could not be captured on the JUCE message thread.";

    return captured;
}

bool applyPluginStateBase64(juce::AudioPluginInstance& plugin, const std::string& stateBase64, std::string& stateError)
{
    stateError.clear();
    if (stateBase64.empty())
        return true;

    juce::MemoryBlock state;
    if (!state.fromBase64Encoding(juce::String::fromUTF8(stateBase64.c_str())))
    {
        stateError = "Saved plugin state could not be decoded.";
        return false;
    }

    const auto restored = runOnJuceMessageThread([&]() -> bool
    {
        try
        {
            plugin.suspendProcessing(true);
            struct ResumeProcessing
            {
                juce::AudioPluginInstance& plugin;
                ~ResumeProcessing() { plugin.suspendProcessing(false); }
            } resume { plugin };

            plugin.setStateInformation(state.getData(), static_cast<int>(state.getSize()));
            plugin.updateHostDisplay(juce::AudioProcessorListener::ChangeDetails()
                .withNonParameterStateChanged(true)
                .withParameterInfoChanged(true));
            return true;
        }
        catch (const std::exception& ex)
        {
            stateError = ex.what();
        }
        catch (...)
        {
            stateError = "The plugin state could not be restored.";
        }

        return false;
    });

    if (!restored && stateError.empty())
        stateError = "The plugin state could not be restored on the JUCE message thread.";

    return restored;
}

bool capturePluginPresetBase64(juce::AudioPluginInstance& plugin, std::string& presetBase64, std::string& presetError)
{
    presetBase64.clear();
    presetError.clear();

    const auto captured = runOnJuceMessageThread([&]() -> bool
    {
        try
        {
            auto* vst3 = plugin.getVST3Client();
            if (vst3 == nullptr)
                return true;

            juce::MemoryBlock flushedState;
            plugin.getStateInformation(flushedState);

            const auto preset = vst3->getPreset();
            if (preset.getSize() > 0)
                presetBase64 = preset.toBase64Encoding().toStdString();
            return true;
        }
        catch (const std::exception& ex)
        {
            presetError = ex.what();
        }
        catch (...)
        {
            presetError = "The VST3 preset data could not be saved.";
        }

        return false;
    });

    if (!captured && presetError.empty())
        presetError = "The VST3 preset data could not be captured on the JUCE message thread.";

    return captured;
}

bool applyPluginPresetBase64(juce::AudioPluginInstance& plugin, const std::string& presetBase64, std::string& presetError)
{
    presetError.clear();
    if (presetBase64.empty())
        return true;

    juce::MemoryBlock preset;
    if (!preset.fromBase64Encoding(juce::String::fromUTF8(presetBase64.c_str())))
    {
        presetError = "Saved VST3 preset data could not be decoded.";
        return false;
    }

    const auto restored = runOnJuceMessageThread([&]() -> bool
    {
        try
        {
            auto* vst3 = plugin.getVST3Client();
            if (vst3 == nullptr)
            {
                presetError = "The plugin does not expose VST3 preset data.";
                return false;
            }

            plugin.suspendProcessing(true);
            struct ResumeProcessing
            {
                juce::AudioPluginInstance& plugin;
                ~ResumeProcessing() { plugin.suspendProcessing(false); }
            } resume { plugin };

            const auto applied = vst3->setPreset(preset);
            if (applied)
            {
                plugin.updateHostDisplay(juce::AudioProcessorListener::ChangeDetails()
                    .withNonParameterStateChanged(true)
                    .withParameterInfoChanged(true));
            }

            return applied;
        }
        catch (const std::exception& ex)
        {
            presetError = ex.what();
        }
        catch (...)
        {
            presetError = "The VST3 preset data could not be restored.";
        }

        return false;
    });

    if (!restored && presetError.empty())
        presetError = "The VST3 preset data could not be restored on the JUCE message thread.";

    return restored;
}

std::string capturePluginParameterStateBase64(juce::AudioPluginInstance& plugin, std::string& parameterError)
{
    parameterError.clear();

    std::string parameterStateBase64;
    const auto captured = runOnJuceMessageThread([&]() -> bool
    {
        try
        {
            std::ostringstream snapshot;
            snapshot << "ELKA_PLUGIN_PARAMETERS_V1\n";
            const auto& parameters = plugin.getParameters();
            snapshot << parameters.size() << '\n';
            snapshot << std::setprecision(9);

            for (int index = 0; index < parameters.size(); ++index)
            {
                if (auto* parameter = parameters[index])
                {
                    snapshot << index << '\t'
                             << std::clamp(parameter->getValue(), 0.0f, 1.0f)
                             << '\n';
                }
            }

            const auto text = snapshot.str();
            juce::MemoryBlock data(text.data(), text.size());
            parameterStateBase64 = data.toBase64Encoding().toStdString();
            return true;
        }
        catch (const std::exception& ex)
        {
            parameterError = ex.what();
        }
        catch (...)
        {
            parameterError = "The plugin parameter snapshot could not be saved.";
        }

        return false;
    });

    if (!captured && parameterError.empty())
        parameterError = "The plugin parameter snapshot could not be captured on the JUCE message thread.";

    return captured ? parameterStateBase64 : std::string {};
}

bool applyPluginParameterStateBase64(juce::AudioPluginInstance& plugin, const std::string& parameterStateBase64, std::string& parameterError)
{
    parameterError.clear();
    if (parameterStateBase64.empty())
        return true;

    juce::MemoryBlock data;
    if (!data.fromBase64Encoding(juce::String::fromUTF8(parameterStateBase64.c_str())))
    {
        parameterError = "Saved plugin parameter snapshot could not be decoded.";
        return false;
    }

    const auto restored = runOnJuceMessageThread([&]() -> bool
    {
        try
        {
            const auto text = std::string(static_cast<const char*>(data.getData()), data.getSize());
            std::istringstream input(text);
            std::string line;
            if (!std::getline(input, line) || line != "ELKA_PLUGIN_PARAMETERS_V1")
            {
                parameterError = "Saved plugin parameter snapshot has an unknown format.";
                return false;
            }

            std::getline(input, line);
            const auto& parameters = plugin.getParameters();

            plugin.suspendProcessing(true);
            struct ResumeProcessing
            {
                juce::AudioPluginInstance& plugin;
                ~ResumeProcessing() { plugin.suspendProcessing(false); }
            } resume { plugin };

            int appliedCount = 0;
            while (std::getline(input, line))
            {
                if (line.empty())
                    continue;

                const auto tab = line.find('\t');
                if (tab == std::string::npos)
                    continue;

                const auto index = std::stoi(line.substr(0, tab));
                const auto value = std::stof(line.substr(tab + 1));
                if (index < 0 || index >= parameters.size())
                    continue;

                if (auto* parameter = parameters[index])
                {
                    parameter->setValueNotifyingHost(std::clamp(value, 0.0f, 1.0f));
                    ++appliedCount;
                }
            }

            plugin.updateHostDisplay(juce::AudioProcessorListener::ChangeDetails()
                .withNonParameterStateChanged(true)
                .withParameterInfoChanged(true));
            return appliedCount > 0 || parameters.isEmpty();
        }
        catch (const std::exception& ex)
        {
            parameterError = ex.what();
        }
        catch (...)
        {
            parameterError = "The plugin parameter snapshot could not be restored.";
        }

        return false;
    });

    if (!restored && parameterError.empty())
        parameterError = "The plugin parameter snapshot could not be restored on the JUCE message thread.";

    return restored;
}

bool pluginFileExists(const juce::PluginDescription& description)
{
    return juce::File(description.fileOrIdentifier).exists();
}

std::vector<juce::String> vst3ResolutionCandidates(const juce::String& identifier)
{
    std::vector<juce::String> candidates;
    const juce::File vst3(identifier);
    candidates.push_back(identifier);

    if (!vst3.isDirectory() || !identifier.endsWithIgnoreCase(".vst3"))
        return candidates;

    const auto moduleName = vst3.getFileName();
    const auto x64Module = vst3
        .getChildFile("Contents")
        .getChildFile("x86_64-win")
        .getChildFile(moduleName);
    if (x64Module.exists())
        candidates.push_back(x64Module.getFullPathName());

    juce::Array<juce::File> nestedModules;
    vst3.findChildFiles(nestedModules, juce::File::findFiles, true, "*.vst3");
    for (const auto& module : nestedModules)
    {
        if (module.exists())
            candidates.push_back(module.getFullPathName());
    }

    std::sort(candidates.begin(), candidates.end(), [](const juce::String& left, const juce::String& right) {
        return left.compareIgnoreCase(right) < 0;
    });
    candidates.erase(std::unique(candidates.begin(), candidates.end(), [](const juce::String& left, const juce::String& right) {
        return left.equalsIgnoreCase(right);
    }), candidates.end());
    return candidates;
}

std::string normalizedPluginIdentifier(const juce::String& identifier)
{
    auto path = juce::File(identifier).getFullPathName().toStdString();
    if (path.empty())
        path = identifier.toStdString();

    std::replace(path.begin(), path.end(), '/', '\\');
    std::transform(path.begin(), path.end(), path.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });

    const auto vst3Bundle = path.find(".vst3");
    if (vst3Bundle != std::string::npos)
        path.resize(vst3Bundle + 5);

    return path;
}

std::string pluginCandidateKey(const juce::String& formatName, const juce::String& identifier)
{
    return formatName.toStdString() + "|" + normalizedPluginIdentifier(identifier);
}

std::string pluginCandidateKey(const juce::PluginDescription& description)
{
    return description.pluginFormatName.toStdString() + "|" +
           normalizedPluginIdentifier(description.fileOrIdentifier);
}

CachedPluginCandidate pluginCandidateSignature(const juce::String& identifier)
{
    juce::File file(identifier);
    if (!file.exists())
        return {};

    return CachedPluginCandidate {
        normalizedPluginIdentifier(identifier),
        static_cast<int64_t>(file.getSize()),
        static_cast<int64_t>(file.getLastModificationTime().toMilliseconds())
    };
}

bool cachedCandidateMatches(const CachedPluginCandidate& cached, const CachedPluginCandidate& current)
{
    return !cached.path.empty() &&
           cached.path == current.path &&
           cached.size == current.size &&
           cached.modified == current.modified;
}

std::string lowerAscii(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

bool pathExtensionIs(const std::filesystem::path& path, const char* extension)
{
    return lowerAscii(path.extension().string()) == extension;
}

std::string normalizedFolderKey(const std::string& path)
{
    try
    {
        auto folder = std::filesystem::path(fromUtf8(path));
        folder = std::filesystem::weakly_canonical(folder).lexically_normal();
        return lowerAscii(toUtf8(folder.wstring()));
    }
    catch (...)
    {
        try
        {
            auto folder = std::filesystem::path(fromUtf8(path)).lexically_normal();
            return lowerAscii(toUtf8(folder.wstring()));
        }
        catch (...)
        {
            return lowerAscii(path);
        }
    }
}

bool pathListContainsFolder(const std::vector<std::string>& paths, const std::string& path)
{
    const auto target = normalizedFolderKey(path);
    return std::any_of(paths.begin(), paths.end(), [&target](const std::string& candidate) {
        return normalizedFolderKey(candidate) == target;
    });
}

bool isUnsupported32BitBinary(const std::filesystem::path& path) noexcept
{
    DWORD binaryType = 0;
    if (!GetBinaryTypeW(path.c_str(), &binaryType))
        return false;

    return binaryType == SCS_32BIT_BINARY;
}

bool hasVst3Ancestor(std::filesystem::path path)
{
    for (auto parent = path.parent_path(); !parent.empty();)
    {
        if (pathExtensionIs(parent, ".vst3"))
            return true;

        const auto next = parent.parent_path();
        if (next == parent)
            break;

        parent = next;
    }

    return false;
}

int64_t parseInt64Attribute(const juce::XmlElement& xml, const char* name, int64_t fallback = -1)
{
    try
    {
        const auto value = xml.getStringAttribute(name).toStdString();
        if (value.empty())
            return fallback;

        return std::stoll(value);
    }
    catch (...)
    {
        return fallback;
    }
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

bool candidateIsInAnyPath(const CachedPluginCandidate& candidate, const std::vector<std::string>& paths)
{
    if (candidate.path.empty())
        return false;

    const juce::File pluginFile(juce::String::fromUTF8(candidate.path.c_str()));

    for (const auto& path : paths)
    {
        const juce::File folder(juce::String::fromUTF8(path.c_str()));
        if (pluginFile == folder || pluginFile.isAChildOf(folder))
            return true;
    }

    return false;
}

std::vector<juce::String> collectPluginCandidates(
    const std::string& root,
    const juce::String& formatName,
    std::ostringstream& report,
    const PluginScanProgressCallback& progress)
{
    std::vector<juce::String> candidates;
    const auto rootPath = std::filesystem::path(juce::String::fromUTF8(root.c_str()).toWideCharPointer());
    const bool scanVst3 = formatName.equalsIgnoreCase("VST3");
    const auto options = std::filesystem::directory_options::skip_permission_denied;
    const auto formatText = formatName.toStdString();

    try
    {
        if (scanVst3 && pathExtensionIs(rootPath, ".vst3") && std::filesystem::exists(rootPath))
        {
            const auto candidate = toUtf8(rootPath.wstring());
            candidates.emplace_back(candidate);
            if (progress)
                progress("Found " + formatText + " candidate", candidate, 1, 1);
            return candidates;
        }

        int visited = 0;
        for (std::filesystem::recursive_directory_iterator it(rootPath, options), end; it != end; ++it)
        {
            const auto& entry = *it;
            const auto& path = entry.path();
            const auto pathText = toUtf8(path.wstring());

            ++visited;
            if (progress)
                progress("Walking " + formatText + " folder", pathText, visited, 0);

            if (scanVst3)
            {
                if (pathExtensionIs(path, ".vst3") && (entry.is_directory() || entry.is_regular_file()))
                {
                    candidates.emplace_back(pathText);
                    if (progress)
                        progress("Found " + formatText + " candidate", pathText, static_cast<int>(candidates.size()), 0);
                    if (entry.is_directory())
                        it.disable_recursion_pending();
                }

                continue;
            }

            if (entry.is_directory() && pathExtensionIs(path, ".vst3"))
            {
                if (progress)
                    progress("Skipping nested VST3 bundle while scanning VST2", pathText, visited, 0);
                it.disable_recursion_pending();
                continue;
            }

            if (entry.is_regular_file() && pathExtensionIs(path, ".dll") && !hasVst3Ancestor(path))
            {
                if (isUnsupported32BitBinary(path))
                {
                    report << "  Skipping 32-bit VST2 DLL in x64 host: " << pathText << "\n";
                    if (progress)
                        progress("Skipping 32-bit VST2 DLL", pathText, visited, 0);
                    continue;
                }

                candidates.emplace_back(pathText);
                if (progress)
                    progress("Found " + formatText + " candidate", pathText, static_cast<int>(candidates.size()), 0);
            }
        }
    }
    catch (const std::exception& ex)
    {
        report << "  Could not enumerate this folder: " << ex.what() << "\n";
    }
    catch (...)
    {
        report << "  Could not enumerate this folder.\n";
    }

    std::sort(candidates.begin(), candidates.end(), [](const juce::String& left, const juce::String& right) {
        return left.compareIgnoreCase(right) < 0;
    });
    candidates.erase(std::unique(candidates.begin(), candidates.end(), [](const juce::String& left, const juce::String& right) {
        return left.equalsIgnoreCase(right);
    }), candidates.end());
    return candidates;
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

juce::PluginDescription lazyDescriptionForCandidate(const juce::String& formatName, const juce::String& identifier)
{
    juce::PluginDescription description;
    const juce::File file(identifier);
    auto name = file.getFileNameWithoutExtension();
    if (name.isEmpty())
        name = identifier;

    description.name = name;
    description.pluginFormatName = formatName;
    description.category = "Unverified";
    description.fileOrIdentifier = identifier;
    description.numInputChannels = 2;
    description.numOutputChannels = 2;
    description.isInstrument = false;
    return description;
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

std::string decodeJsonStringValue(const std::string& text, size_t quoteIndex)
{
    if (quoteIndex >= text.size() || text[quoteIndex] != '"')
        return {};

    std::string value;
    for (size_t index = quoteIndex + 1; index < text.size(); ++index)
    {
        const char ch = text[index];
        if (ch == '"')
            return value;

        if (ch != '\\')
        {
            value.push_back(ch);
            continue;
        }

        if (++index >= text.size())
            break;

        switch (text[index])
        {
        case '"':
        case '\\':
        case '/':
            value.push_back(text[index]);
            break;
        case 'b':
            value.push_back('\b');
            break;
        case 'f':
            value.push_back('\f');
            break;
        case 'n':
            value.push_back('\n');
            break;
        case 'r':
            value.push_back('\r');
            break;
        case 't':
            value.push_back('\t');
            break;
        case 'u':
            index += std::min<size_t>(4, text.size() - index - 1);
            value.push_back('?');
            break;
        default:
            value.push_back(text[index]);
            break;
        }
    }

    return {};
}

std::string jsonStringValueForKey(const std::string& text, const char* key)
{
    const std::string quotedKey = std::string("\"") + key + "\"";
    size_t searchFrom = 0;
    while (searchFrom < text.size())
    {
        const auto keyIndex = text.find(quotedKey, searchFrom);
        if (keyIndex == std::string::npos)
            return {};

        auto cursor = keyIndex + quotedKey.size();
        while (cursor < text.size() && std::isspace(static_cast<unsigned char>(text[cursor])))
            ++cursor;

        if (cursor >= text.size() || text[cursor] != ':')
        {
            searchFrom = keyIndex + quotedKey.size();
            continue;
        }

        ++cursor;
        while (cursor < text.size() && std::isspace(static_cast<unsigned char>(text[cursor])))
            ++cursor;

        if (cursor < text.size() && text[cursor] == '"')
            return decodeJsonStringValue(text, cursor);

        searchFrom = keyIndex + quotedKey.size();
    }

    return {};
}

juce::File vst3BundleForIdentifier(const juce::String& identifier)
{
    juce::File current(identifier);
    while (current.getFullPathName().isNotEmpty())
    {
        if (current.getFileName().endsWithIgnoreCase(".vst3"))
            return current;

        const auto parent = current.getParentDirectory();
        if (parent == current)
            break;

        current = parent;
    }

    return {};
}

juce::File moduleInfoFileForVst3(const juce::String& identifier)
{
    const auto bundle = vst3BundleForIdentifier(identifier);
    if (!bundle.exists())
        return {};

    const juce::File candidates[] = {
        bundle.getChildFile("Contents").getChildFile("moduleinfo.json"),
        bundle.getChildFile("Contents").getChildFile("Resources").getChildFile("moduleinfo.json"),
        bundle.getChildFile("moduleinfo.json")
    };

    for (const auto& candidate : candidates)
    {
        if (candidate.existsAsFile())
            return candidate;
    }

    return {};
}

juce::String cleanedPluginFileName(const juce::String& identifier)
{
    const juce::File file(identifier);
    auto name = file.getFileNameWithoutExtension();
    if (name.isEmpty())
        name = identifier;

    name = name.replaceCharacter('_', ' ');
    name = name.trim();
    return name.isNotEmpty() ? name : identifier;
}

juce::PluginDescription nameOnlyDescriptionForCandidate(const juce::String& formatName, const juce::String& identifier)
{
    auto description = lazyDescriptionForCandidate(formatName, identifier);
    description.name = cleanedPluginFileName(identifier);
    description.category = "NameOnly";

    if (!formatName.equalsIgnoreCase("VST3"))
        return description;

    const auto moduleInfo = moduleInfoFileForVst3(identifier);
    if (!moduleInfo.existsAsFile())
        return description;

    const auto text = moduleInfo.loadFileAsString().toStdString();
    const auto name = jsonStringValueForKey(text, "Name");
    if (!name.empty())
        description.name = juce::String::fromUTF8(name.c_str());

    const auto vendor = jsonStringValueForKey(text, "Vendor");
    if (!vendor.empty())
        description.manufacturerName = juce::String::fromUTF8(vendor.c_str());

    const auto category = jsonStringValueForKey(text, "Category");
    if (!category.empty())
        description.category = juce::String::fromUTF8(category.c_str());

    return description;
}

void scanFormatIntoList(
    const juce::String& formatName,
    const std::vector<std::string>& paths,
    std::set<std::string>& seen,
    std::set<std::string>& knownFiles,
    std::map<std::string, CachedPluginCandidate>& checkedCandidates,
    std::vector<PluginSummary>& summaries,
    std::vector<juce::PluginDescription>& descriptions,
    std::vector<unsigned char>& lazyDescriptions,
    std::ostringstream& report,
    const PluginScanProgressCallback& progress)
{
    const auto formatText = formatName.toStdString();
    for (const auto& path : paths)
    {
        if (progress)
            progress("Scanning " + formatText + " folder", path, 0, 0);

        if (!std::filesystem::is_directory(std::filesystem::path(path)))
        {
            report << "Skip missing folder: " << path << "\n";
            if (progress)
                progress("Skipped missing " + formatText + " folder", path, 0, 0);
            continue;
        }

        report << "Scanning " << formatText << ": " << path << "\n";
        const auto candidates = collectPluginCandidates(path, formatName, report, progress);

        if (candidates.empty())
            report << "  No plugin candidates found.\n";

        for (size_t candidateIndex = 0; candidateIndex < candidates.size(); ++candidateIndex)
        {
            const auto& fileOrIdentifier = candidates[candidateIndex];
            if (progress)
            {
                progress(
                    "Inventorying " + formatText + " candidate",
                    fileOrIdentifier.toStdString(),
                    static_cast<int>(candidateIndex + 1),
                    static_cast<int>(candidates.size()));
            }

            const auto normalizedCandidate = normalizedPluginIdentifier(fileOrIdentifier);
            const auto candidateKey = pluginCandidateKey(formatName, fileOrIdentifier);
            const auto candidateSignature = pluginCandidateSignature(fileOrIdentifier);
            if (knownFiles.find(normalizedCandidate) != knownFiles.end())
            {
                report << "  Cached: " << fileOrIdentifier.toStdString() << "\n";
                if (!candidateSignature.path.empty())
                    checkedCandidates[candidateKey] = candidateSignature;
                continue;
            }

            if (!candidateSignature.path.empty())
                checkedCandidates[candidateKey] = candidateSignature;

            if (progress)
            {
                progress(
                    "Reading " + formatText + " name",
                    fileOrIdentifier.toStdString(),
                    static_cast<int>(candidateIndex + 1),
                    static_cast<int>(candidates.size()));
            }

            auto description = nameOnlyDescriptionForCandidate(formatName, fileOrIdentifier);

            const auto key = pluginKey(description);
            if (!seen.insert(key).second)
            {
                report << "  Duplicate skipped: " << description.name.toStdString() << " | " << fileOrIdentifier.toStdString() << "\n";
                continue;
            }

            summaries.push_back(toSummary(description));
            descriptions.push_back(description);
            lazyDescriptions.push_back(1);
            knownFiles.insert(normalizedCandidate);
            checkedCandidates[pluginCandidateKey(description)] = pluginCandidateSignature(description.fileOrIdentifier);
            report << "  Added name-only candidate: " << description.name.toStdString() << " | " << fileOrIdentifier.toStdString() << "\n";
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

bool probePluginFileInternal(
    const std::string& format,
    const std::string& fileOrIdentifier,
    int sampleRate,
    int blockSize,
    std::string& error,
    PluginLoadProgressCallback progress)
{
    error.clear();

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (fileOrIdentifier.empty())
    {
        error = "No plugin file was provided.";
        return false;
    }

    const int preparedSampleRate = std::clamp(sampleRate, 8000, 192000);
    const int preparedBlockSize = std::clamp(blockSize, 64, 8192);
    const auto formatName = juce::String::fromUTF8(format.c_str());
    const auto identifier = juce::String::fromUTF8(fileOrIdentifier.c_str());

    if (progress)
        progress("Probe starting", fileOrIdentifier);

    try
    {
        juce::ScopedJuceInitialiser_GUI juceInitialiser;
        juce::OwnedArray<juce::PluginDescription> found;

        if (formatName.equalsIgnoreCase("VST3"))
        {
            juce::VST3PluginFormat vst3Format;
            for (const auto& resolutionCandidate : vst3ResolutionCandidates(identifier))
            {
                if (progress)
                    progress("Probe reading VST3 metadata", resolutionCandidate.toStdString());
                vst3Format.findAllTypesForFile(found, resolutionCandidate);
                if (!found.isEmpty())
                    break;
            }
        }
#if ELKA_ENABLE_VST2_HOST && JUCE_INTERNAL_HAS_VST
        else if (formatName.equalsIgnoreCase("VST") || formatName.equalsIgnoreCase("VST2"))
        {
            if (progress)
                progress("Probe reading VST2 metadata", fileOrIdentifier);
            juce::VSTPluginFormat vstFormat;
            vstFormat.findAllTypesForFile(found, identifier);
        }
#else
        else if (formatName.equalsIgnoreCase("VST") || formatName.equalsIgnoreCase("VST2"))
        {
            error = "VST2 hosting is not enabled.";
            return false;
        }
#endif
        else
        {
            error = "Unsupported plugin format: " + format;
            return false;
        }

        if (found.isEmpty() || found[0] == nullptr)
        {
            error = "The plugin did not report a loadable type.";
            return false;
        }

        const auto description = *found[0];
        const auto detail = description.name.toStdString() + " | " + description.fileOrIdentifier.toStdString();
        if (progress)
            progress("Probe creating plugin instance", detail);

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

        if (progress)
            progress("Probe preparing plugin", detail);
        instance->disableNonMainBuses();
        if (instance->getBusCount(true) > 0)
            instance->setChannelLayoutOfBus(true, 0, juce::AudioChannelSet::stereo());
        if (instance->getBusCount(false) > 0)
            instance->setChannelLayoutOfBus(false, 0, juce::AudioChannelSet::stereo());
        instance->setRateAndBufferSizeDetails(static_cast<double>(preparedSampleRate), preparedBlockSize);
        instance->prepareToPlay(static_cast<double>(preparedSampleRate), preparedBlockSize);
        instance->releaseResources();

        if (progress)
            progress("Probe complete", detail);
        return true;
    }
    catch (const std::exception& ex)
    {
        error = ex.what();
        if (progress)
            progress("Probe failed", error);
        return false;
    }
    catch (...)
    {
        error = "The plugin probe failed.";
        if (progress)
            progress("Probe failed", error);
        return false;
    }
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    if (progress)
        progress("Probe failed", error);
    return false;
#endif
}

int clampPluginChannels(int requested, const juce::PluginDescription& description)
{
    const int describedChannels = std::max(description.numInputChannels, description.numOutputChannels);
    const int preferredChannels = describedChannels > 0 ? std::min(requested, describedChannels) : requested;
    return std::clamp(preferredChannels, 1, 32);
}

juce::AudioChannelSet standardLayoutForChannelCount(int channels)
{
    switch (std::clamp(channels, 1, 32))
    {
    case 1: return juce::AudioChannelSet::mono();
    case 2: return juce::AudioChannelSet::stereo();
    case 4: return juce::AudioChannelSet::quadraphonic();
    case 6: return juce::AudioChannelSet::create5point1();
    case 8: return juce::AudioChannelSet::create7point1();
    case 12: return juce::AudioChannelSet::create7point1point4();
    default: return juce::AudioChannelSet::discreteChannels(std::clamp(channels, 1, 32));
    }
}

juce::AudioChannelSet layoutForId(int layoutId, int fallbackChannels)
{
    if (layoutId >= 1000 && layoutId <= 1032)
        return juce::AudioChannelSet::discreteChannels(layoutId - 1000);

    const int channels = std::clamp(fallbackChannels, 1, 32);
    switch (layoutId)
    {
    case 0: return juce::AudioChannelSet::mono();
    case 1: return juce::AudioChannelSet::stereo();
    case 2: return channels == 4 ? juce::AudioChannelSet::quadraphonic() : standardLayoutForChannelCount(channels);
    case 3: return channels == 6 ? juce::AudioChannelSet::create5point1() : standardLayoutForChannelCount(channels);
    case 4: return channels == 8 ? juce::AudioChannelSet::create7point1() : standardLayoutForChannelCount(channels);
    case 5: return channels == 12 ? juce::AudioChannelSet::create7point1point4() : standardLayoutForChannelCount(channels);
    case 6: return juce::AudioChannelSet::discreteChannels(channels);
    case 7: return channels == 8 ? juce::AudioChannelSet::create7point1SDDS() : standardLayoutForChannelCount(channels);
    default: return standardLayoutForChannelCount(channels);
    }
}

struct PluginBusLayoutChoice
{
    int id = 1;
    std::string name = "Stereo";
    int channels = 2;
    juce::AudioChannelSet layout;
};

int layoutIdForChannelCount(int channels)
{
    switch (std::clamp(channels, 1, 32))
    {
    case 1: return 0;
    case 2: return 1;
    case 4: return 2;
    case 6: return 3;
    case 8: return 4;
    case 12: return 5;
    default: return 1000 + std::clamp(channels, 1, 32);
    }
}

int layoutChannelsForId(int layoutId, int fallbackChannels)
{
    if (layoutId >= 1000 && layoutId <= 1032)
        return layoutId - 1000;

    switch (layoutId)
    {
    case 0: return 1;
    case 1: return 2;
    case 2: return 4;
    case 3: return 6;
    case 4: return 8;
    case 5: return 12;
    case 7: return 8;
    default: return std::clamp(fallbackChannels, 1, 32);
    }
}

std::string layoutNameForId(int layoutId, int fallbackChannels)
{
    if (layoutId >= 1000 && layoutId <= 1032)
        return "Discrete " + std::to_string(layoutId - 1000);

    switch (layoutId)
    {
    case 0: return "Mono";
    case 1: return "Stereo";
    case 2: return "Quad";
    case 3: return "5.1";
    case 4: return "7.1";
    case 5: return "7.1.4";
    case 7: return "7.1 SDDS";
    default: return std::to_string(std::clamp(fallbackChannels, 1, 32)) + " channel";
    }
}

PluginBusLayoutChoice makeLayoutChoice(int layoutId, int fallbackChannels)
{
    const int channels = layoutChannelsForId(layoutId, fallbackChannels);
    return PluginBusLayoutChoice { layoutId, layoutNameForId(layoutId, channels), channels, layoutForId(layoutId, channels) };
}

PluginBusLayoutChoice makeActualLayoutChoice(const PluginBusLayoutChoice& requested, const juce::AudioChannelSet& actual)
{
    const int channels = std::clamp(actual.size() > 0 ? actual.size() : requested.channels, 1, 32);
    int id = requested.id;

    if (actual == juce::AudioChannelSet::mono())
        id = 0;
    else if (actual == juce::AudioChannelSet::stereo())
        id = 1;
    else if (actual == juce::AudioChannelSet::quadraphonic())
        id = 2;
    else if (actual == juce::AudioChannelSet::create5point1())
        id = 3;
    else if (actual == juce::AudioChannelSet::create7point1())
        id = 4;
    else if (actual == juce::AudioChannelSet::create7point1point4())
        id = 5;
    else if (actual == juce::AudioChannelSet::create7point1SDDS())
        id = 7;
    else if (channels != requested.channels)
        id = layoutIdForChannelCount(channels);

    const auto name = (id == requested.id && channels == requested.channels)
        ? requested.name
        : layoutNameForId(id, channels);
    return PluginBusLayoutChoice { id, name, channels, actual.size() > 0 ? actual : layoutForId(id, channels) };
}

std::vector<PluginBusLayoutChoice> standardBusLayoutChoices()
{
    return {
        makeLayoutChoice(0, 1),
        makeLayoutChoice(1, 2),
        makeLayoutChoice(2, 4),
        makeLayoutChoice(3, 6),
        makeLayoutChoice(4, 8),
        makeLayoutChoice(7, 8),
        makeLayoutChoice(5, 12),
        makeLayoutChoice(1008, 8),
        makeLayoutChoice(1012, 12)
    };
}

void ensureBusesLayoutHasCurrentBuses(juce::AudioPluginInstance& instance, juce::AudioProcessor::BusesLayout& layout)
{
    for (int bus = layout.inputBuses.size(); bus < instance.getBusCount(true); ++bus)
        layout.inputBuses.add(instance.getChannelLayoutOfBus(true, bus));

    for (int bus = layout.outputBuses.size(); bus < instance.getBusCount(false); ++bus)
        layout.outputBuses.add(instance.getChannelLayoutOfBus(false, bus));
}

std::string encodeLayoutChoices(const std::vector<PluginBusLayoutChoice>& choices)
{
    std::ostringstream encoded;
    bool first = true;
    for (const auto& choice : choices)
    {
        if (choice.channels <= 0)
            continue;

        if (!first)
            encoded << ';';

        encoded << choice.id << ':' << choice.name << ':' << choice.channels;
        first = false;
    }

    return encoded.str();
}

std::vector<PluginBusLayoutChoice> supportedLayoutsForMainBus(
    juce::AudioPluginInstance& instance,
    bool input,
    const juce::AudioChannelSet& selectedInputLayout,
    const juce::AudioChannelSet& selectedOutputLayout,
    const PluginBusLayoutChoice& selectedChoice)
{
    (void)instance;
    (void)input;
    (void)selectedInputLayout;
    (void)selectedOutputLayout;

    // Do not probe every layout in-process. Some protected/surround VST3s fault
    // inside checkBusesLayoutSupported(), which can terminate the WPF host.
    // Offer the known host layouts and validate only the user's selected layout
    // when the node is created/reconfigured.
    auto supported = standardBusLayoutChoices();
    if (std::none_of(supported.begin(), supported.end(), [&selectedChoice](const PluginBusLayoutChoice& existing) { return existing.id == selectedChoice.id; }))
        supported.push_back(selectedChoice);

    return supported;
}

std::string fallbackLayoutCapability(int layoutId, const std::string& layoutName, int pins)
{
    const auto choice = makeLayoutChoice(layoutId, pins);
    const PluginBusLayoutChoice resolved { choice.id, layoutName.empty() ? choice.name : layoutName, choice.channels, choice.layout };
    return encodeLayoutChoices({ resolved });
}

float sanitizePluginSample(float sample) noexcept
{
    if (!std::isfinite(sample))
        return 0.0f;

    return std::clamp(sample, -8.0f, 8.0f);
}

bool processPluginBlockSafely(juce::AudioPluginInstance& plugin, juce::AudioBuffer<float>& block, juce::MidiBuffer& midi) noexcept
{
#if defined(_MSC_VER)
    __try
    {
        plugin.processBlock(block, midi);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
#else
    try
    {
        plugin.processBlock(block, midi);
        return true;
    }
    catch (...)
    {
        return false;
    }
#endif
}

class HostedVst3Processor final : public RealtimePluginProcessor
{
public:
    HostedVst3Processor(std::unique_ptr<juce::AudioPluginInstance> pluginInstance, int inputPins, int outputPins, int blockSize)
        : plugin(std::move(pluginInstance)),
          inputPinCount(std::clamp(inputPins, 1, 32)),
          outputPinCount(std::clamp(outputPins, 1, 32)),
          processChannels(std::max(inputPinCount, outputPinCount)),
          maxBlockSize(realtimePluginBlockSize(blockSize))
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
            return renderPassthrough(routing, buffer.samplesPerFrame);

        if (plugin == nullptr || buffer.write == nullptr || buffer.samplesPerFrame <= 0)
            return false;

        if (routing.inputRouteCount <= 0)
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

        if (!processPluginBlockSafely(*plugin, block, midi))
        {
            failed = true;
            return renderPassthrough(routing, buffer.samplesPerFrame);
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
    bool renderPassthrough(const PluginRoutingView& routing, int samples) noexcept
    {
        if (samples <= 0 || routing.inputRoutes == nullptr || routing.outputRoutes == nullptr)
            return false;

        std::array<float*, 64> clearedDestinations {};
        int clearedCount = 0;
        bool rendered = false;

        for (int outputIndex = 0; outputIndex < routing.outputRouteCount; ++outputIndex)
        {
            const auto& outputRoute = routing.outputRoutes[outputIndex];
            if (outputRoute.pluginPin < 0 || outputRoute.pluginPin >= outputPinCount || outputRoute.destination == nullptr)
                continue;

            const PluginAudioInputRoute* matchingInput = nullptr;
            for (int inputIndex = 0; inputIndex < routing.inputRouteCount; ++inputIndex)
            {
                const auto& inputRoute = routing.inputRoutes[inputIndex];
                if (inputRoute.pluginPin == outputRoute.pluginPin && inputRoute.source != nullptr)
                {
                    matchingInput = &inputRoute;
                    break;
                }
            }

            if (matchingInput == nullptr)
                continue;

            auto* destination = outputRoute.destination;
            if (matchingInput->source == destination)
            {
                rendered = true;
                continue;
            }

            bool alreadyCleared = false;
            for (int i = 0; i < clearedCount; ++i)
                alreadyCleared = alreadyCleared || clearedDestinations[static_cast<size_t>(i)] == destination;

            if (!alreadyCleared)
            {
                std::fill_n(destination, samples, 0.0f);
                if (clearedCount < static_cast<int>(clearedDestinations.size()))
                    clearedDestinations[static_cast<size_t>(clearedCount++)] = destination;
            }

            for (int sample = 0; sample < samples; ++sample)
                destination[sample] = sanitizePluginSample(destination[sample] + matchingInput->source[sample]);

            rendered = true;
        }

        return rendered;
    }

    std::unique_ptr<juce::AudioPluginInstance> plugin;
    juce::AudioBuffer<float> scratch;
    juce::MidiBuffer midi;
    int inputPinCount = 2;
    int outputPinCount = 2;
    int processChannels = 2;
    int maxBlockSize = 4096;
    bool failed = false;
};

std::unique_ptr<HostedVst3Processor> createHostedProcessorFromFile(
    const std::string& format,
    const std::string& fileOrIdentifier,
    int sampleRate,
    int blockSize,
    int inputPins,
    int outputPins,
    int inputLayoutId,
    int outputLayoutId,
    std::string& error)
{
    error.clear();
    if (fileOrIdentifier.empty())
    {
        error = "No plugin file was provided.";
        return nullptr;
    }

    const int preparedSampleRate = std::clamp(sampleRate, 8000, 192000);
    const int preparedBlockSize = realtimePluginBlockSize(blockSize);
    const int safeInputPins = std::clamp(inputPins, 1, 32);
    const int safeOutputPins = std::clamp(outputPins, 1, 32);
    const int channelCount = std::max(safeInputPins, safeOutputPins);
    const auto inputLayout = layoutForId(inputLayoutId, safeInputPins);
    const auto outputLayout = layoutForId(outputLayoutId, safeOutputPins);
    const auto formatName = juce::String::fromUTF8(format.c_str());
    const auto identifier = juce::String::fromUTF8(fileOrIdentifier.c_str());

    juce::OwnedArray<juce::PluginDescription> found;
    if (formatName.equalsIgnoreCase("VST3"))
    {
        juce::VST3PluginFormat vst3Format;
        for (const auto& resolutionCandidate : vst3ResolutionCandidates(identifier))
        {
            vst3Format.findAllTypesForFile(found, resolutionCandidate);
            if (!found.isEmpty())
                break;
        }
    }
#if ELKA_ENABLE_VST2_HOST && JUCE_INTERNAL_HAS_VST
    else if (formatName.equalsIgnoreCase("VST") || formatName.equalsIgnoreCase("VST2"))
    {
        juce::VSTPluginFormat vstFormat;
        vstFormat.findAllTypesForFile(found, identifier);
    }
#else
    else if (formatName.equalsIgnoreCase("VST") || formatName.equalsIgnoreCase("VST2"))
    {
        error = "VST2 hosting is not enabled.";
        return nullptr;
    }
#endif
    else
    {
        error = "Unsupported plugin format: " + format;
        return nullptr;
    }

    if (found.isEmpty() || found[0] == nullptr)
    {
        error = "The plugin did not report a loadable type.";
        return nullptr;
    }

    const auto description = *found[0];
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
        return nullptr;
    }

    try
    {
        instance->disableNonMainBuses();
        if (instance->getBusCount(true) > 0)
            instance->setChannelLayoutOfBus(true, 0, inputLayout);
        if (instance->getBusCount(false) > 0)
            instance->setChannelLayoutOfBus(false, 0, outputLayout);

        instance->setPlayConfigDetails(channelCount, channelCount, static_cast<double>(preparedSampleRate), preparedBlockSize);
        instance->setRateAndBufferSizeDetails(static_cast<double>(preparedSampleRate), preparedBlockSize);
        instance->prepareToPlay(static_cast<double>(preparedSampleRate), preparedBlockSize);
    }
    catch (const std::exception& ex)
    {
        error = ex.what();
        return nullptr;
    }
    catch (...)
    {
        error = "The selected plugin failed while preparing.";
        return nullptr;
    }

    return std::make_unique<HostedVst3Processor>(std::move(instance), safeInputPins, safeOutputPins, preparedBlockSize);
}

constexpr DWORD SandboxReadyTimeoutMs = 30000;
constexpr DWORD SandboxControlTimeoutMs = 10000;
constexpr DWORD SandboxStateTimeoutMs = 30000;
constexpr DWORD SandboxMagic = 0x414B4C45; // ELKA
constexpr DWORD SandboxVersion = 1;
constexpr int SandboxHeaderBytes = 64;
constexpr int SandboxStateBytes = 8 * 1024 * 1024;
constexpr int SandboxMaxPins = 32;
constexpr LONG SandboxCommandAudio = 1;
constexpr LONG SandboxCommandOpenEditor = 2;
constexpr LONG SandboxCommandGetState = 3;
constexpr LONG SandboxCommandSetState = 4;
constexpr LONG SandboxCommandGetPreset = 5;
constexpr LONG SandboxCommandSetPreset = 6;
constexpr LONG SandboxCommandGetParameters = 7;
constexpr LONG SandboxCommandSetParameters = 8;
volatile LONG sandboxGlobalSequence = 0;

struct SandboxAudioHeader
{
    LONG magic = static_cast<LONG>(SandboxMagic);
    LONG version = static_cast<LONG>(SandboxVersion);
    LONG maxChannels = 0;
    LONG maxSamples = 0;
    LONG inputPins = 0;
    LONG outputPins = 0;
    LONG sampleCount = 0;
    volatile LONG requestId = 0;
    volatile LONG responseId = 0;
    volatile LONG status = 0;
    volatile LONG command = 0;
    volatile LONG textByteCount = 0;
    LONG textCapacity = 0;
    LONG reserved[3] {};
};

static_assert(sizeof(SandboxAudioHeader) <= SandboxHeaderBytes);

std::wstring quoteWindowsArg(const std::wstring& value)
{
    std::wstring result = L"\"";
    for (wchar_t ch : value)
    {
        if (ch == L'\"')
            result += L'\\';
        result += ch;
    }

    result += L"\"";
    return result;
}

std::wstring configuredWorkerPath()
{
    std::array<wchar_t, 32768> envBuffer {};
    const DWORD envLength = GetEnvironmentVariableW(
        L"ELKA_PLUGIN_WORKER_EXE",
        envBuffer.data(),
        static_cast<DWORD>(envBuffer.size()));
    if (envLength > 0 && envLength < envBuffer.size())
    {
        std::filesystem::path configured(std::wstring(envBuffer.data(), envLength));
        if (std::filesystem::exists(configured))
            return configured.wstring();
    }

    std::array<wchar_t, MAX_PATH> buffer {};
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size())
        return {};

    std::filesystem::path path(std::wstring(buffer.data(), length));
    path = path.parent_path() / L"Elka.PluginWorker.exe";
    return path.wstring();
}

void closeHandleIfValid(HANDLE& handle) noexcept
{
    if (handle != nullptr)
    {
        CloseHandle(handle);
        handle = nullptr;
    }
}

class ScopedSandboxIpcFlag
{
public:
    explicit ScopedSandboxIpcFlag(volatile LONG& target, DWORD waitMilliseconds = 0) noexcept
        : flag(&target)
    {
        const auto deadline = GetTickCount64() + waitMilliseconds;
        do
        {
            if (InterlockedCompareExchange(flag, 1, 0) == 0)
            {
                locked = true;
                return;
            }

            if (waitMilliseconds == 0)
                return;

            Sleep(1);
        }
        while (GetTickCount64() < deadline);
    }

    ~ScopedSandboxIpcFlag()
    {
        if (locked && flag != nullptr)
            InterlockedExchange(flag, 0);
    }

    bool isLocked() const noexcept { return locked; }

private:
    volatile LONG* flag = nullptr;
    bool locked = false;
};

class SandboxedPluginProcessor final : public RealtimePluginProcessor
{
public:
    SandboxedPluginProcessor(
        const std::string& format,
        const std::string& fileOrIdentifier,
        int sampleRate,
        int blockSize,
        int timeoutBlockSize,
        int inputPins,
        int outputPins,
        int inputLayoutId,
        int outputLayoutId,
        std::string& error)
        : inputPinCount(std::clamp(inputPins, 1, SandboxMaxPins)),
          outputPinCount(std::clamp(outputPins, 1, SandboxMaxPins)),
          processChannels(std::max(inputPinCount, outputPinCount)),
          maxBlockSize(realtimePluginBlockSize(blockSize)),
          processTimeoutMs(sandboxProcessTimeoutFor(sampleRate, timeoutBlockSize))
    {
        start(format, fileOrIdentifier, sampleRate, inputLayoutId, outputLayoutId, error);
    }

    ~SandboxedPluginProcessor() override
    {
        stop();
    }

    SandboxedPluginProcessor(const SandboxedPluginProcessor&) = delete;
    SandboxedPluginProcessor& operator=(const SandboxedPluginProcessor&) = delete;

    bool process(AudioBufferView buffer, const PluginRoutingView& routing) noexcept override
    {
        if (!ready || header == nullptr || audioData == nullptr || requestEvent == nullptr || responseEvent == nullptr)
            return bypass(buffer, routing);

        if (buffer.samplesPerFrame <= 0 || buffer.samplesPerFrame > maxBlockSize)
            return bypass(buffer, routing);

        ScopedSandboxIpcFlag ipc(ipcBusy);
        if (!ipc.isLocked())
            return renderSilence(buffer, routing);

        if (!drainLateAudioResponse(0))
            return renderSilence(buffer, routing);

        for (int ch = 0; ch < processChannels; ++ch)
        {
            auto* destination = channelPointer(ch);
            if (destination != nullptr)
                std::fill_n(destination, buffer.samplesPerFrame, 0.0f);
        }

        for (int route = 0; route < routing.inputRouteCount; ++route)
        {
            const auto& inputRoute = routing.inputRoutes[route];
            if (inputRoute.pluginPin < 0 || inputRoute.pluginPin >= inputPinCount || inputRoute.source == nullptr)
                continue;

            auto* destination = channelPointer(inputRoute.pluginPin);
            if (destination == nullptr)
                continue;

            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                destination[sample] = sanitizePluginSample(destination[sample] + inputRoute.source[sample]);
        }

        header->inputPins = inputPinCount;
        header->outputPins = outputPinCount;
        header->sampleCount = buffer.samplesPerFrame;
        InterlockedExchange(&header->command, SandboxCommandAudio);
        const LONG request = InterlockedIncrement(&requestCounter);
        InterlockedExchange(&header->requestId, request);
        ResetEvent(responseEvent);
        SetEvent(requestEvent);

        const DWORD wait = WaitForSingleObject(responseEvent, processTimeoutMs);
        if (wait != WAIT_OBJECT_0)
        {
            lateAudioResponsePending = true;
            lateAudioRequestId = request;
            return renderSilence(buffer, routing);
        }

        lateAudioResponsePending = false;
        lateAudioRequestId = 0;
        if (header->responseId != request || header->status < 0)
            return renderSilence(buffer, routing);

        std::array<float*, 64> clearedDestinations {};
        int clearedCount = 0;
        for (int route = 0; route < routing.outputRouteCount; ++route)
        {
            const auto& outputRoute = routing.outputRoutes[route];
            if (outputRoute.pluginPin < 0 || outputRoute.pluginPin >= outputPinCount || outputRoute.destination == nullptr)
                continue;

            bool alreadyCleared = false;
            for (int i = 0; i < clearedCount; ++i)
                alreadyCleared = alreadyCleared || clearedDestinations[static_cast<size_t>(i)] == outputRoute.destination;

            if (!alreadyCleared)
            {
                std::fill_n(outputRoute.destination, buffer.samplesPerFrame, 0.0f);
                if (clearedCount < static_cast<int>(clearedDestinations.size()))
                    clearedDestinations[static_cast<size_t>(clearedCount++)] = outputRoute.destination;
            }

            const auto* source = channelPointer(outputRoute.pluginPin);
            if (source == nullptr)
                continue;

            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                outputRoute.destination[sample] = sanitizePluginSample(outputRoute.destination[sample] + source[sample]);
        }

        return true;
    }

    bool isReady() const noexcept { return ready; }

    bool openEditor(std::string& error) noexcept
    {
        error.clear();
        if (!ready || header == nullptr || controlEvent == nullptr || responseEvent == nullptr)
        {
            error = "Sandboxed plugin worker is not ready.";
            return false;
        }

        ScopedSandboxIpcFlag ipc(ipcBusy, 250);
        if (!ipc.isLocked())
        {
            error = "Sandboxed plugin worker is busy. Try opening the editor again.";
            return false;
        }

        if (!drainLateAudioResponse(250))
        {
            error = "Sandboxed plugin worker is still finishing a late audio block. Try again.";
            return false;
        }

        InterlockedExchange(&header->command, SandboxCommandOpenEditor);
        const LONG request = InterlockedIncrement(&requestCounter);
        InterlockedExchange(&header->requestId, request);
        ResetEvent(responseEvent);
        SetEvent(controlEvent);

        const DWORD wait = WaitForSingleObject(responseEvent, SandboxControlTimeoutMs);
        if (wait != WAIT_OBJECT_0 || header->responseId != request)
        {
            error = "Sandboxed plugin worker did not answer the editor request.";
            return false;
        }

        if (header->status < 0)
        {
            error = "Sandboxed plugin editor could not be opened in the worker.";
            return false;
        }

        return true;
    }

    std::string stateBase64(std::string& error)
    {
        std::string value;
        captureTextBase64Command(SandboxCommandGetState, value, error, "state");
        return value;
    }

    bool setStateBase64(const std::string& stateBase64, std::string& error)
    {
        return applyTextBase64Command(SandboxCommandSetState, stateBase64, error, "state");
    }

    std::string presetBase64(std::string& error)
    {
        std::string value;
        captureTextBase64Command(SandboxCommandGetPreset, value, error, "preset");
        return value;
    }

    bool setPresetBase64(const std::string& presetBase64, std::string& error)
    {
        return applyTextBase64Command(SandboxCommandSetPreset, presetBase64, error, "preset");
    }

    std::string parameterStateBase64(std::string& error)
    {
        std::string value;
        captureTextBase64Command(SandboxCommandGetParameters, value, error, "parameter snapshot");
        return value;
    }

    bool setParameterStateBase64(const std::string& parameterStateBase64, std::string& error)
    {
        return applyTextBase64Command(SandboxCommandSetParameters, parameterStateBase64, error, "parameter snapshot");
    }

private:
    bool captureTextBase64Command(LONG command, std::string& value, std::string& error, const char* label)
    {
        value.clear();
        error.clear();
        if (!ready || header == nullptr || stateData == nullptr || stateDataBytes <= 1 || controlEvent == nullptr || responseEvent == nullptr)
        {
            error = std::string("Sandboxed plugin worker is not ready for ") + label + " capture.";
            return false;
        }

        ScopedSandboxIpcFlag ipc(ipcBusy, 1000);
        if (!ipc.isLocked())
        {
            error = std::string("Sandboxed plugin worker is busy. Try saving ") + label + " again.";
            return false;
        }

        if (!drainLateAudioResponse(250))
        {
            error = "Sandboxed plugin worker is still finishing a late audio block.";
            return false;
        }

        std::fill_n(stateData, static_cast<size_t>(stateDataBytes), static_cast<unsigned char>(0));
        InterlockedExchange(&header->textByteCount, 0);
        InterlockedExchange(&header->command, command);
        const LONG request = InterlockedIncrement(&requestCounter);
        InterlockedExchange(&header->requestId, request);
        ResetEvent(responseEvent);
        SetEvent(controlEvent);

        const DWORD wait = WaitForSingleObject(responseEvent, SandboxStateTimeoutMs);
        if (wait != WAIT_OBJECT_0 || header->responseId != request)
        {
            error = std::string("Sandboxed plugin worker did not answer the ") + label + " capture request.";
            return false;
        }

        if (header->status < 0)
        {
            error = std::string("Sandboxed plugin ") + label + " capture failed in the worker.";
            return false;
        }

        const auto byteCount = std::clamp(static_cast<int>(header->textByteCount), 0, stateDataBytes);
        value.assign(reinterpret_cast<const char*>(stateData), static_cast<size_t>(byteCount));
        return true;
    }

    bool applyTextBase64Command(LONG command, const std::string& value, std::string& error, const char* label)
    {
        error.clear();
        if (value.empty())
            return true;

        if (!ready || header == nullptr || stateData == nullptr || stateDataBytes <= 1 || controlEvent == nullptr || responseEvent == nullptr)
        {
            error = std::string("Sandboxed plugin worker is not ready for ") + label + " restore.";
            return false;
        }

        if (value.size() >= static_cast<size_t>(stateDataBytes))
        {
            error = std::string("Saved sandboxed plugin ") + label + " is too large for the worker transfer buffer.";
            return false;
        }

        ScopedSandboxIpcFlag ipc(ipcBusy, 1000);
        if (!ipc.isLocked())
        {
            error = std::string("Sandboxed plugin worker is busy. Try restoring ") + label + " again.";
            return false;
        }

        if (!drainLateAudioResponse(250))
        {
            error = "Sandboxed plugin worker is still finishing a late audio block.";
            return false;
        }

        std::fill_n(stateData, static_cast<size_t>(stateDataBytes), static_cast<unsigned char>(0));
        std::memcpy(stateData, value.data(), value.size());
        InterlockedExchange(&header->textByteCount, static_cast<LONG>(value.size()));
        InterlockedExchange(&header->command, command);
        const LONG request = InterlockedIncrement(&requestCounter);
        InterlockedExchange(&header->requestId, request);
        ResetEvent(responseEvent);
        SetEvent(controlEvent);

        const DWORD wait = WaitForSingleObject(responseEvent, SandboxStateTimeoutMs);
        if (wait != WAIT_OBJECT_0 || header->responseId != request)
        {
            error = std::string("Sandboxed plugin worker did not answer the ") + label + " restore request.";
            return false;
        }

        if (header->status < 0)
        {
            error = std::string("Sandboxed plugin ") + label + " restore failed in the worker.";
            return false;
        }

        return true;
    }

    void start(const std::string& format, const std::string& fileOrIdentifier, int sampleRate, int inputLayoutId, int outputLayoutId, std::string& error)
    {
        error.clear();
        const auto workerPath = configuredWorkerPath();
        if (workerPath.empty() || !std::filesystem::exists(workerPath))
        {
            error = "Elka.PluginWorker.exe was not found beside the main app or in the embedded worker cache.";
            return;
        }

        const DWORD pid = GetCurrentProcessId();
        const LONG sequence = InterlockedIncrement(&sandboxGlobalSequence);
        const std::wstring baseName = L"Local\\ElkaFxSandbox_" + std::to_wstring(pid) + L"_" + std::to_wstring(sequence);
        mapName = baseName + L"_map";
        requestEventName = baseName + L"_request";
        controlEventName = baseName + L"_control";
        responseEventName = baseName + L"_response";
        shutdownEventName = baseName + L"_shutdown";

        const size_t audioBytes = static_cast<size_t>(processChannels) * static_cast<size_t>(maxBlockSize) * sizeof(float);
        const size_t mappingBytes = SandboxHeaderBytes + audioBytes + static_cast<size_t>(SandboxStateBytes);
        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, static_cast<DWORD>(mappingBytes), mapName.c_str());
        if (mapping == nullptr)
        {
            error = "Could not create sandbox shared memory.";
            return;
        }

        auto* mapped = static_cast<unsigned char*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, mappingBytes));
        if (mapped == nullptr)
        {
            error = "Could not map sandbox shared memory.";
            return;
        }

        mappedView = mapped;
        header = reinterpret_cast<SandboxAudioHeader*>(mappedView);
        std::memset(header, 0, SandboxHeaderBytes);
        header->magic = static_cast<LONG>(SandboxMagic);
        header->version = static_cast<LONG>(SandboxVersion);
        header->maxChannels = processChannels;
        header->maxSamples = maxBlockSize;
        header->inputPins = inputPinCount;
        header->outputPins = outputPinCount;
        audioData = reinterpret_cast<float*>(mappedView + SandboxHeaderBytes);
        stateData = mappedView + SandboxHeaderBytes + audioBytes;
        stateDataBytes = SandboxStateBytes;
        header->textCapacity = stateDataBytes;

        requestEvent = CreateEventW(nullptr, FALSE, FALSE, requestEventName.c_str());
        controlEvent = CreateEventW(nullptr, FALSE, FALSE, controlEventName.c_str());
        responseEvent = CreateEventW(nullptr, FALSE, FALSE, responseEventName.c_str());
        shutdownEvent = CreateEventW(nullptr, TRUE, FALSE, shutdownEventName.c_str());
        if (requestEvent == nullptr || controlEvent == nullptr || responseEvent == nullptr || shutdownEvent == nullptr)
        {
            error = "Could not create sandbox synchronization events.";
            return;
        }

        std::wstring commandLine = quoteWindowsArg(workerPath);
        commandLine += L" host-v1 ";
        commandLine += quoteWindowsArg(fromUtf8(format));
        commandLine += L" ";
        commandLine += quoteWindowsArg(fromUtf8(fileOrIdentifier));
        commandLine += L" ";
        commandLine += std::to_wstring(std::clamp(sampleRate, 8000, 192000));
        commandLine += L" ";
        commandLine += std::to_wstring(maxBlockSize);
        commandLine += L" ";
        commandLine += std::to_wstring(inputPinCount);
        commandLine += L" ";
        commandLine += std::to_wstring(outputPinCount);
        commandLine += L" ";
        commandLine += std::to_wstring(inputLayoutId);
        commandLine += L" ";
        commandLine += std::to_wstring(outputLayoutId);
        commandLine += L" ";
        commandLine += quoteWindowsArg(mapName);
        commandLine += L" ";
        commandLine += quoteWindowsArg(requestEventName);
        commandLine += L" ";
        commandLine += quoteWindowsArg(responseEventName);
        commandLine += L" ";
        commandLine += quoteWindowsArg(shutdownEventName);
        commandLine += L" ";
        commandLine += quoteWindowsArg(controlEventName);

        STARTUPINFOW startup {};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION processInfo {};
        std::vector<wchar_t> mutableCommand(commandLine.begin(), commandLine.end());
        mutableCommand.push_back(L'\0');
        if (!CreateProcessW(nullptr, mutableCommand.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | HIGH_PRIORITY_CLASS, nullptr, nullptr, &startup, &processInfo))
        {
            error = "Could not start Elka.PluginWorker.exe.";
            return;
        }

        workerProcess = processInfo.hProcess;
        workerThread = processInfo.hThread;
        AllowSetForegroundWindow(processInfo.dwProcessId);

        const DWORD wait = WaitForSingleObject(responseEvent, SandboxReadyTimeoutMs);
        if (wait != WAIT_OBJECT_0 || header->status <= 0)
        {
            error = "Sandboxed plugin worker did not become ready before timeout.";
            stop();
            return;
        }

        ready = true;
    }

    void stop() noexcept
    {
        ready = false;
        if (shutdownEvent != nullptr)
            SetEvent(shutdownEvent);

        if (workerProcess != nullptr)
        {
            const DWORD wait = WaitForSingleObject(workerProcess, 1500);
            if (wait != WAIT_OBJECT_0)
                TerminateProcess(workerProcess, 2);
        }

        closeHandleIfValid(workerThread);
        closeHandleIfValid(workerProcess);
        closeHandleIfValid(shutdownEvent);
        closeHandleIfValid(responseEvent);
        closeHandleIfValid(controlEvent);
        closeHandleIfValid(requestEvent);

        if (mappedView != nullptr)
        {
            UnmapViewOfFile(mappedView);
            mappedView = nullptr;
        }

        audioData = nullptr;
        stateData = nullptr;
        stateDataBytes = 0;
        header = nullptr;
        closeHandleIfValid(mapping);
    }

    float* channelPointer(int channel) noexcept
    {
        if (audioData == nullptr || channel < 0 || channel >= processChannels)
            return nullptr;

        return audioData + (static_cast<size_t>(channel) * static_cast<size_t>(maxBlockSize));
    }

    const float* channelPointer(int channel) const noexcept
    {
        if (audioData == nullptr || channel < 0 || channel >= processChannels)
            return nullptr;

        return audioData + (static_cast<size_t>(channel) * static_cast<size_t>(maxBlockSize));
    }

    bool drainLateAudioResponse(DWORD waitMilliseconds) noexcept
    {
        if (!lateAudioResponsePending)
            return true;

        if (responseEvent == nullptr)
        {
            lateAudioResponsePending = false;
            lateAudioRequestId = 0;
            return true;
        }

        const DWORD wait = WaitForSingleObject(responseEvent, waitMilliseconds);
        if (wait != WAIT_OBJECT_0)
            return false;

        lateAudioResponsePending = false;
        lateAudioRequestId = 0;
        return true;
    }

    bool renderSilence(AudioBufferView buffer, const PluginRoutingView& routing) noexcept
    {
        if (buffer.samplesPerFrame <= 0)
            return false;

        std::array<float*, 64> clearedDestinations {};
        int clearedCount = 0;
        bool rendered = false;

        for (int outputIndex = 0; outputIndex < routing.outputRouteCount; ++outputIndex)
        {
            const auto& outputRoute = routing.outputRoutes[outputIndex];
            if (outputRoute.destination == nullptr)
                continue;

            bool alreadyCleared = false;
            for (int i = 0; i < clearedCount; ++i)
                alreadyCleared = alreadyCleared || clearedDestinations[static_cast<size_t>(i)] == outputRoute.destination;

            if (!alreadyCleared)
            {
                std::fill_n(outputRoute.destination, buffer.samplesPerFrame, 0.0f);
                if (clearedCount < static_cast<int>(clearedDestinations.size()))
                    clearedDestinations[static_cast<size_t>(clearedCount++)] = outputRoute.destination;
            }

            rendered = true;
        }

        return rendered;
    }
    bool bypass(AudioBufferView buffer, const PluginRoutingView& routing) noexcept
    {
        if (buffer.samplesPerFrame <= 0)
            return false;

        std::array<float*, 64> clearedDestinations {};
        int clearedCount = 0;
        bool rendered = false;
        for (int outputIndex = 0; outputIndex < routing.outputRouteCount; ++outputIndex)
        {
            const auto& outputRoute = routing.outputRoutes[outputIndex];
            if (outputRoute.destination == nullptr)
                continue;

            const PluginAudioInputRoute* passthroughInput = nullptr;
            for (int inputIndex = 0; inputIndex < routing.inputRouteCount; ++inputIndex)
            {
                const auto& inputRoute = routing.inputRoutes[inputIndex];
                if (inputRoute.pluginPin == outputRoute.pluginPin && inputRoute.source != nullptr)
                {
                    passthroughInput = &inputRoute;
                    break;
                }
            }

            if (passthroughInput == nullptr)
                continue;

            bool alreadyCleared = false;
            for (int i = 0; i < clearedCount; ++i)
                alreadyCleared = alreadyCleared || clearedDestinations[static_cast<size_t>(i)] == outputRoute.destination;

            if (!alreadyCleared)
            {
                std::fill_n(outputRoute.destination, buffer.samplesPerFrame, 0.0f);
                if (clearedCount < static_cast<int>(clearedDestinations.size()))
                    clearedDestinations[static_cast<size_t>(clearedCount++)] = outputRoute.destination;
            }

            for (int sample = 0; sample < buffer.samplesPerFrame; ++sample)
                outputRoute.destination[sample] = sanitizePluginSample(outputRoute.destination[sample] + passthroughInput->source[sample]);

            rendered = true;
        }

        return rendered;
    }

    static DWORD sandboxProcessTimeoutFor(int sampleRate, int blockSize) noexcept
    {
        const auto safeSampleRate = std::clamp(sampleRate, 8000, 192000);
        const auto safeBlockSize = std::clamp(blockSize, 64, 8192);
        const auto blockMs = (static_cast<double>(safeBlockSize) * 1000.0) / static_cast<double>(safeSampleRate);

        if (blockMs <= 3.0)
            return 0;

        if (blockMs <= 6.0)
            return static_cast<DWORD>(std::clamp(static_cast<int>(std::floor(blockMs * 0.50)), 1, 3));

        const auto waitMs = static_cast<int>(std::floor((blockMs * 1.10) + 1.0));
        return static_cast<DWORD>(std::clamp(waitMs, 4, 24));
    }

    std::wstring mapName;
    std::wstring requestEventName;
    std::wstring controlEventName;
    std::wstring responseEventName;
    std::wstring shutdownEventName;
    HANDLE mapping = nullptr;
    HANDLE requestEvent = nullptr;
    HANDLE controlEvent = nullptr;
    HANDLE responseEvent = nullptr;
    HANDLE shutdownEvent = nullptr;
    HANDLE workerProcess = nullptr;
    HANDLE workerThread = nullptr;
    unsigned char* mappedView = nullptr;
    SandboxAudioHeader* header = nullptr;
    float* audioData = nullptr;
    unsigned char* stateData = nullptr;
    int stateDataBytes = 0;
    volatile LONG requestCounter = 0;
    volatile LONG ipcBusy = 0;
    bool lateAudioResponsePending = false;
    LONG lateAudioRequestId = 0;
    int inputPinCount = 2;
    int outputPinCount = 2;
    int processChannels = 2;
    int maxBlockSize = 512;
    DWORD processTimeoutMs = 8;
    bool ready = false;
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
        bringToFront();
    }

    void closeButtonPressed() override
    {
        setVisible(false);
    }

    void bringToFront()
    {
        toFront(true);
       #if JUCE_WINDOWS
        if (auto* hwnd = static_cast<HWND>(getWindowHandle()))
        {
            ShowWindow(hwnd, SW_SHOWNORMAL);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
       #endif
    }
};

struct WorkerPluginSession
{
    juce::ScopedJuceInitialiser_GUI juceInitialiser;
    std::unique_ptr<HostedVst3Processor> processor;
    std::unique_ptr<PluginEditorWindow> editorWindow;
    int inputPins = 2;
    int outputPins = 2;
    int channelCount = 2;
    int maxBlockSize = 512;
};

std::mutex workerSessionsMutex;
std::map<int, std::unique_ptr<WorkerPluginSession>> workerSessions;
int nextWorkerSessionHandle = 1;
#endif
}

bool probePluginFile(
    const std::string& format,
    const std::string& fileOrIdentifier,
    int sampleRate,
    int blockSize,
    std::string& error,
    PluginLoadProgressCallback progress)
{
    return probePluginFileInternal(format, fileOrIdentifier, sampleRate, blockSize, error, std::move(progress));
}

int createWorkerPluginProcessor(
    const std::string& format,
    const std::string& fileOrIdentifier,
    int sampleRate,
    int blockSize,
    int inputPins,
    int outputPins,
    int inputLayoutId,
    int outputLayoutId,
    std::string& error)
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    auto session = std::make_unique<WorkerPluginSession>();
    session->inputPins = std::clamp(inputPins, 1, SandboxMaxPins);
    session->outputPins = std::clamp(outputPins, 1, SandboxMaxPins);
    session->channelCount = std::max(session->inputPins, session->outputPins);
    session->maxBlockSize = realtimePluginBlockSize(blockSize);
    session->processor = createHostedProcessorFromFile(
        format,
        fileOrIdentifier,
        sampleRate,
        session->maxBlockSize,
        session->inputPins,
        session->outputPins,
        inputLayoutId,
        outputLayoutId,
        error);

    if (session->processor == nullptr)
        return 0;

    std::lock_guard lock(workerSessionsMutex);
    const int handle = nextWorkerSessionHandle++;
    workerSessions[handle] = std::move(session);
    return handle;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return 0;
#endif
}

bool processWorkerPluginProcessor(int handle, float* planarData, int channelCount, int samples)
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::unique_lock lock(workerSessionsMutex);
    const auto found = workerSessions.find(handle);
    if (found == workerSessions.end() || found->second == nullptr || found->second->processor == nullptr)
        return false;

    auto* session = found->second.get();
    auto* processor = session->processor.get();
    const int safeChannelCount = std::clamp(channelCount, 1, SandboxMaxPins);
    const int safeSamples = std::clamp(samples, 0, session->maxBlockSize);
    if (planarData == nullptr || safeSamples <= 0)
        return false;

    std::array<float*, SandboxMaxPins> writePointers {};
    for (int ch = 0; ch < safeChannelCount; ++ch)
        writePointers[static_cast<size_t>(ch)] = planarData + (static_cast<size_t>(ch) * static_cast<size_t>(session->maxBlockSize));

    std::array<PluginAudioInputRoute, SandboxMaxPins> inputRoutes {};
    std::array<PluginAudioOutputRoute, SandboxMaxPins> outputRoutes {};
    const int inputRouteCount = std::min(session->inputPins, safeChannelCount);
    const int outputRouteCount = std::min(session->outputPins, safeChannelCount);
    for (int pin = 0; pin < inputRouteCount; ++pin)
        inputRoutes[static_cast<size_t>(pin)] = PluginAudioInputRoute { writePointers[static_cast<size_t>(pin)], pin };
    for (int pin = 0; pin < outputRouteCount; ++pin)
        outputRoutes[static_cast<size_t>(pin)] = PluginAudioOutputRoute { writePointers[static_cast<size_t>(pin)], pin, -1, -1, -1 };

    AudioBufferView view {};
    view.write = writePointers.data();
    view.inputChannels = safeChannelCount;
    view.outputChannels = safeChannelCount;
    view.samplesPerFrame = safeSamples;
    lock.unlock();

    return processor->process(
        view,
        PluginRoutingView {
            inputRoutes.data(),
            inputRouteCount,
            outputRoutes.data(),
            outputRouteCount
        });
#else
    return false;
#endif
}

bool openWorkerPluginEditor(int handle, std::string& error)
{
    error.clear();
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::lock_guard lock(workerSessionsMutex);
    const auto found = workerSessions.find(handle);
    if (found == workerSessions.end() || found->second == nullptr || found->second->processor == nullptr)
    {
        error = "Worker plugin session was not found.";
        return false;
    }

    auto* session = found->second.get();
    auto* plugin = session->processor->pluginInstance();
    if (plugin == nullptr)
    {
        error = "Worker plugin instance was not found.";
        return false;
    }

    const auto opened = runOnJuceMessageThread([&]() -> bool
    {
        try
        {
            if (session->editorWindow == nullptr)
            {
                session->editorWindow = std::make_unique<PluginEditorWindow>(
                    *plugin,
                    juce::String(plugin->getName()).isNotEmpty() ? plugin->getName() : "Sandboxed VST");
            }
            else
            {
                session->editorWindow->setVisible(true);
                session->editorWindow->bringToFront();
            }

            return true;
        }
        catch (const std::exception& ex)
        {
            error = ex.what();
            session->editorWindow.reset();
            return false;
        }
        catch (...)
        {
            error = "The sandboxed plugin editor could not be opened.";
            session->editorWindow.reset();
            return false;
        }
    });

    if (!opened && error.empty())
        error = "The sandboxed plugin editor could not be opened on the worker message thread.";

    return opened;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return false;
#endif
}


juce::AudioPluginInstance* workerPluginInstanceLocked(int handle, std::string& error)
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    const auto found = workerSessions.find(handle);
    if (found == workerSessions.end() || found->second == nullptr || found->second->processor == nullptr)
    {
        error = "Worker plugin session was not found.";
        return nullptr;
    }

    auto* plugin = found->second->processor->pluginInstance();
    if (plugin == nullptr)
        error = "Worker plugin instance was not found.";
    return plugin;
#else
    (void) handle;
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return nullptr;
#endif
}

std::string workerPluginStateBase64(int handle, std::string& error)
{
    error.clear();
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::lock_guard lock(workerSessionsMutex);
    auto* plugin = workerPluginInstanceLocked(handle, error);
    if (plugin == nullptr)
        return {};

    std::string stateBase64;
    if (!capturePluginStateBase64(*plugin, stateBase64, error))
        return {};

    return stateBase64;
#else
    (void) handle;
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return {};
#endif
}

bool setWorkerPluginStateBase64(int handle, const std::string& stateBase64, std::string& error)
{
    error.clear();
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::lock_guard lock(workerSessionsMutex);
    auto* plugin = workerPluginInstanceLocked(handle, error);
    return plugin != nullptr && applyPluginStateBase64(*plugin, stateBase64, error);
#else
    (void) handle;
    (void) stateBase64;
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return false;
#endif
}

std::string workerPluginPresetBase64(int handle, std::string& error)
{
    error.clear();
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::lock_guard lock(workerSessionsMutex);
    auto* plugin = workerPluginInstanceLocked(handle, error);
    if (plugin == nullptr)
        return {};

    std::string presetBase64;
    if (!capturePluginPresetBase64(*plugin, presetBase64, error))
        return {};

    return presetBase64;
#else
    (void) handle;
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return {};
#endif
}

bool setWorkerPluginPresetBase64(int handle, const std::string& presetBase64, std::string& error)
{
    error.clear();
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::lock_guard lock(workerSessionsMutex);
    auto* plugin = workerPluginInstanceLocked(handle, error);
    return plugin != nullptr && applyPluginPresetBase64(*plugin, presetBase64, error);
#else
    (void) handle;
    (void) presetBase64;
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return false;
#endif
}

std::string workerPluginParameterStateBase64(int handle, std::string& error)
{
    error.clear();
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::lock_guard lock(workerSessionsMutex);
    auto* plugin = workerPluginInstanceLocked(handle, error);
    return plugin == nullptr ? std::string {} : capturePluginParameterStateBase64(*plugin, error);
#else
    (void) handle;
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return {};
#endif
}

bool setWorkerPluginParameterStateBase64(int handle, const std::string& parameterStateBase64, std::string& error)
{
    error.clear();
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::lock_guard lock(workerSessionsMutex);
    auto* plugin = workerPluginInstanceLocked(handle, error);
    return plugin != nullptr && applyPluginParameterStateBase64(*plugin, parameterStateBase64, error);
#else
    (void) handle;
    (void) parameterStateBase64;
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return false;
#endif
}

void pollWorkerPluginMessages(int milliseconds)
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
   #if JUCE_WINDOWS
    const auto deadline = juce::Time::getMillisecondCounterHiRes() + std::clamp(milliseconds, 1, 50);
    do
    {
        MSG message {};
        auto dispatched = false;
        while (PeekMessage(&message, nullptr, 0, 0, PM_REMOVE) != 0)
        {
            if (message.message == WM_QUIT)
                return;

            TranslateMessage(&message);
            DispatchMessage(&message);
            dispatched = true;
        }

        if (!dispatched)
            juce::Thread::sleep(1);
    }
    while (juce::Time::getMillisecondCounterHiRes() < deadline);
   #else
    juce::Thread::sleep(std::clamp(milliseconds, 1, 50));
   #endif
#else
    (void) milliseconds;
#endif
}

void destroyWorkerPluginProcessor(int handle)
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::lock_guard lock(workerSessionsMutex);
    const auto found = workerSessions.find(handle);
    if (found != workerSessions.end() && found->second != nullptr)
    {
        auto* session = found->second.get();
        runOnJuceMessageThread([&]() -> bool
        {
            session->editorWindow.reset();
            return true;
        });
    }

    workerSessions.erase(handle);
#else
    (void) handle;
#endif
}

struct PluginHostLayer::Impl
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    juce::ScopedJuceInitialiser_GUI juceInitialiser;
    std::vector<juce::PluginDescription> descriptions;
    std::vector<unsigned char> lazyDescriptions;
    std::map<std::string, CachedPluginCandidate> checkedCandidates;
    std::unique_ptr<HostedVst3Processor> activeProcessor;
    std::unique_ptr<PluginEditorWindow> editorWindow;
    std::array<std::unique_ptr<RealtimePluginProcessor>, PluginHostLayer::MaxPluginNodes> nodeProcessors {};
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
#ifndef _WIN64
    addIfDirectoryExists(paths, envVar(L"ProgramFiles(x86)") + L"\\Common Files\\VST3");
#endif
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
#ifndef _WIN64
    addIfDirectoryExists(paths, envVar(L"ProgramFiles(x86)") + L"\\Steinberg\\VstPlugins");
    addIfDirectoryExists(paths, envVar(L"ProgramFiles(x86)") + L"\\VstPlugins");
    addIfDirectoryExists(paths, envVar(L"ProgramFiles(x86)") + L"\\Common Files\\VST2");
#endif
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
    return scanPluginPaths(paths, append, formatFlags, {});
}

int PluginHostLayer::scanPluginPaths(const std::vector<std::string>& paths, bool append, int formatFlags, PluginScanProgressCallback progress)
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
        if (progress)
            progress("No plugin folders found", {}, 0, 0);
        return static_cast<int>(discoveredPlugins.size());
    }

    report << "Folders:\n";
    for (const auto& path : paths)
        report << "  " << path << "\n";

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    std::vector<PluginSummary> keptSummaries;
    std::vector<juce::PluginDescription> keptDescriptions;
    std::vector<unsigned char> keptLazyDescriptions;
    int removedMissing = 0;
    int removedOutsideFolders = 0;
    int removedLazy = 0;

    for (size_t index = 0; index < impl->descriptions.size(); ++index)
    {
        const auto& description = impl->descriptions[index];
        const auto lazy = index < impl->lazyDescriptions.size() ? impl->lazyDescriptions[index] : static_cast<unsigned char>(0);
        if (lazy != 0)
        {
            ++removedLazy;
            continue;
        }

        if (!pluginFileExists(description))
        {
            ++removedMissing;
            continue;
        }

        if (!append && !pluginIsInAnyPath(description, paths))
        {
            ++removedOutsideFolders;
            continue;
        }

        keptDescriptions.push_back(description);
        keptSummaries.push_back(toSummary(description));
        keptLazyDescriptions.push_back(lazy);
    }

    impl->descriptions = std::move(keptDescriptions);
    impl->lazyDescriptions = std::move(keptLazyDescriptions);
    discoveredPlugins = std::move(keptSummaries);

    std::map<std::string, CachedPluginCandidate> keptCheckedCandidates;
    int removedCheckedCandidates = 0;
    for (const auto& [key, candidate] : impl->checkedCandidates)
    {
        const juce::File candidateFile(juce::String::fromUTF8(candidate.path.c_str()));
        if (candidate.path.empty() || !candidateFile.exists() || (!append && !candidateIsInAnyPath(candidate, paths)))
        {
            ++removedCheckedCandidates;
            continue;
        }

        keptCheckedCandidates[key] = candidate;
    }

    impl->checkedCandidates = std::move(keptCheckedCandidates);

    std::set<std::string> seen;

    for (const auto& existing : discoveredPlugins)
        seen.insert(pluginKey(existing));

    std::set<std::string> knownFiles;
    for (const auto& description : impl->descriptions)
    {
        knownFiles.insert(normalizedPluginIdentifier(description.fileOrIdentifier));
        const auto key = pluginCandidateKey(description);
        if (impl->checkedCandidates.find(key) == impl->checkedCandidates.end())
            impl->checkedCandidates[key] = pluginCandidateSignature(description.fileOrIdentifier);
    }

    if (!discoveredPlugins.empty())
        report << "Cached plugin entries kept: " << discoveredPlugins.size() << "\n";
    if (removedMissing > 0)
        report << "Removed missing plugin entries: " << removedMissing << "\n";
    if (removedOutsideFolders > 0)
        report << "Removed entries outside selected folders: " << removedOutsideFolders << "\n";
    if (removedLazy > 0)
        report << "Refreshed name-only cache entries: " << removedLazy << "\n";
    if (removedCheckedCandidates > 0)
        report << "Removed stale checked candidates: " << removedCheckedCandidates << "\n";

    if (scanVst3)
    {
        auto vst3Paths = defaultVst3SearchPaths();
        auto vst2Paths = defaultVst2SearchPaths();
        std::vector<std::string> pathsForVst3;
        for (const auto& path : paths)
        {
            const auto isDefaultVst3Path = pathListContainsFolder(vst3Paths, path);
            const auto isDefaultVst2Path = pathListContainsFolder(vst2Paths, path);
            if (isDefaultVst3Path || !isDefaultVst2Path)
            {
                pathsForVst3.push_back(path);
            }
        }

        if (pathsForVst3.empty())
            pathsForVst3 = paths;

        scanFormatIntoList(
            "VST3",
            pathsForVst3,
            seen,
            knownFiles,
            impl->checkedCandidates,
            discoveredPlugins,
            impl->descriptions,
            impl->lazyDescriptions,
            report,
            progress);
    }

#if ELKA_ENABLE_VST2_HOST && JUCE_INTERNAL_HAS_VST
    if (scanVst2)
    {
        auto vst2Paths = defaultVst2SearchPaths();
        auto vst3Paths = defaultVst3SearchPaths();
        std::vector<std::string> pathsForVst2;
        for (const auto& path : paths)
        {
            const auto isDefaultVst2Path = pathListContainsFolder(vst2Paths, path);
            const auto isDefaultVst3Path = pathListContainsFolder(vst3Paths, path);
            if (isDefaultVst2Path || !isDefaultVst3Path)
            {
                pathsForVst2.push_back(path);
            }
        }

        if (pathsForVst2.empty())
            pathsForVst2 = paths;

        scanFormatIntoList(
            "VST",
            pathsForVst2,
            seen,
            knownFiles,
            impl->checkedCandidates,
            discoveredPlugins,
            impl->descriptions,
            impl->lazyDescriptions,
            report,
            progress);
    }
#endif

    saveCachedPlugins();

    report << "Plugin scan complete: " << discoveredPlugins.size() << " plugin(s).\n";
    scanReport = report.str();
    if (progress)
        progress("Plugin scan complete", std::to_string(discoveredPlugins.size()) + " plugin(s)", static_cast<int>(discoveredPlugins.size()), static_cast<int>(discoveredPlugins.size()));
    return static_cast<int>(discoveredPlugins.size());
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    report << error << "\n";
    scanReport = report.str();
    if (progress)
        progress("Plugin scan failed", error, 0, 0);
    return 0;
#endif
}

void PluginHostLayer::loadCachedPlugins()
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    discoveredPlugins.clear();
    impl->descriptions.clear();
    impl->lazyDescriptions.clear();
    impl->checkedCandidates.clear();

    auto file = pluginCacheFile();
    if (!file.existsAsFile())
        file = legacyPluginCacheFile();

    if (!file.existsAsFile())
        file = legacyVst3PluginCacheFile();

    if (!file.existsAsFile())
        return;

    auto root = juce::XmlDocument::parse(file);
    if (root == nullptr || (!root->hasTagName("ELKA_PLUGIN_CACHE") && !root->hasTagName("ELKA_VST3_CACHE")))
        return;

    std::set<std::string> seen;
    for (auto* child = root->getFirstChildElement(); child != nullptr; child = child->getNextElement())
    {
        if (child->hasTagName("CHECKED_CANDIDATE"))
        {
            CachedPluginCandidate candidate {
                child->getStringAttribute("path").toStdString(),
                parseInt64Attribute(*child, "size"),
                parseInt64Attribute(*child, "modified")
            };

            auto key = child->getStringAttribute("key").toStdString();
            if (key.empty())
            {
                const auto format = child->getStringAttribute("format");
                if (format.isNotEmpty() && !candidate.path.empty())
                    key = pluginCandidateKey(format, juce::String::fromUTF8(candidate.path.c_str()));
            }

            const juce::File fileOnDisk(juce::String::fromUTF8(candidate.path.c_str()));
            if (!key.empty() && !candidate.path.empty() && fileOnDisk.exists())
                impl->checkedCandidates[key] = candidate;
            continue;
        }

        if (child->hasTagName("LAZY_CANDIDATE"))
        {
            const auto path = child->getStringAttribute("path");
            if (path.isEmpty() || !juce::File(path).exists())
                continue;

            auto format = child->getStringAttribute("format");
            if (format.isEmpty())
                format = path.endsWithIgnoreCase(".vst3") ? "VST3" : "VST";

            auto description = lazyDescriptionForCandidate(format, path);
            const auto savedName = child->getStringAttribute("name");
            if (savedName.isNotEmpty())
                description.name = savedName;

            const auto summary = toSummary(description);
            const auto key = pluginKey(summary);
            if (!seen.insert(key).second)
                continue;

            impl->descriptions.push_back(description);
            impl->lazyDescriptions.push_back(1);
            discoveredPlugins.push_back(summary);
            impl->checkedCandidates[pluginCandidateKey(format, path)] = pluginCandidateSignature(path);
            continue;
        }

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
        impl->lazyDescriptions.push_back(0);
        discoveredPlugins.push_back(summary);
        impl->checkedCandidates[pluginCandidateKey(description)] = pluginCandidateSignature(description.fileOrIdentifier);
    }
#endif
}

void PluginHostLayer::saveCachedPlugins() const
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    auto file = pluginCacheFile();
    file.getParentDirectory().createDirectory();

    juce::XmlElement root("ELKA_PLUGIN_CACHE");
    for (size_t index = 0; index < impl->descriptions.size(); ++index)
    {
        const auto& description = impl->descriptions[index];
        const bool lazy = index < impl->lazyDescriptions.size() && impl->lazyDescriptions[index] != 0;
        if (lazy)
        {
            auto* xml = new juce::XmlElement("LAZY_CANDIDATE");
            xml->setAttribute("name", description.name);
            xml->setAttribute("format", description.pluginFormatName);
            xml->setAttribute("path", description.fileOrIdentifier);
            root.addChildElement(xml);
        }
        else
        {
            auto xml = description.createXml();
            if (xml != nullptr)
                root.addChildElement(xml.release());
        }
    }

    for (const auto& [key, candidate] : impl->checkedCandidates)
    {
        if (candidate.path.empty())
            continue;

        const juce::File fileOnDisk(juce::String::fromUTF8(candidate.path.c_str()));
        if (!fileOnDisk.exists())
            continue;

        auto* xml = new juce::XmlElement("CHECKED_CANDIDATE");
        xml->setAttribute("key", key);
        xml->setAttribute("path", candidate.path);
        xml->setAttribute("size", juce::String(candidate.size));
        xml->setAttribute("modified", juce::String(candidate.modified));
        root.addChildElement(xml);
    }

    root.writeTo(file);
#endif
}

bool PluginHostLayer::resolvePluginDescription(size_t index)
{
#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (index >= impl->descriptions.size())
    {
        error = "No plugin selected.";
        return false;
    }

    if (index >= impl->lazyDescriptions.size() || impl->lazyDescriptions[index] == 0)
        return true;

    const auto candidate = impl->descriptions[index];
    const auto identifier = candidate.fileOrIdentifier;
    const auto formatName = candidate.pluginFormatName;

    juce::OwnedArray<juce::PluginDescription> found;
    try
    {
        if (formatName.equalsIgnoreCase("VST3"))
        {
            juce::VST3PluginFormatHeadless format;
            for (const auto& resolutionCandidate : vst3ResolutionCandidates(identifier))
            {
                format.findAllTypesForFile(found, resolutionCandidate);
                if (!found.isEmpty())
                    break;
            }
        }
#if ELKA_ENABLE_VST2_HOST && JUCE_INTERNAL_HAS_VST
        else
        {
            juce::VSTPluginFormatHeadless format;
            format.findAllTypesForFile(found, identifier);
        }
#else
        else
        {
            error = "VST2 disabled in this build.";
            return false;
        }
#endif
    }
    catch (const std::exception& ex)
    {
        error = std::string("Selected plugin failed while reading metadata: ") + ex.what();
        return false;
    }
    catch (...)
    {
        error = "Selected plugin failed while reading metadata.";
        return false;
    }

    if (found.isEmpty() || found[0] == nullptr)
    {
        error = "Selected plugin did not report a loadable VST type.";
        return false;
    }

    impl->descriptions[index] = *found[0];
    impl->lazyDescriptions[index] = 0;
    discoveredPlugins[index] = toSummary(impl->descriptions[index]);
    impl->checkedCandidates[pluginCandidateKey(impl->descriptions[index])] =
        pluginCandidateSignature(impl->descriptions[index].fileOrIdentifier);
    saveCachedPlugins();
    return true;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return false;
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

    if (!resolvePluginDescription(index))
        return false;

    const auto& description = impl->descriptions[index];
    const int preparedSampleRate = std::clamp(sampleRate, 8000, 192000);
    const int preparedBlockSize = realtimePluginBlockSize(maxBlockSize);
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
    int inputLayoutId,
    const std::string& inputLayoutName,
    int outputLayoutId,
    const std::string& outputLayoutName,
    int kind,
    int sourceStart,
    int sourceCount,
    const std::string& initialStateBase64,
    const std::string& initialPresetBase64,
    PluginLoadProgressCallback progress)
{
    error.clear();
    (void)inputLayoutName;
    (void)outputLayoutName;

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (index >= impl->descriptions.size())
    {
        error = "No plugin selected.";
        return -1;
    }

    const auto initialDetail = discoveredPlugins[index].name + " | " + discoveredPlugins[index].fileOrIdentifier;
    if (progress)
        progress("Resolving selected plugin metadata", initialDetail);

    if (!resolvePluginDescription(index))
    {
        if (progress)
            progress("Plugin metadata resolution failed", error);
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
    const auto resolvedDetail = description.name.toStdString() + " | " + description.fileOrIdentifier.toStdString();
    if (progress)
        progress("Creating plugin instance", resolvedDetail);

    const int preparedSampleRate = std::clamp(sampleRate, 8000, 192000);
    const int preparedBlockSize = realtimePluginBlockSize(maxBlockSize);
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
        if (progress)
            progress("Plugin instance creation failed", creationError.toStdString());
        error = creationError.isNotEmpty()
            ? creationError.toStdString()
            : "JUCE could not create the selected plugin instance.";
        return -1;
    }

    try
    {
        if (progress)
            progress("Configuring plugin buses", resolvedDetail);

        const auto inputChoice = makeLayoutChoice(inputLayoutId, requestedMainInputPins);
        const auto outputChoice = makeLayoutChoice(outputLayoutId, requestedOutputPins);
        const auto inputLayout = inputChoice.layout;
        const int sidechainLayoutId = requestedSidechainPins == 1 ? 0 : requestedSidechainPins == 2 ? 1 : 99;
        const auto sidechainLayout = layoutForId(sidechainLayoutId, requestedSidechainPins);
        const auto outputLayout = outputChoice.layout;
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

        auto effectiveInputChoice = inputChoice;
        if (instance->getBusCount(true) > 0)
            effectiveInputChoice = makeActualLayoutChoice(inputChoice, instance->getChannelLayoutOfBus(true, 0));

        auto effectiveOutputChoice = outputChoice;
        if (instance->getBusCount(false) > 0)
            effectiveOutputChoice = makeActualLayoutChoice(outputChoice, instance->getChannelLayoutOfBus(false, 0));

        const int effectiveMainInputPins = std::clamp(effectiveInputChoice.channels, 1, 32 - effectiveSidechainPins);
        const int effectiveOutputPins = std::clamp(effectiveOutputChoice.channels, 1, 32);
        std::string stateRestoreWarning;
        if (!initialPresetBase64.empty())
        {
            if (progress)
                progress("Restoring saved VST3 preset before prepare", resolvedDetail);
            applyPluginPresetBase64(*instance, initialPresetBase64, stateRestoreWarning);
        }
        if (!initialStateBase64.empty())
        {
            if (progress)
                progress("Restoring saved plugin state before prepare", resolvedDetail);
            applyPluginStateBase64(*instance, initialStateBase64, stateRestoreWarning);
        }

        // setPlayConfigDetails() disables non-main buses in JUCE. Keep sidechain
        // buses alive by using the explicit bus layouts configured above.
        if (progress)
            progress("Preparing plugin for audio", resolvedDetail);
        instance->setRateAndBufferSizeDetails(static_cast<double>(preparedSampleRate), preparedBlockSize);
        instance->prepareToPlay(static_cast<double>(preparedSampleRate), preparedBlockSize);

        if (!initialPresetBase64.empty())
        {
            if (progress)
                progress("Restoring saved VST3 preset after prepare", resolvedDetail);
            applyPluginPresetBase64(*instance, initialPresetBase64, stateRestoreWarning);
        }
        if (!initialStateBase64.empty())
        {
            if (progress)
                progress("Restoring saved plugin state after prepare", resolvedDetail);
            applyPluginStateBase64(*instance, initialStateBase64, stateRestoreWarning);
        }

        if (progress)
            progress("Installing plugin into realtime graph", resolvedDetail);
        const auto supportedInputLayouts = encodeLayoutChoices(supportedLayoutsForMainBus(*instance, true, effectiveInputChoice.layout, effectiveOutputChoice.layout, effectiveInputChoice));
        const auto supportedOutputLayouts = encodeLayoutChoices(supportedLayoutsForMainBus(*instance, false, effectiveInputChoice.layout, effectiveOutputChoice.layout, effectiveOutputChoice));
        const int effectiveInputPins = std::clamp(effectiveMainInputPins + effectiveSidechainPins, 1, 32);

        const auto slotIndex = static_cast<size_t>(slot);
        impl->nodeEditors[slotIndex].reset();
        impl->nodeProcessors[slotIndex] =
            std::make_unique<HostedVst3Processor>(std::move(instance), effectiveInputPins, effectiveOutputPins, preparedBlockSize);
        impl->nodeSummaries[slotIndex] = PluginNodeSummary {
            slot,
            discoveredPlugins[index].name,
            kind,
            false,
            effectiveInputPins,
            effectiveMainInputPins,
            effectiveSidechainPins,
            effectiveOutputPins,
            effectiveInputChoice.id,
            effectiveInputChoice.name,
            effectiveInputChoice.id,
            effectiveInputChoice.name,
            effectiveOutputChoice.id,
            effectiveOutputChoice.name,
            supportedInputLayouts,
            supportedOutputLayouts,
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
        if (!stateRestoreWarning.empty())
            error = stateRestoreWarning;

        if (progress)
            progress("Plugin node loaded", resolvedDetail);
    }
    catch (const std::exception& ex)
    {
        error = ex.what();
        if (progress)
            progress("Plugin node load failed", error);
        removePluginNode(slot);
        return -1;
    }
    catch (...)
    {
        error = "The selected plugin failed while preparing.";
        if (progress)
            progress("Plugin node load failed", error);
        removePluginNode(slot);
        return -1;
    }

    return slot;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return -1;
#endif
}

int PluginHostLayer::addSandboxedDiscoveredPluginNode(
    size_t index,
    int sampleRate,
    int maxBlockSize,
    int mainInputPins,
    int sidechainInputPins,
    int outputPins,
    int inputLayoutId,
    const std::string& inputLayoutName,
    int outputLayoutId,
    const std::string& outputLayoutName,
    int kind,
    int sourceStart,
    int sourceCount,
    const std::string& initialStateBase64,
    const std::string& initialPresetBase64,
    PluginLoadProgressCallback progress)
{
    error.clear();
    (void)inputLayoutName;
    (void)outputLayoutName;

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (index >= discoveredPlugins.size())
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

    const auto& summary = discoveredPlugins[index];
    const auto detail = summary.name + " | " + summary.fileOrIdentifier;
    const int preparedSampleRate = std::clamp(sampleRate, 8000, 192000);
    const int preparedBlockSize = realtimePluginBlockSize(maxBlockSize);
    const int requestedMainInputPins = std::clamp(mainInputPins, 1, 32);
    const int requestedSidechainPins = std::clamp(sidechainInputPins, 0, 32 - requestedMainInputPins);
    const int effectiveInputPins = std::clamp(requestedMainInputPins + requestedSidechainPins, 1, 32);
    const int requestedOutputPins = std::clamp(outputPins, 1, 32);

    if (summary.fileOrIdentifier.empty())
    {
        error = "The selected plugin does not have a loadable file identifier.";
        return -1;
    }

    if (progress)
        progress("Starting sandboxed plugin worker", detail);

    auto processor = std::make_unique<SandboxedPluginProcessor>(
        summary.format,
        summary.fileOrIdentifier,
        preparedSampleRate,
        preparedBlockSize,
        maxBlockSize,
        effectiveInputPins,
        requestedOutputPins,
        inputLayoutId,
        outputLayoutId,
        error);

    if (processor == nullptr || !processor->isReady())
    {
        if (error.empty())
            error = "Sandboxed plugin worker did not become ready.";
        if (progress)
            progress("Sandboxed plugin load failed", error);
        return -1;
    }

    std::string stateRestoreWarning;
    if (!initialPresetBase64.empty())
    {
        if (progress)
            progress("Restoring saved sandboxed VST3 preset", detail);
        processor->setPresetBase64(initialPresetBase64, stateRestoreWarning);
    }
    if (!initialStateBase64.empty())
    {
        if (progress)
            progress("Restoring saved sandboxed plugin state", detail);
        processor->setStateBase64(initialStateBase64, stateRestoreWarning);
    }

    if (progress)
        progress("Installing sandboxed plugin into realtime graph", detail);

    const auto slotIndex = static_cast<size_t>(slot);
    impl->nodeEditors[slotIndex].reset();
    impl->nodeProcessors[slotIndex] = std::move(processor);
    impl->nodeSummaries[slotIndex] = PluginNodeSummary {
        slot,
        summary.name + " (sandbox)",
        kind,
        false,
        effectiveInputPins,
        requestedMainInputPins,
        requestedSidechainPins,
        requestedOutputPins,
        inputLayoutId,
        inputLayoutName.empty() ? layoutNameForId(inputLayoutId, requestedMainInputPins) : inputLayoutName,
        inputLayoutId,
        inputLayoutName.empty() ? layoutNameForId(inputLayoutId, requestedMainInputPins) : inputLayoutName,
        outputLayoutId,
        outputLayoutName.empty() ? layoutNameForId(outputLayoutId, requestedOutputPins) : outputLayoutName,
        fallbackLayoutCapability(inputLayoutId, inputLayoutName, requestedMainInputPins),
        fallbackLayoutCapability(outputLayoutId, outputLayoutName, requestedOutputPins),
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
    impl->loadedName = summary.name;
    if (!stateRestoreWarning.empty())
        error = stateRestoreWarning;

    if (progress)
        progress("Sandboxed plugin node loaded", detail);
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
    auto* processor = dynamic_cast<HostedVst3Processor*>(impl->nodeProcessors[index].get());
    if (processor == nullptr || processor->pluginInstance() == nullptr)
    {
        if (auto* sandboxed = dynamic_cast<SandboxedPluginProcessor*>(impl->nodeProcessors[index].get()))
        {
            if (sandboxed->openEditor(error))
                return true;

            if (error.empty())
                error = "Sandboxed VST editor could not be opened.";
            return false;
        }

        error = "No VST node is selected.";
        return false;
    }

    auto opened = runOnJuceMessageThread([&]() -> bool
    {
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
                impl->nodeEditors[index]->bringToFront();
            }

            return true;
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
    });

    if (!opened && error.empty())
        error = "The plugin editor could not be opened on the JUCE message thread.";

    return opened;
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

std::string PluginHostLayer::pluginNodeStateBase64(int slot)
{
    error.clear();

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
    {
        error = "No VST node is selected.";
        return {};
    }

    auto* realtime = impl->nodeProcessors[static_cast<size_t>(slot)].get();
    if (auto* processor = dynamic_cast<HostedVst3Processor*>(realtime))
    {
        if (processor->pluginInstance() == nullptr)
        {
            error = "VST plugin instance was not found.";
            return {};
        }

        std::string stateBase64;
        if (!capturePluginStateBase64(*processor->pluginInstance(), stateBase64, error))
            return {};

        return stateBase64;
    }

    if (auto* sandboxed = dynamic_cast<SandboxedPluginProcessor*>(realtime))
        return sandboxed->stateBase64(error);

    error = "No VST node is selected.";
    return {};
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return {};
#endif
}

bool PluginHostLayer::setPluginNodeStateBase64(int slot, const std::string& stateBase64)
{
    error.clear();

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
    {
        error = "No VST node is selected.";
        return false;
    }

    if (stateBase64.empty())
        return true;

    auto* realtime = impl->nodeProcessors[static_cast<size_t>(slot)].get();
    if (auto* processor = dynamic_cast<HostedVst3Processor*>(realtime))
    {
        if (processor->pluginInstance() == nullptr)
        {
            error = "VST plugin instance was not found.";
            return false;
        }

        return applyPluginStateBase64(*processor->pluginInstance(), stateBase64, error);
    }

    if (auto* sandboxed = dynamic_cast<SandboxedPluginProcessor*>(realtime))
        return sandboxed->setStateBase64(stateBase64, error);

    error = "No VST node is selected.";
    return false;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return false;
#endif
}

std::string PluginHostLayer::pluginNodePresetBase64(int slot)
{
    error.clear();

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
    {
        error = "No VST node is selected.";
        return {};
    }

    auto* realtime = impl->nodeProcessors[static_cast<size_t>(slot)].get();
    if (auto* sandboxed = dynamic_cast<SandboxedPluginProcessor*>(realtime))
        return sandboxed->presetBase64(error);

    auto* processor = dynamic_cast<HostedVst3Processor*>(realtime);
    if (processor == nullptr || processor->pluginInstance() == nullptr)
    {
        error = "No VST node is selected.";
        return {};
    }

    try
    {
        std::string presetBase64;
        std::string presetError;
        if (!capturePluginPresetBase64(*processor->pluginInstance(), presetBase64, presetError))
        {
            error = presetError;
            return {};
        }

        return presetBase64;
    }
    catch (const std::exception& ex)
    {
        error = ex.what();
    }
    catch (...)
    {
        error = "The VST3 preset data could not be saved.";
    }

    return {};
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return {};
#endif
}

bool PluginHostLayer::setPluginNodePresetBase64(int slot, const std::string& presetBase64)
{
    error.clear();

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
    {
        error = "No VST node is selected.";
        return false;
    }

    auto* realtime = impl->nodeProcessors[static_cast<size_t>(slot)].get();
    if (auto* sandboxed = dynamic_cast<SandboxedPluginProcessor*>(realtime))
        return sandboxed->setPresetBase64(presetBase64, error);

    auto* processor = dynamic_cast<HostedVst3Processor*>(realtime);
    if (processor == nullptr || processor->pluginInstance() == nullptr)
    {
        error = "No VST node is selected.";
        return false;
    }

    if (presetBase64.empty())
        return true;

    try
    {
        std::string presetError;
        const auto restored = applyPluginPresetBase64(*processor->pluginInstance(), presetBase64, presetError);
        if (!restored)
            error = presetError;

        return restored;
    }
    catch (const std::exception& ex)
    {
        error = ex.what();
    }
    catch (...)
    {
        error = "The VST3 preset data could not be restored.";
    }

    return false;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return false;
#endif
}

std::string PluginHostLayer::pluginNodeParameterStateBase64(int slot)
{
    error.clear();

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
    {
        error = "No VST node is selected.";
        return {};
    }

    auto* realtime = impl->nodeProcessors[static_cast<size_t>(slot)].get();
    if (auto* sandboxed = dynamic_cast<SandboxedPluginProcessor*>(realtime))
        return sandboxed->parameterStateBase64(error);

    auto* processor = dynamic_cast<HostedVst3Processor*>(realtime);
    if (processor == nullptr || processor->pluginInstance() == nullptr)
    {
        error = "No VST node is selected.";
        return {};
    }

    std::string parameterError;
    auto parameterState = capturePluginParameterStateBase64(*processor->pluginInstance(), parameterError);
    if (parameterState.empty() && !parameterError.empty())
        error = parameterError;
    return parameterState;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return {};
#endif
}

bool PluginHostLayer::setPluginNodeParameterStateBase64(int slot, const std::string& parameterStateBase64)
{
    error.clear();

#if ELKA_ENABLE_JUCE_PLUGIN_HOST
    if (slot < 0 || slot >= MaxPluginNodes)
    {
        error = "No VST node is selected.";
        return false;
    }

    auto* realtime = impl->nodeProcessors[static_cast<size_t>(slot)].get();
    if (auto* sandboxed = dynamic_cast<SandboxedPluginProcessor*>(realtime))
        return sandboxed->setParameterStateBase64(parameterStateBase64, error);

    auto* processor = dynamic_cast<HostedVst3Processor*>(realtime);
    if (processor == nullptr || processor->pluginInstance() == nullptr)
    {
        error = "No VST node is selected.";
        return false;
    }

    if (parameterStateBase64.empty())
        return true;

    std::string parameterError;
    const auto restored = applyPluginParameterStateBase64(*processor->pluginInstance(), parameterStateBase64, parameterError);
    if (!restored)
        error = parameterError;
    return restored;
#else
    error = "JUCE is not available. Put JUCE in external/JUCE and rebuild.";
    return false;
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
