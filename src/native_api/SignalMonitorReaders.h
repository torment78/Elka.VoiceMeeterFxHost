#pragma once

#include "native_api/SignalMonitorAnalyzer.h"
#include <limits>
#include <memory>
#include <unordered_map>

namespace elka
{
// Non-realtime access only. Snapshot copies share a small lifecycle lock, not the
// host control lock. Audio capture never acquires either mutex.
class SignalMonitorReaders
{
public:
    // The owner must detach the hub before destroying it; open is serialized with
    // owner creation/shutdown by the control API, but reads and close are independent.
    int open(SignalMonitorHub& hub, int stream, bool output, int first, int channels)
    {
        auto reader = std::make_shared<Reader>(hub);
        std::lock_guard lock(mutex);
        // Do not recycle public handles across host recreation or stale UI readers.
        if (nextHandle == std::numeric_limits<int>::max()) return 0;
        const int tap = hub.open(stream, output, first, channels);
        if (tap == 0) return 0;
        reader->tap = tap;
        const int handle = nextHandle++;
        try { readers.emplace(handle, std::move(reader)); }
        catch (...) { hub.close(tap); throw; }
        return handle;
    }

    void close(int handle)
    {
        std::lock_guard lock(mutex);
        const auto found = readers.find(handle);
        if (found == readers.end()) return;
        found->second->hub.close(found->second->tap);
        readers.erase(found);
    }

    // Called before the engine is destroyed, while all hub members still exist.
    void detach(SignalMonitorHub& hub)
    {
        std::lock_guard lock(mutex);
        for (auto it = readers.begin(); it != readers.end();)
        {
            if (&it->second->hub != &hub) { ++it; continue; }
            hub.close(it->second->tap);
            it = readers.erase(it);
        }
    }

    int read(int handle, float* peaks, int peakCount, float* spectrum, int spectrumCount,
        int* sampleRate, unsigned long long* sequence)
    {
        const bool peaksOnly = spectrum == nullptr && spectrumCount == 0;
        if (!peaks || peakCount != SignalMonitorHub::Channels ||
            (!peaksOnly && (!spectrum || spectrumCount != SignalMonitorAnalyzer::Bins)) ||
            !sampleRate || !sequence) return -1;

        std::unique_lock lock(mutex);
        const auto found = readers.find(handle);
        if (found == readers.end()) return -1;
        auto reader = found->second;
        auto& analyzer = reader->analyzer;
        std::unique_lock readerLock(analyzer.readerMutex, std::try_to_lock);
        if (!readerLock.owns_lock()) return 1;
        if (!reader->hub.read(reader->tap, analyzer.snapshot)) return -1;
        // Only the copied snapshot and retained analyzer are used from here on.
        // Close/shutdown can now release the tap; other readers need not wait for FFT.
        lock.unlock();
        if (analyzer.snapshot.sequence == analyzer.previousSequence) return 1;
        analyzer.previousSequence = analyzer.snapshot.sequence;
        std::copy(analyzer.snapshot.peaks.begin(), analyzer.snapshot.peaks.end(), peaks);
        *sampleRate = analyzer.snapshot.sampleRate;
        *sequence = analyzer.snapshot.sequence;
        if (!peaksOnly) analyzer.spectrum(spectrum);
        return 0;
    }

private:
    struct Reader
    {
        explicit Reader(SignalMonitorHub& owner) : hub(owner) {}
        SignalMonitorHub& hub;
        int tap = 0;
        SignalMonitorAnalyzer analyzer;
    };
    std::mutex mutex;
    std::unordered_map<int, std::shared_ptr<Reader>> readers;
    int nextHandle = 1;
};
}
