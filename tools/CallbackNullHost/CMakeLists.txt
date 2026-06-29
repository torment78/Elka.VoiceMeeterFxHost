cmake_minimum_required(VERSION 3.20)

project(ElkaVoiceMeeterCallbackNullHost
    VERSION 1.0.0
    DESCRIPTION "Standalone VoiceMeeter Remote API callback null host diagnostic"
    LANGUAGES CXX)

add_executable(ElkaVoiceMeeterCallbackNullHost
    CallbackNullHost.cpp)

target_compile_features(ElkaVoiceMeeterCallbackNullHost PRIVATE cxx_std_20)

target_compile_definitions(ElkaVoiceMeeterCallbackNullHost PRIVATE
    UNICODE
    _UNICODE
    NOMINMAX
    WIN32_LEAN_AND_MEAN)

target_link_libraries(ElkaVoiceMeeterCallbackNullHost PRIVATE
    Advapi32
    Avrt)

if (MSVC)
    target_compile_options(ElkaVoiceMeeterCallbackNullHost PRIVATE
        /W4
        /permissive-
        /EHsc)
endif()
