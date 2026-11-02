#pragma once

namespace elka
{
enum class AudioThreadPriorityMode : int { Guarded = 0, Off = 1, Legacy = 2 };
constexpr int AudioThreadPriorityStreams = 4;
constexpr int AudioThreadPriorityUnseen = -9999;

void ensureAudioThreadPriorityOnce(int streamIndex) noexcept;
// Returns four records, or -1 for invalid buffers. Does not initialize the engine.
int getAudioThreadPriorityInfo(int* before, int* after, int count, int* mode) noexcept;
}
