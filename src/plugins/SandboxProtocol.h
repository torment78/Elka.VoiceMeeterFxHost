#pragma once

#include <cstddef>
#include <windows.h>

namespace elka::sandbox
{
inline constexpr DWORD SandboxMagic = 0x414B4C45; // ELKA
inline constexpr DWORD SandboxVersion = 2;
inline constexpr int SandboxHeaderBytes = 128;
inline constexpr int SandboxStateBytes = 8 * 1024 * 1024;
inline constexpr int SandboxMaxPins = 32;
inline constexpr LONG SandboxCommandAudio = 1;
inline constexpr LONG SandboxCommandOpenEditor = 2;
inline constexpr LONG SandboxCommandGetState = 3;
inline constexpr LONG SandboxCommandSetState = 4;
inline constexpr LONG SandboxCommandGetPreset = 5;
inline constexpr LONG SandboxCommandSetPreset = 6;
inline constexpr LONG SandboxCommandGetParameters = 7;
inline constexpr LONG SandboxCommandSetParameters = 8;
inline constexpr LONG SandboxCommandGetParameterInfo = 9;
inline constexpr LONG SandboxCommandSetControl = 10;
inline constexpr LONG SandboxCommandCloseEditor = 11;

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
    volatile LONG audioFailed = 0;
    LONG reservedAudio[5] {};
    volatile LONG controlRequestId = 0;
    volatile LONG controlResponseId = 0;
    volatile LONG controlStatus = 0;
    volatile LONG controlCommand = 0;
    volatile LONG textByteCount = 0;
    LONG textCapacity = 0;
    volatile LONG editorOpen = 0;
    LONG reservedControl[9] {};
};

static_assert(sizeof(SandboxAudioHeader) == SandboxHeaderBytes);
static_assert(offsetof(SandboxAudioHeader, requestId) == 28);
static_assert(offsetof(SandboxAudioHeader, responseId) == 32);
static_assert(offsetof(SandboxAudioHeader, controlRequestId) == 64);
static_assert(offsetof(SandboxAudioHeader, textCapacity) == 84);
}
