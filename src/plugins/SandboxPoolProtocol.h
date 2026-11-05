#pragma once

#include "SandboxProtocol.h"
#include "PluginContainerIdentity.h"
#include <algorithm>
#include <cctype>
#include <filesystem>
#include <string>

namespace elka::sandbox
{
inline constexpr LONG PoolMagic = 0x504b4c45;
inline constexpr LONG PoolAddInstance = 1;
inline constexpr LONG PoolRemoveInstance = 2;
enum class Mode { Auto = 0, Shared = 1, OwnProcess = 2 };

struct PoolHeader
{
    LONG magic = PoolMagic;
    LONG version = SandboxVersion;
    volatile LONG requestId = 0;
    volatile LONG responseId = 0;
    volatile LONG status = 0;
    volatile LONG command = 0;
    LONG instanceId = 0;
    LONG reserved[9] {};
    LONG sampleRate = 48000;
    LONG maxSamples = 8192;
    LONG inputPins = 2;
    LONG outputPins = 2;
    LONG inputLayout = 1;
    LONG outputLayout = 1;
    wchar_t format[16] {};
    wchar_t identifier[32768] {};
    wchar_t instanceBase[128] {};
    char error[1024] {};
};
static_assert(offsetof(PoolHeader, sampleRate) == 64);
static_assert(offsetof(PoolHeader, format) == 88);

inline std::string normalizedGroupPart(std::string value)
{
    std::replace(value.begin(), value.end(), '\\', '/');
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) return {};
    return value.substr(first, value.find_last_not_of(" \t\r\n") - first + 1);
}

inline std::string groupKey(Mode mode, const std::string& identifier,
                            const std::string& manufacturer, const std::string& uniqueId)
{
    if (mode == Mode::OwnProcess) return "own:" + uniqueId;
    if (mode == Mode::Shared) return "shared";
    const auto identity = parsePluginContainerIdentity(identifier);
    std::error_code error;
    const auto canonical = std::filesystem::weakly_canonical(std::filesystem::path(std::u8string(identity.path.begin(), identity.path.end())), error).u8string();
    const auto path = error ? identity.path : std::string(canonical.begin(), canonical.end());
    if (identity.uid) return "container:" + normalizedGroupPart(path);
    const auto vendor = normalizedGroupPart(manufacturer);
    return vendor.empty() ? "file:" + normalizedGroupPart(path) : "vendor:" + vendor;
}
}
