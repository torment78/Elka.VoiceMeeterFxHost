#include "engine/AudioThreadPriority.h"

#include <windows.h>
#include <avrt.h>
#include <atomic>
#include <cwchar>

namespace elka
{
namespace
{
static_assert(std::atomic<int>::is_always_lock_free);
std::atomic<int> beforePriority[AudioThreadPriorityStreams] {
    AudioThreadPriorityUnseen, AudioThreadPriorityUnseen,
    AudioThreadPriorityUnseen, AudioThreadPriorityUnseen
};
std::atomic<int> afterPriority[AudioThreadPriorityStreams] {
    AudioThreadPriorityUnseen, AudioThreadPriorityUnseen,
    AudioThreadPriorityUnseen, AudioThreadPriorityUnseen
};

AudioThreadPriorityMode readModeAtDllLoad() noexcept
{
    wchar_t value[16] {};
    const DWORD length = GetEnvironmentVariableW(L"ELKA_AUDIO_THREAD_PRIORITY", value, 16);
    if (length > 0 && length < 16)
    {
        if (std::wcscmp(value, L"off") == 0) return AudioThreadPriorityMode::Off;
        if (std::wcscmp(value, L"legacy") == 0) return AudioThreadPriorityMode::Legacy;
    }
    return AudioThreadPriorityMode::Guarded;
}

// DLL initialization, not a function-local static or an audio-thread environment read.
const AudioThreadPriorityMode activeMode = readModeAtDllLoad();

class ThreadPriority
{
public:
    ThreadPriority() noexcept
        : before(GetThreadPriority(GetCurrentThread()))
    {
        const bool join = activeMode == AudioThreadPriorityMode::Legacy ||
            (activeMode == AudioThreadPriorityMode::Guarded &&
             before != THREAD_PRIORITY_ERROR_RETURN && before < THREAD_PRIORITY_HIGHEST);
        if (join)
        {
            DWORD taskIndex = 0;
            mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
            if (mmcss == nullptr)
            {
                taskIndex = 0;
                mmcss = AvSetMmThreadCharacteristicsW(L"Audio", &taskIndex);
            }
            if (mmcss != nullptr)
                AvSetMmThreadPriority(mmcss, AVRT_PRIORITY_HIGH);
        }
        if (activeMode == AudioThreadPriorityMode::Legacy)
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
        after = GetThreadPriority(GetCurrentThread());
    }

    ~ThreadPriority() noexcept
    {
        if (mmcss != nullptr) AvRevertMmThreadCharacteristics(mmcss);
    }

    ThreadPriority(const ThreadPriority&) = delete;
    ThreadPriority& operator=(const ThreadPriority&) = delete;

    const int before;
    int after = AudioThreadPriorityUnseen;
    unsigned reportedStreams = 0;

private:
    HANDLE mmcss = nullptr;
};
}

void ensureAudioThreadPriorityOnce(int streamIndex) noexcept
{
    if (streamIndex < 0 || streamIndex >= AudioThreadPriorityStreams) return;
    thread_local ThreadPriority priority;
    const unsigned bit = 1u << streamIndex;
    if ((priority.reportedStreams & bit) != 0) return;

    // VoiceMeeter can deliver multiple stream kinds on one thread. Publish the
    // same before/after observation for each, without another MMCSS registration.
    beforePriority[streamIndex].store(priority.before, std::memory_order_relaxed);
    afterPriority[streamIndex].store(priority.after, std::memory_order_release);
    priority.reportedStreams |= bit;
}

int getAudioThreadPriorityInfo(int* before, int* after, int count, int* mode) noexcept
{
    if (!before || !after || count < AudioThreadPriorityStreams || !mode) return -1;
    *mode = static_cast<int>(activeMode);
    for (int stream = 0; stream < AudioThreadPriorityStreams; ++stream)
    {
        after[stream] = afterPriority[stream].load(std::memory_order_acquire);
        before[stream] = after[stream] == AudioThreadPriorityUnseen
            ? AudioThreadPriorityUnseen : beforePriority[stream].load(std::memory_order_relaxed);
    }
    return AudioThreadPriorityStreams;
}
}
