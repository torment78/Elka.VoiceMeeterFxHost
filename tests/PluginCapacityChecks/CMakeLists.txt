set(capacity_test_dir "${CMAKE_CURRENT_LIST_DIR}")
set(capacity_host_source "${PROJECT_SOURCE_DIR}/src/plugins/PluginHostLayer.cpp")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${capacity_host_source}")
file(READ "${capacity_host_source}" isolated_host)
# Compile the production host with only its cache root redirected. No user's
# cache, settings, installed plugins, audio devices or Remote API are touched.
string(REPLACE
    "juce::File::getSpecialLocation(juce::File::userApplicationDataDirectory)"
    "juce::File::getSpecialLocation(juce::File::currentExecutableFile).getParentDirectory().getChildFile(\"capacity-test-data\")"
    isolated_host "${isolated_host}")
file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/CapacityTestPluginHost.cpp" CONTENT "${isolated_host}" @ONLY)

add_library(CapacityProbeVst MODULE EXCLUDE_FROM_ALL "${capacity_test_dir}/ProbeVst.cpp")
target_include_directories(CapacityProbeVst PRIVATE "${ELKA_VST2_SDK_PATH}")

add_executable(PluginCapacityChecks EXCLUDE_FROM_ALL "${capacity_test_dir}/main.cpp"
    "${CMAKE_CURRENT_BINARY_DIR}/CapacityTestPluginHost.cpp" src/engine/RealtimeEngine.cpp)
target_include_directories(PluginCapacityChecks PRIVATE src "${ELKA_VST2_SDK_PATH}")
target_compile_definitions(PluginCapacityChecks PRIVATE ELKA_ENABLE_JUCE_PLUGIN_HOST=1 ELKA_ENABLE_VST2_HOST=1
    JUCE_PLUGINHOST_VST=1 JUCE_PLUGINHOST_VST3=1 JUCE_WEB_BROWSER=0 JUCE_USE_CURL=0 NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(PluginCapacityChecks PRIVATE juce::juce_audio_processors Avrt)
add_dependencies(PluginCapacityChecks CapacityProbeVst)

# Size-only reference: no host is constructed and no code runs at the old limit.
file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/capacity-baseline/plugins/PluginCapacity.h"
    CONTENT "#pragma once\nnamespace elka { inline constexpr int HostedPluginCapacity = 16; }\n" @ONLY)
add_executable(PluginCapacityBaselineFootprint EXCLUDE_FROM_ALL "${capacity_test_dir}/Footprint.cpp")
target_include_directories(PluginCapacityBaselineFootprint PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/capacity-baseline" src)
target_compile_definitions(PluginCapacityBaselineFootprint PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
