#include "insert_asio/InsertAsioHost.h"

#include "engine/RealtimeEngine.h"

#include <algorithm>
#include <cmath>
#include <sstream>

namespace elka
{
namespace
{
constexpr int DefaultInsertBlockSize = 512;

int chooseBufferSize(const juce::Array<int>& sizes, int requested, int fallback) noexcept
{
    if (sizes.isEmpty())
        return requested > 0 ? requested : fallback;

    if (requested > 0 && sizes.contains(requested))
        return requested;

    const auto* begin = sizes.begin();
    const auto* end = sizes.end();
    const auto best = std::min_element(begin, end, [requested, fallback](int left, int right) {
        const int target = requested > 0 ? requested : fallback;
        return std::abs(left - target) < std::abs(right - target);
    });

    return best != end ? *best : fallback;
}

double chooseSampleRate(const juce::Array<double>& rates, int requested) noexcept
{
    if (rates.isEmpty())
        return requested > 0 ? static_cast<double>(requested) : 0.0;

    if (requested > 0)
    {
        for (auto rate : rates)
        {
            if (std::abs(rate - static_cast<double>(requested)) < 1.0)
                return rate;
        }

        const auto* begin = rates.begin();
        const auto* end = rates.end();
        const auto best = std::min_element(begin, end, [requested](double left, double right) {
            const double target = static_cast<double>(requested);
            return std::abs(left - target) < std::abs(right - target);
        });

        return best != end ? *best : 0.0;
    }

    return rates[0];
}

juce::BigInteger allChannels(int count)
{
    juce::BigInteger channels;
    for (int channel = 0; channel < count; ++channel)
        channels.setBit(channel);

    return channels;
}
}

InsertAsioHost::InsertAsioHost(RealtimeEngine& engine)
    : realtimeEngine(engine)
{
}

InsertAsioHost::~InsertAsioHost()
{
    std::wstring ignored;
    stop(ignored);
}

bool InsertAsioHost::probe(int expectedChannelCount, std::wstring& status)
{
    std::lock_guard lock(deviceMutex);
    const auto candidate = selectInsertDriverCandidate(expectedChannelCount, status);
    if (candidate.name.isEmpty())
        return false;

    status += L"\nSelected Insert ASIO driver: " + toWide(candidate.name) +
        L" (" + std::to_wstring(candidate.inputChannels) + L" in / " +
        std::to_wstring(candidate.outputChannels) + L" out)";
    return true;
}

bool InsertAsioHost::start(int requestedSampleRate, int requestedBlockSize, int expectedChannelCount, std::wstring& status)
{
    std::lock_guard lock(deviceMutex);

    if (running.load(std::memory_order_acquire))
    {
        status = L"Insert ASIO host already running: " + driverName;
        return true;
    }

    if (requestedSampleRate <= 0)
    {
        status = L"Insert ASIO start failed: VoiceMeeter has not reported a sample rate yet.";
        return false;
    }

    const auto candidate = selectInsertDriverCandidate(expectedChannelCount, status);
    if (candidate.name.isEmpty())
        return false;

    const auto name = candidate.name;
    std::unique_ptr<juce::AudioIODevice> nextDevice(asioType->createDevice(name, name));
    if (nextDevice == nullptr)
    {
        status = L"Could not create Insert ASIO device: " + toWide(name);
        return false;
    }

    const auto inputNames = nextDevice->getInputChannelNames();
    const auto outputNames = nextDevice->getOutputChannelNames();
    const int inputCount = inputNames.size();
    const int outputCount = outputNames.size();
    if (inputCount <= 0 || outputCount <= 0)
    {
        status = L"Insert ASIO driver opened with no usable input/output channels.";
        return false;
    }

    if (expectedChannelCount > 0 && (inputCount != expectedChannelCount || outputCount != expectedChannelCount))
    {
        std::wostringstream warning;
        warning << L"Selected Insert ASIO driver has " << inputCount << L" in / " << outputCount
                << L" out, but this VoiceMeeter profile expects " << expectedChannelCount
                << L" in / " << expectedChannelCount << L" out. " << status;
        status = warning.str();
        return false;
    }

    const auto rate = chooseSampleRate(nextDevice->getAvailableSampleRates(), requestedSampleRate);
    if (rate <= 0.0)
    {
        status = L"Insert ASIO start failed: no usable sample rate was reported by VoiceMeeter or the driver.";
        return false;
    }

    const auto buffer = chooseBufferSize(nextDevice->getAvailableBufferSizes(), requestedBlockSize, DefaultInsertBlockSize);
    const auto error = nextDevice->open(allChannels(inputCount), allChannels(outputCount), rate, buffer);
    if (error.isNotEmpty())
    {
        status = L"Insert ASIO open failed: " + toWide(error);
        return false;
    }

    inputChannels.store(inputCount, std::memory_order_relaxed);
    outputChannels.store(outputCount, std::memory_order_relaxed);
    sampleRate.store(static_cast<int>(std::round(nextDevice->getCurrentSampleRate())), std::memory_order_relaxed);
    blockSize.store(nextDevice->getCurrentBufferSizeSamples(), std::memory_order_relaxed);
    driverName = toWide(name);
    lastError.clear();

    running.store(true, std::memory_order_release);
    nextDevice->start(this);
    if (!running.load(std::memory_order_acquire))
    {
        auto errorText = lastError.empty() ? L"driver stopped immediately after start" : lastError;
        nextDevice->close();
        status = L"Insert ASIO start failed: " + errorText;
        return false;
    }

    device = std::move(nextDevice);

    std::wostringstream message;
    message << L"Insert ASIO host running: " << driverName
            << L" | " << sampleRate.load(std::memory_order_relaxed) << L" Hz"
            << L" | " << blockSize.load(std::memory_order_relaxed) << L" spl"
            << L" | " << inputCount << L" in / " << outputCount << L" out"
            << L" | pass-through";
    status = message.str();
    return true;
}

void InsertAsioHost::stop(std::wstring& status) noexcept
{
    std::lock_guard lock(deviceMutex);
    if (device != nullptr)
    {
        device->stop();
        device->close();
        device.reset();
    }

    running.store(false, std::memory_order_release);
    status = L"Insert ASIO host stopped.";
}

bool InsertAsioHost::isOpen() const
{
    std::lock_guard lock(deviceMutex);
    return device != nullptr;
}

void InsertAsioHost::status(std::wstring& status) const
{
    std::lock_guard lock(deviceMutex);
    if (!running.load(std::memory_order_acquire))
    {
        if (device != nullptr)
        {
            status = lastError.empty()
                ? L"Insert ASIO host stopped while the ASIO device is still open."
                : L"Insert ASIO host stopped while the ASIO device is still open: " + lastError;
        }
        else
        {
            status = lastError.empty()
                ? L"Insert ASIO host stopped."
                : L"Insert ASIO host stopped: " + lastError;
        }

        return;
    }

    std::wostringstream message;
    message << L"Insert ASIO running: " << driverName
            << L" | " << sampleRate.load(std::memory_order_relaxed) << L" Hz"
            << L" | " << blockSize.load(std::memory_order_relaxed) << L" spl"
            << L" | " << inputChannels.load(std::memory_order_relaxed) << L" in / "
            << outputChannels.load(std::memory_order_relaxed) << L" out";
    status = message.str();
}

void InsertAsioHost::audioDeviceIOCallbackWithContext(
    const float* const* inputChannelData,
    int numInputChannels,
    float* const* outputChannelData,
    int numOutputChannels,
    int numSamples,
    const juce::AudioIODeviceCallbackContext&)
{
    const int outputLimit = std::min(numOutputChannels, 128);
    const int inputLimit = std::min(numInputChannels, 128);
    float* readPointers[128] {};
    float* writePointers[128] {};

    for (int channel = 0; channel < inputLimit; ++channel)
        readPointers[channel] = const_cast<float*>(inputChannelData != nullptr ? inputChannelData[channel] : nullptr);

    for (int channel = 0; channel < outputLimit; ++channel)
        writePointers[channel] = outputChannelData != nullptr ? outputChannelData[channel] : nullptr;

    AudioBufferView buffer {
        sampleRate.load(std::memory_order_relaxed),
        numSamples,
        inputLimit,
        outputLimit,
        readPointers,
        writePointers
    };

    realtimeEngine.processInsertAsio(buffer);

    for (int channel = outputLimit; channel < numOutputChannels; ++channel)
    {
        float* output = outputChannelData != nullptr ? outputChannelData[channel] : nullptr;
        if (output != nullptr)
            std::fill(output, output + numSamples, 0.0f);
    }

}

void InsertAsioHost::audioDeviceAboutToStart(juce::AudioIODevice* startedDevice)
{
    if (startedDevice == nullptr)
        return;

    sampleRate.store(static_cast<int>(std::round(startedDevice->getCurrentSampleRate())), std::memory_order_relaxed);
    blockSize.store(startedDevice->getCurrentBufferSizeSamples(), std::memory_order_relaxed);
}

void InsertAsioHost::audioDeviceStopped()
{
    running.store(false, std::memory_order_release);
}

void InsertAsioHost::audioDeviceError(const juce::String& errorMessage)
{
    std::lock_guard lock(deviceMutex);
    lastError = toWide(errorMessage);
}

std::wstring InsertAsioHost::toWide(const juce::String& text)
{
    return text.toWideCharPointer();
}

bool InsertAsioHost::isVoiceMeeterInsertDriverName(const juce::String& name)
{
    const auto lower = name.toLowerCase();
    return lower.contains("voicemeeter") &&
           lower.contains("insert") &&
           lower.contains("asio");
}

std::vector<InsertAsioHost::DriverCandidate> InsertAsioHost::findInsertDriverCandidates(std::wstring& status)
{
    if (asioType == nullptr)
        asioType.reset(juce::AudioIODeviceType::createAudioIODeviceType_ASIO());

    if (asioType == nullptr)
    {
        status = L"JUCE ASIO device type is not available in this build.";
        return {};
    }

    asioType->scanForDevices();
    const auto names = asioType->getDeviceNames();
    std::vector<DriverCandidate> candidates;
    for (const auto& name : names)
    {
        if (!isVoiceMeeterInsertDriverName(name))
            continue;

        DriverCandidate candidate;
        candidate.name = name;

        std::unique_ptr<juce::AudioIODevice> probeDevice(asioType->createDevice(name, name));
        if (probeDevice != nullptr)
        {
            candidate.inputChannels = probeDevice->getInputChannelNames().size();
            candidate.outputChannels = probeDevice->getOutputChannelNames().size();
        }

        candidates.push_back(candidate);
    }

    std::wostringstream message;
    if (candidates.empty())
    {
        message << L"VoiceMeeter Insert ASIO driver not found. ASIO drivers visible: ";
    }
    else
    {
        message << L"VoiceMeeter Insert ASIO candidates:";
        for (const auto& candidate : candidates)
        {
            message << L"\n  " << toWide(candidate.name)
                    << L" (" << candidate.inputChannels << L" in / "
                    << candidate.outputChannels << L" out)";
        }

        status = message.str();
        return candidates;
    }

    for (int index = 0; index < names.size(); ++index)
    {
        if (index > 0)
            message << L", ";
        message << toWide(names[index]);
    }

    status = message.str();
    return {};
}

InsertAsioHost::DriverCandidate InsertAsioHost::selectInsertDriverCandidate(int expectedChannelCount, std::wstring& status)
{
    auto candidates = findInsertDriverCandidates(status);
    if (candidates.empty())
        return {};

    auto exact = std::find_if(candidates.begin(), candidates.end(), [expectedChannelCount](const DriverCandidate& candidate) {
        return expectedChannelCount > 0 &&
               candidate.inputChannels == expectedChannelCount &&
               candidate.outputChannels == expectedChannelCount;
    });
    if (exact != candidates.end())
        return *exact;

    auto best = std::max_element(candidates.begin(), candidates.end(), [](const DriverCandidate& left, const DriverCandidate& right) {
        const auto leftChannels = std::min(left.inputChannels, left.outputChannels);
        const auto rightChannels = std::min(right.inputChannels, right.outputChannels);
        return leftChannels < rightChannels;
    });

    if (best != candidates.end())
        return *best;

    return {};
}
}
