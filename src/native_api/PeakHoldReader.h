#pragma once

#include "engine/PeakHold.h"
#include <mutex>
#include <thread>

namespace elka
{
// Reads never take the host/plugin mutex. Detach waits only on the control thread,
// before destroying the engine. Sequential consistency closes the load/free race.
class PeakHoldReader
{
public:
    void attach(PeakHold& hold) noexcept { hold_.store(&hold); }
    void detach() noexcept
    {
        hold_.store(nullptr);
        while (readers_.load() != 0) std::this_thread::yield();
    }
    int read(int stream, bool output, int first, int count, float* maximum, uint32_t* overs) noexcept
    {
        Access access(*this);
        return !access.hold ? -1 : access.hold->read(stream, output, first, count, maximum, overs) ? 0 : -2;
    }
    int watch(int stream, bool output, int first, int count, bool on)
    {
        std::lock_guard lock(watchMutex_);
        Access access(*this);
        return !access.hold ? -1 : access.hold->watch(stream, output, first, count, on) ? 0 : -2;
    }
    int reset(int stream, bool output, int first, int count) noexcept
    {
        Access access(*this);
        return !access.hold ? -1 : access.hold->reset(stream, output, first, count) ? 0 : -2;
    }
private:
    struct Access
    {
        PeakHoldReader& owner;
        PeakHold* hold;
        explicit Access(PeakHoldReader& reader) noexcept : owner(reader)
        {
            owner.readers_.fetch_add(1);
            hold = owner.hold_.load();
        }
        ~Access() { owner.readers_.fetch_sub(1); }
    };
    std::atomic<PeakHold*> hold_ {};
    std::atomic<uint32_t> readers_ {};
    std::mutex watchMutex_;
};
}
