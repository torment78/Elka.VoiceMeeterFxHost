#include "engine/AudioThreadPriority.h"
#include <windows.h>
#include <avrt.h>
#include <atomic>
#include <cstdlib>
#include <cwchar>
#include <iostream>
#include <new>
#include <thread>

namespace
{
struct Calls
{
    int priority = 0, reads = 0, sets = 0, pro = 0, audio = 0, high = 0;
    bool failPro = false, failAll = false, checking = false;
};
thread_local Calls calls;
std::atomic<int> environmentReads {0}, reverts {0}, errors {0}, allocations {0};
int checks = 0;
void check(bool ok, const char* message)
{
    ++checks;
    if (!ok) { std::cerr << "FAIL: " << message << '\n'; std::exit(1); }
}
}

void* operator new(std::size_t size)
{
    if (calls.checking) ++allocations;
    if (void* value = std::malloc(size == 0 ? 1 : size)) return value;
    throw std::bad_alloc();
}
void operator delete(void* value) noexcept { std::free(value); }
void operator delete(void* value, std::size_t) noexcept { std::free(value); }

DWORD GetEnvironmentVariableW(const wchar_t* name, wchar_t* output, DWORD capacity)
{
    ++environmentReads;
    if (std::wcscmp(name, L"ELKA_AUDIO_THREAD_PRIORITY") != 0) ++errors;
    const wchar_t* value = _wgetenv(name);
    if (!value) return 0;
    const auto length = static_cast<DWORD>(std::wcslen(value));
    if (length >= capacity) return length + 1;
    std::wmemcpy(output, value, length + 1);
    return length;
}
HANDLE GetCurrentThread() { return &calls; }
int GetThreadPriority(HANDLE thread)
{
    if (thread != &calls) ++errors;
    ++calls.reads;
    return calls.priority;
}
int SetThreadPriority(HANDLE thread, int value)
{
    if (thread != &calls || value != THREAD_PRIORITY_HIGHEST) ++errors;
    ++calls.sets;
    calls.priority = value;
    return 1;
}
HANDLE AvSetMmThreadCharacteristicsW(const wchar_t* name, DWORD* index)
{
    if (*index != 0) ++errors;
    *index = 42;
    if (std::wcscmp(name, L"Pro Audio") == 0)
    {
        ++calls.pro;
        return calls.failPro || calls.failAll ? nullptr : &calls;
    }
    if (std::wcscmp(name, L"Audio") != 0) ++errors;
    ++calls.audio;
    return calls.failAll ? nullptr : &calls;
}
int AvSetMmThreadPriority(HANDLE handle, int value)
{
    if (handle != &calls || value != AVRT_PRIORITY_HIGH) ++errors;
    ++calls.high;
    return 1;
}
int AvRevertMmThreadCharacteristics(HANDLE handle)
{
    if (handle != &calls) ++errors;
    ++reverts;
    return 1;
}

int main(int argc, char** argv)
{
    using namespace elka;
    check(argc == 2, "expected mode argument");
    const int expectedMode = std::atoi(argv[1]);
    int before[5] {}, after[5] {}, mode = -1;
    before[4] = after[4] = 12345;
    check(environmentReads == 1, "environment read during initialization exactly once");
    check(getAudioThreadPriorityInfo(before, after, 5, &mode) == 4 && mode == expectedMode, "mode and record count");
    for (int i = 0; i < 4; ++i)
        check(before[i] == -9999 && after[i] == -9999, "unseen sentinel");
    check(before[4] == 12345 && after[4] == 12345, "export bounds");
    check(getAudioThreadPriorityInfo(nullptr, after, 4, &mode) == -1, "null before");
    check(getAudioThreadPriorityInfo(before, nullptr, 4, &mode) == -1, "null after");
    check(getAudioThreadPriorityInfo(before, after, 3, &mode) == -1, "short buffer");
    check(getAudioThreadPriorityInfo(before, after, 4, nullptr) == -1, "null mode");
    ensureAudioThreadPriorityOnce(-1);
    ensureAudioThreadPriorityOnce(4);
    check(calls.reads == 0, "invalid stream does not initialize thread guard");

    // Changing the environment after DLL-style initialization must not change the mode.
    _wputenv_s(L"ELKA_AUDIO_THREAD_PRIORITY", expectedMode == 1 ? L"legacy" : L"off");
    for (int priority : {15, 2, 1, 0, -1, THREAD_PRIORITY_ERROR_RETURN})
    {
        for (int failure = 0; failure < 3; ++failure)
        {
            const bool join = expectedMode == 2 || (expectedMode == 0 && priority < 2);
            const int previousReverts = reverts.load();
            std::thread worker([&] {
                calls.priority = priority;
                calls.failPro = failure == 1;
                calls.failAll = failure == 2;
                calls.checking = true;
                ensureAudioThreadPriorityOnce(0);
                for (int repeat = 0; repeat < 10000; ++repeat)
                    for (int stream = 0; stream < 4; ++stream) ensureAudioThreadPriorityOnce(stream);
                calls.checking = false;
                check(calls.reads == 2, "read before and after only once per thread");
                check(calls.pro == (join ? 1 : 0), "one guarded MMCSS attempt per thread");
                check(calls.audio == (join && failure != 0 ? 1 : 0), "Audio fallback only after failed Pro Audio");
                check(calls.high == (join && failure != 2 ? 1 : 0), "AVRT high only on an owned handle");
                check(calls.sets == (expectedMode == 2 ? 1 : 0), "SetThreadPriority restricted to legacy");
                getAudioThreadPriorityInfo(before, after, 4, &mode);
                check(mode == expectedMode, "environment changes ignored after load");
                for (int stream = 0; stream < 4; ++stream)
                    check(before[stream] == priority && after[stream] == (expectedMode == 2 ? 2 : priority),
                        "every stream on a shared thread has before/after data");
            });
            worker.join();
            check(reverts == previousReverts + (join && failure != 2 ? 1 : 0), "owned MMCSS handle reverted at thread exit");
        }
    }
    check(environmentReads == 1, "no audio-thread environment read");
    check(allocations == 0, "no helper C++ heap allocations");
    check(errors == 0, "API arguments and same-thread cleanup");
    std::cout << checks << " audio thread priority checks passed, mode=" << expectedMode
              << ". Simulated OS calls; no audio device opened.\n";
}
