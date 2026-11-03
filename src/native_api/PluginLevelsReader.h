#pragma once

#include "engine/PluginLevels.h"
#include <thread>

namespace elka
{
class PluginLevelsReader
{
public:
    void attach(PluginLevels& levels) noexcept { levels_.store(&levels); }
    void detach() noexcept
    {
        levels_.store(nullptr);
        while (readers_.load() != 0) std::this_thread::yield();
    }
    int watches(const uint64_t* masks, int count) noexcept
    {
        Access access(*this);
        return !access.levels ? -1 : access.levels->watches(masks, count) ? 0 : -2;
    }
    int read(float* peaks, int count) noexcept
    {
        Access access(*this);
        return !access.levels ? -1 : access.levels->read(peaks, count) ? 0 : -2;
    }
private:
    struct Access
    {
        PluginLevelsReader& owner;
        PluginLevels* levels;
        explicit Access(PluginLevelsReader& reader) noexcept : owner(reader)
        {
            owner.readers_.fetch_add(1);
            levels = owner.levels_.load();
        }
        ~Access() { owner.readers_.fetch_sub(1); }
    };
    std::atomic<PluginLevels*> levels_ {};
    std::atomic<uint32_t> readers_ {};
};
}
