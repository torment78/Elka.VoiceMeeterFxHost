#pragma once

#include <charconv>
#include <cstdint>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>
#include <type_traits>

namespace elka
{
// The separator cannot occur in a Windows filename. Ordinary plugin paths stay unchanged.
inline constexpr std::string_view PluginContainerMarker = "|elka-shell-v1|";

struct PluginContainerIdentity
{
    std::string path;
    std::optional<uint32_t> uid;
};

inline PluginContainerIdentity parsePluginContainerIdentity(std::string_view identifier)
{
    const auto marker = identifier.find(PluginContainerMarker);
    if (marker == std::string_view::npos)
        return { std::string(identifier), std::nullopt };

    const auto value = identifier.substr(marker + PluginContainerMarker.size());
    uint32_t uid = 0;
    const auto parsed = std::from_chars(value.data(), value.data() + value.size(), uid, 16);
    if (marker == 0 || value.size() != 8 || parsed.ec != std::errc() || parsed.ptr != value.data() + value.size())
        throw std::invalid_argument("Invalid shared-plugin identifier. Rescan the plugin folder.");

    return { std::string(identifier.substr(0, marker)), uid };
}

inline std::string pluginContainerIdentifier(const std::string& path, uint32_t uid)
{
    char digits[8];
    constexpr char hex[] = "0123456789abcdef";
    for (int i = 7; i >= 0; --i)
    {
        digits[i] = hex[uid & 15];
        uid >>= 4;
    }
    return path + std::string(PluginContainerMarker) + std::string(digits, 8);
}

template <typename Descriptions>
auto selectContainerPlugin(const Descriptions& descriptions, const std::optional<uint32_t>& uid)
    -> std::remove_cv_t<std::remove_reference_t<decltype(descriptions[0])>>
{
    for (auto* description : descriptions)
        if (description != nullptr && (!uid || static_cast<uint32_t>(description->uniqueId) == *uid))
            return description;
    return nullptr;
}
}
