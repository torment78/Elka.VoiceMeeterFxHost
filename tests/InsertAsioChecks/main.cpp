#include "insert_asio/InsertAsioHost.h"
#include "engine/RealtimeEngine.h"
#include <iostream>
#include <stdexcept>

namespace
{
int checks = 0;
void check(bool condition, const char* message)
{
    ++checks;
    if (!condition) throw std::runtime_error(message);
}

struct Counters
{
    int creates = 0, scans = 0, opens = 0, instances = 0, failOpens = 0;
    juce::AudioIODeviceCallback* callback = nullptr;
};

class FakeDevice final : public juce::AudioIODevice
{
public:
    FakeDevice(const juce::String& name, Counters& counters)
        : AudioIODevice(name, "ASIO"), counts(counters), channels(name.contains("Potato") ? 34 : 22)
    {
        check(++counts.instances == 1, "never instantiate overlapping ASIO devices");
    }
    ~FakeDevice() override { --counts.instances; }
    juce::StringArray getOutputChannelNames() override { return names(); }
    juce::StringArray getInputChannelNames() override { return names(); }
    juce::Array<double> getAvailableSampleRates() override { return { 44100.0, 48000.0 }; }
    juce::Array<int> getAvailableBufferSizes() override { return { 128, 256, 512 }; }
    int getDefaultBufferSize() override { return 512; }
    juce::String open(const juce::BigInteger&, const juce::BigInteger&, double rate, int block) override
    {
        ++counts.opens;
        if (counts.failOpens > 0) { --counts.failOpens; return "simulated open failure"; }
        opened = true; sampleRate = rate; blockSize = block; return {};
    }
    void close() override { opened = false; }
    bool isOpen() override { return opened; }
    void start(juce::AudioIODeviceCallback* callback) override
    {
        counts.callback = callback; playing = true; callback->audioDeviceAboutToStart(this);
    }
    void stop() override
    {
        if (counts.callback) counts.callback->audioDeviceStopped();
        counts.callback = nullptr; playing = false;
    }
    bool isPlaying() override { return playing; }
    juce::String getLastError() override { return {}; }
    int getCurrentBufferSizeSamples() override { return blockSize; }
    double getCurrentSampleRate() override { return sampleRate; }
    int getCurrentBitDepth() override { return 32; }
    juce::BigInteger getActiveOutputChannels() const override { return {}; }
    juce::BigInteger getActiveInputChannels() const override { return {}; }
    int getOutputLatencyInSamples() override { return 0; }
    int getInputLatencyInSamples() override { return 0; }
private:
    juce::StringArray names() const
    {
        juce::StringArray result;
        for (int i = 0; i < channels; ++i) result.add(juce::String(i));
        return result;
    }
    Counters& counts;
    int channels, blockSize = 256;
    double sampleRate = 48000;
    bool opened = false, playing = false;
};

class FakeType final : public juce::AudioIODeviceType
{
public:
    explicit FakeType(Counters& counters) : AudioIODeviceType("ASIO"), counts(counters) {}
    void scanForDevices() override { ++counts.scans; }
    juce::StringArray getDeviceNames(bool) const override
    {
        return { "VoiceMeeter Insert Virtual ASIO", "VoiceMeeter Potato Insert Virtual ASIO" };
    }
    int getDefaultDeviceIndex(bool) const override { return 0; }
    int getIndexOfDevice(juce::AudioIODevice*, bool) const override { return 0; }
    bool hasSeparateInputsAndOutputs() const override { return false; }
    juce::AudioIODevice* createDevice(const juce::String& output, const juce::String&) override
    {
        ++counts.creates; return new FakeDevice(output, counts);
    }
private:
    Counters& counts;
};
}

int main()
{
    try
    {
        Counters counts;
        auto engine = std::make_unique<elka::RealtimeEngine>();
        elka::InsertAsioHost host(*engine, std::make_unique<FakeType>(counts));
        std::wstring status, name;
        int inputs = 0, outputs = 0;
        check(!host.driverInfo(name, inputs, outputs), "no invented cache before detection");
        check(host.detectDriver(34, status), "startup detection succeeds");
        check(counts.scans == 1 && counts.creates == 2 && counts.opens == 0, "detect scans candidates once without opening");
        check(host.driverInfo(name, inputs, outputs) && name.find(L"Potato") != name.npos && inputs == 34 && outputs == 34, "cache has matching driver and counts");
        check(host.start(48000, 256, 34, status), "cached start succeeds");
        check(counts.scans == 1 && counts.creates == 3 && counts.opens == 1, "start uses one real instance, no probes");
        check(host.probe(34, status) && status.find(L"ASIO Patch is running; showing the active driver. Stop ASIO Patch to re-detect.") == 0 &&
              status.find(L"34 in / 34 out") != status.npos, "legacy probe safely returns cached driver information while running");
        check(!host.detectDriver(34, status), "explicit detection refused while running");
        check(counts.scans == 1 && counts.creates == 3, "neither active call scans nor creates");
        counts.callback->audioDeviceStopped();
        check(!host.isRunning() && host.isOpen(), "simulate stopped but still open driver");
        check(host.probe(22, status) && !host.detectDriver(22, status), "open device also protected");
        check(!host.start(48000, 256, 22, status), "open stopped device must be closed first");
        check(counts.scans == 1 && counts.creates == 3, "open guards perform no driver creation");
        host.stop(status);
        counts.failOpens = 1;
        check(host.start(48000, 256, 34, status), "cached open failure falls back to detection");
        check(counts.scans == 2 && counts.creates == 7 && counts.opens == 3, "fallback destroys failed instance before probing and reopening");
        host.stop(status);
        check(host.start(48000, 256, 22, status), "edition change detects matching driver");
        check(counts.scans == 3 && counts.creates == 10, "mismatched cache scanned once");
        check(host.driverInfo(name, inputs, outputs) && inputs == 22 && outputs == 22, "Banana counts returned");
        host.stop(status);
        check(host.driverInfo(name, inputs, outputs) && inputs == 22, "stopping retains cache");
        check(!host.start(0, 256, 22, status) && counts.creates == 10, "invalid rate does not instantiate driver");
        std::cout << "PASS: " << checks << " Insert ASIO control checks; fake drivers only, no audio callbacks invoked.\n";
        return 0;
    }
    catch (const std::exception& ex) { std::cerr << ex.what() << '\n'; return 1; }
}
