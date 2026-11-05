add_executable(SandboxPoolChecks EXCLUDE_FROM_ALL "${CMAKE_CURRENT_LIST_DIR}/main.cpp")
target_include_directories(SandboxPoolChecks PRIVATE src "${ELKA_VST2_SDK_PATH}")
target_compile_definitions(SandboxPoolChecks PRIVATE ELKA_ENABLE_JUCE_PLUGIN_HOST=1 ELKA_ENABLE_VST2_HOST=1
    JUCE_PLUGINHOST_VST=1 JUCE_PLUGINHOST_VST3=1 JUCE_WEB_BROWSER=0 JUCE_USE_CURL=0 NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(SandboxPoolChecks PRIVATE juce::juce_audio_processors Avrt Psapi)
