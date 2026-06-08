cmake_minimum_required(VERSION 3.24)

project(ElkaVoiceMeeterFxHost
    VERSION 0.1.0
    DESCRIPTION "VoiceMeeter callback-based FX host prototype"
    LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(ELKA_JUCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/external/JUCE")
set(ELKA_VST2_SDK_PATH "" CACHE PATH "Required legacy VST2 SDK folder containing pluginterfaces/vst2.x/aeffect.h and aeffectx.h")
set(ELKA_ENABLE_VST2_HOST OFF)
if (EXISTS "${ELKA_JUCE_DIR}/CMakeLists.txt")
    set(JUCE_MODULES_ONLY ON CACHE BOOL "Build JUCE modules only" FORCE)
    add_subdirectory("${ELKA_JUCE_DIR}" external/JUCE-build EXCLUDE_FROM_ALL)
    set(ELKA_ENABLE_JUCE_PLUGIN_HOST ON)

    if (NOT ELKA_VST2_SDK_PATH)
        message(FATAL_ERROR "VST2 hosting is required. Set ELKA_VST2_SDK_PATH to a legacy VST2 SDK folder, for example external/VST2_SDK, containing pluginterfaces/vst2.x/aeffect.h and pluginterfaces/vst2.x/aeffectx.h.")
    endif()

    if (NOT EXISTS "${ELKA_VST2_SDK_PATH}/pluginterfaces/vst2.x/aeffect.h"
        OR NOT EXISTS "${ELKA_VST2_SDK_PATH}/pluginterfaces/vst2.x/aeffectx.h")
        message(FATAL_ERROR "VST2 hosting is required, but ELKA_VST2_SDK_PATH='${ELKA_VST2_SDK_PATH}' does not contain pluginterfaces/vst2.x/aeffect.h and aeffectx.h.")
    endif()

    set(ELKA_ENABLE_VST2_HOST ON)
    if (COMMAND juce_set_vst2_sdk_path)
        juce_set_vst2_sdk_path("${ELKA_VST2_SDK_PATH}")
    endif()
else()
    message(FATAL_ERROR "JUCE is required for VST2/VST3 hosting. Put JUCE in external/JUCE before configuring this project.")
endif()

set(ELKA_NATIVE_CORE_SOURCES
    src/voicemeeter/VoicemeeterRemoteApi.cpp
    src/voicemeeter/VoicemeeterClient.cpp
    src/engine/RealtimeEngine.cpp
    src/plugins/PluginHostLayer.cpp
)

add_library(ElkaVoiceMeeterFxHost.Native SHARED
    src/native_api/FxHostNativeApi.cpp
    ${ELKA_NATIVE_CORE_SOURCES}
)

foreach(target IN ITEMS ElkaVoiceMeeterFxHost.Native)
    target_include_directories(${target} PRIVATE
        src
    )

    target_compile_definitions(${target} PRIVATE
        UNICODE
        _UNICODE
        NOMINMAX
        WIN32_LEAN_AND_MEAN
        JUCE_WEB_BROWSER=0
        JUCE_USE_CURL=0
        JUCE_DISABLE_ASSERTIONS=1
    )

    target_link_libraries(${target} PRIVATE
        Advapi32
        Comctl32
        Gdi32
        Mmdevapi
        Ole32
        Propsys
        Shell32
        User32
    )
endforeach()

target_compile_definitions(ElkaVoiceMeeterFxHost.Native PRIVATE
    ELKA_VOICEMEETER_FX_HOST_NATIVE_EXPORTS=1
)

if (ELKA_ENABLE_JUCE_PLUGIN_HOST)
    foreach(target IN ITEMS ElkaVoiceMeeterFxHost.Native)
        target_compile_definitions(${target} PRIVATE
            ELKA_ENABLE_JUCE_PLUGIN_HOST=1
            ELKA_ENABLE_VST2_HOST=$<BOOL:${ELKA_ENABLE_VST2_HOST}>
            JUCE_PLUGINHOST_VST3=1
            JUCE_PLUGINHOST_VST=$<BOOL:${ELKA_ENABLE_VST2_HOST}>
        )

        if (ELKA_ENABLE_VST2_HOST)
            target_include_directories(${target} PRIVATE
                "${ELKA_VST2_SDK_PATH}"
            )
        endif()

        target_link_libraries(${target} PRIVATE
            juce::juce_audio_processors
        )
    endforeach()
else()
    foreach(target IN ITEMS ElkaVoiceMeeterFxHost.Native)
        target_compile_definitions(${target} PRIVATE
            ELKA_ENABLE_JUCE_PLUGIN_HOST=0
        )
    endforeach()
endif()

if (MSVC)
    foreach(target IN ITEMS ElkaVoiceMeeterFxHost.Native)
        target_compile_options(${target} PRIVATE
            /W4
            /permissive-
            /EHsc
        )
    endforeach()
endif()
