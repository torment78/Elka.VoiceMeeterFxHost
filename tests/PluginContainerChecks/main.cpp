#include "plugins/PluginContainerIdentity.h"
#include <algorithm>
#include <iostream>
#include <vector>

int main()
{
    int checks = 0;
    const auto check = [&](bool condition, const char* message) {
        if (!condition) throw std::runtime_error(message);
        ++checks;
    };
    try
    {
        const std::string path = "C:\\Plugins\\WaveShell1-VST3 12.7_x64.vst3";
        check(!elka::parsePluginContainerIdentity(path).uid, "ordinary paths remain compatible");
        for (const uint32_t uid : { 0u, 1u, 0x12345678u, 0x80000000u, 0xffffffffu })
        {
            const auto parsed = elka::parsePluginContainerIdentity(elka::pluginContainerIdentifier(path, uid));
            check(parsed.path == path && parsed.uid == uid, "unsigned identity round trip");
        }
        for (const auto* bad : { "", "123", "gggggggg", "123456789", "1234567 " })
        {
            bool rejected = false;
            try { elka::parsePluginContainerIdentity(path + std::string(elka::PluginContainerMarker) + bad); }
            catch (const std::invalid_argument&) { rejected = true; }
            check(rejected, "malformed identity must not select the first plugin");
        }
        struct Description { int32_t uniqueId; } first { 11 }, second { 22 }, third { -1 };
        std::vector<Description*> descriptions { &first, &second, &third };
        check(elka::selectContainerPlugin(descriptions, 22u) == &second, "non-first plugin selection");
        check(elka::selectContainerPlugin(descriptions, 0xffffffffu) == &third, "negative JUCE ID preserves bits");
        check(elka::selectContainerPlugin(descriptions, 33u) == nullptr, "missing plugin cannot fall back");
        check(elka::selectContainerPlugin(descriptions, std::nullopt) == &first, "ordinary loading unchanged");
        std::reverse(descriptions.begin(), descriptions.end());
        check(elka::selectContainerPlugin(descriptions, 11u) == &first, "factory order does not change identity");
        descriptions.clear();
        check(elka::selectContainerPlugin(descriptions, 11u) == nullptr, "empty shell handled");
        std::cout << "PASS: " << checks << " plugin container identity/selection checks\n";
        return 0;
    }
    catch (const std::exception& ex)
    {
        std::cerr << ex.what() << '\n';
        return 1;
    }
}
