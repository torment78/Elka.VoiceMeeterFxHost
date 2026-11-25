#pragma once

#include <juce_audio_processors/juce_audio_processors.h>

#include <algorithm>
#include <cmath>

namespace elka
{
// Built-in routing utility hosted like a VST node. One stereo input feeds
// either the A output pair (pins 0/1) or the B output pair (pins 2/3).
// A short equal-gain ramp avoids clicks when the path changes.
class PathSwitchProcessor final : public juce::AudioPluginInstance
{
public:
    static constexpr const char* FormatName = "Internal";
    static constexpr const char* Identifier = "elka:path-switch";
    static constexpr const char* DisplayName = "Path Switch";

    PathSwitchProcessor()
        : juce::AudioPluginInstance(BusesProperties()
              .withInput("Input", juce::AudioChannelSet::stereo(), true)
              .withOutput("Output", juce::AudioChannelSet::discreteChannels(4), true))
    {
        // AudioPluginInstance hides addParameter from hosts; this instance owns its own parameter.
        juce::AudioProcessor::addParameter(path =new juce::AudioParameterChoice(juce::ParameterID { "path", 1 }, "Path", juce::StringArray { "A", "B" }, 0));
    }

    static juce::PluginDescription description()
    {
        juce::PluginDescription result;
        result.name = DisplayName;
        result.descriptiveName = "Sends one stereo input to path A or path B";
        result.pluginFormatName = FormatName;
        result.category = "Utility";
        result.manufacturerName = "ElkaSoft";
        result.version = "1.0";
        result.fileOrIdentifier = Identifier;
        result.uniqueId = 0x454c5053; // 'ELPS'
        result.deprecatedUid = result.uniqueId;
        result.isInstrument = false;
        result.numInputChannels = 2;
        result.numOutputChannels = 4;
        return result;
    }

    static bool isPathSwitch(const juce::PluginDescription& candidate)
    {
        return candidate.pluginFormatName.equalsIgnoreCase(FormatName) &&
               candidate.fileOrIdentifier.equalsIgnoreCase(Identifier);
    }

    void fillInPluginDescription(juce::PluginDescription& target) const override { target = description(); }

    const juce::String getName() const override { return DisplayName; }

    bool isBusesLayoutSupported(const BusesLayout& layouts) const override
    {
        return layouts.getMainInputChannels() == 2 && layouts.getMainOutputChannels() == 4;
    }

    void prepareToPlay(double sampleRate, int) override
    {
        rampStep = 1.0f / std::max(1.0f, static_cast<float>(sampleRate) * 0.01f); // 10 ms
        mixB = targetMixB();
    }

    void releaseResources() override {}

    void processBlock(juce::AudioBuffer<float>& buffer, juce::MidiBuffer&) override
    {
        const int samples = buffer.getNumSamples();
        if (buffer.getNumChannels() < 4)
            return;

        auto* inL = buffer.getWritePointer(0);
        auto* inR = buffer.getWritePointer(1);
        auto* bL = buffer.getWritePointer(2);
        auto* bR = buffer.getWritePointer(3);
        const float target = targetMixB();

        // In-place: channels 0/1 carry the input and become path A.
        for (int sample = 0; sample < samples; ++sample)
        {
            if (mixB < target)
                mixB = std::min(target, mixB + rampStep);
            else if (mixB > target)
                mixB = std::max(target, mixB - rampStep);

            const float left = inL[sample];
            const float right = inR[sample];
            const float gainA = 1.0f - mixB;
            bL[sample] = left * mixB;
            bR[sample] = right * mixB;
            inL[sample] = left * gainA;
            inR[sample] = right * gainA;
        }
    }

    using juce::AudioPluginInstance::processBlock;

    double getTailLengthSeconds() const override { return 0.0; }
    bool acceptsMidi() const override { return false; }
    bool producesMidi() const override { return false; }

    juce::AudioProcessorEditor* createEditor() override { return new juce::GenericAudioProcessorEditor(*this); }
    bool hasEditor() const override { return true; }

    int getNumPrograms() override { return 1; }
    int getCurrentProgram() override { return 0; }
    void setCurrentProgram(int) override {}
    const juce::String getProgramName(int) override { return {}; }
    void changeProgramName(int, const juce::String&) override {}

    void getStateInformation(juce::MemoryBlock& destination) override
    {
        juce::MemoryOutputStream stream(destination, false);
        stream.writeInt(StateMagic);
        stream.writeInt(path->getIndex());
    }

    void setStateInformation(const void* data, int sizeInBytes) override
    {
        if (data == nullptr || sizeInBytes < 8)
            return;

        juce::MemoryInputStream stream(data, static_cast<size_t>(sizeInBytes), false);
        if (stream.readInt() != StateMagic)
            return;

        const int index = std::clamp(stream.readInt(), 0, 1);
        *path = index;
    }

private:
    static constexpr int StateMagic = 0x454c5053;

    float targetMixB() const noexcept { return path != nullptr && path->getIndex() == 1 ? 1.0f : 0.0f; }

    juce::AudioParameterChoice* path = nullptr;
    float mixB = 0.0f;
    float rampStep = 1.0f / 480.0f;
};
} // namespace elka
