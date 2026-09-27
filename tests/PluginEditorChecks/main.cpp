#include "plugins/PluginEditorWindow.h"
#include <iostream>
#include <memory>

namespace
{
int checks = 0;

void check(bool condition, const char* name)
{
    if (!condition)
        throw std::runtime_error(name);
    ++checks;
}

struct EditorCounts
{
    int created = 0;
    int destroyed = 0;
};

class TestEditor final : public juce::AudioProcessorEditor
{
public:
    TestEditor(juce::AudioProcessor& plugin, EditorCounts& countsIn)
        : AudioProcessorEditor(plugin), counts(countsIn)
    {
        ++counts.created;
        setSize(420, 280);
    }

    ~TestEditor() override
    {
        processor.editorBeingDeleted(this);
        ++counts.destroyed;
    }

private:
    EditorCounts& counts;
};

class TestPlugin final : public juce::AudioPluginInstance
{
public:
    explicit TestPlugin(EditorCounts& countsIn) : counts(countsIn) {}
    const juce::String getName() const override { return "Editor lifecycle check"; }
    void fillInPluginDescription(juce::PluginDescription&) const override {}
    void prepareToPlay(double, int) override {}
    void releaseResources() override { ++releaseCalls; }
    void processBlock(juce::AudioBuffer<float>& audio, juce::MidiBuffer&) override
    {
        audio.applyGain(gain);
        ++processedBlocks;
    }
    double getTailLengthSeconds() const override { return 0; }
    bool acceptsMidi() const override { return false; }
    bool producesMidi() const override { return false; }
    bool hasEditor() const override { return true; }
    juce::AudioProcessorEditor* createEditor() override { return new TestEditor(*this, counts); }
    int getNumPrograms() override { return 1; }
    int getCurrentProgram() override { return 0; }
    void setCurrentProgram(int) override {}
    const juce::String getProgramName(int) override { return {}; }
    void changeProgramName(int, const juce::String&) override {}
    void getStateInformation(juce::MemoryBlock&) override {}
    void setStateInformation(const void*, int) override {}

    float gain = 0.25f;
    int releaseCalls = 0;
    int processedBlocks = 0;

private:
    EditorCounts& counts;
};

void drainMessages()
{
    bool drained = false;
    juce::MessageManager::callAsync([&] { drained = true; });
    const auto deadline = juce::Time::getMillisecondCounterHiRes() + 2000;
    while (!drained && juce::Time::getMillisecondCounterHiRes() < deadline)
    {
        MSG message {};
        while (PeekMessage(&message, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&message);
            DispatchMessage(&message);
        }
        juce::Thread::sleep(1);
    }
    check(drained, "Queued editor close completes on the message thread");
}

void checkAudio(TestPlugin& plugin)
{
    juce::AudioBuffer<float> audio(2, 16);
    juce::MidiBuffer midi;
    audio.clear();
    audio.setSample(0, 0, 1.0f);
    audio.setSample(1, 0, -1.0f);
    plugin.processBlock(audio, midi);
    check(audio.getSample(0, 0) == 0.25f && audio.getSample(1, 0) == -0.25f,
          "Processor state and sample processing survive editor close");
    check(plugin.releaseCalls == 0, "Editor close never releases audio processing resources");
}

void checkOwnerLifecycle()
{
    EditorCounts counts;
    TestPlugin plugin(counts);
    std::unique_ptr<elka::PluginEditorWindow> editor;
    int closeCalls = 0;
    auto open = [&]
    {
        editor = std::make_unique<elka::PluginEditorWindow>(plugin, plugin.getName(), [&]
        {
            check(juce::MessageManager::getInstance()->isThisTheMessageThread(),
                  "Editor is destroyed on its UI thread");
            ++closeCalls;
            editor.reset();
        });
    };

    open();
    check(plugin.getActiveEditor() != nullptr && counts.created == 1, "Editor is active on open");
    checkAudio(plugin);
    editor->closeButtonPressed();
    check(editor != nullptr && counts.destroyed == 0, "Destruction waits until close handler returns");
    check(!editor->isVisible(), "X hides the editor immediately");
    editor->closeButtonPressed();
    drainMessages();
    check(!editor && counts.destroyed == 1 && closeCalls == 1, "Repeated X closes release the editor exactly once");
    check(plugin.getActiveEditor() == nullptr, "Processor no longer retains an active editor");
    checkAudio(plugin);

    open();
    check(counts.created == 2 && plugin.getActiveEditor() != nullptr, "Closed editor can be recreated");
    checkAudio(plugin);
    editor->closeButtonPressed();
    editor.reset();
    open();
    auto* replacement = editor.get();
    drainMessages();
    check(editor.get() == replacement && closeCalls == 1, "Stale close cannot destroy a replacement editor");

    editor->closeButtonPressed();
    editor.reset();
    drainMessages();
    check(closeCalls == 1 && counts.created == counts.destroyed,
          "Explicit close or node removal cancels deferred editor access");
    checkAudio(plugin);

    for (int cycle = 0; cycle < 5; ++cycle)
    {
        open();
        editor->closeButtonPressed();
        drainMessages();
        check(!editor && counts.created == counts.destroyed, "Repeated open and close leaves no editors alive");
    }
    checkAudio(plugin);
}
}

int main()
{
    try
    {
        juce::ScopedJuceInitialiser_GUI gui;
        checkOwnerLifecycle();
        std::cout << checks << " plugin editor checks passed\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAILED: " << error.what() << '\n';
        return 1;
    }
}
