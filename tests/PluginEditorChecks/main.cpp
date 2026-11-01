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
    check(elka::PluginEditorWindow::isOpenForProcess(GetCurrentProcessId(), &plugin), "Editor query finds the selected plugin window");
    check(elka::PluginEditorWindow::isOpenForProcess(GetCurrentProcessId()), "Worker-style query finds its editor without audio IPC");
    TestPlugin other(counts);
    check(!elka::PluginEditorWindow::isOpenForProcess(GetCurrentProcessId(), &other), "Another plugin cannot match the editor");
    check(!elka::PluginEditorWindow::isOpenForProcess(0), "Invalid process cannot match an editor");
    checkAudio(plugin);
    editor->closeButtonPressed();
    check(editor != nullptr && counts.destroyed == 0, "Destruction waits until close handler returns");
    check(!editor->isVisible(), "X hides the editor immediately");
    check(!elka::PluginEditorWindow::isOpenForProcess(GetCurrentProcessId(), &plugin), "Manual X is reflected before deferred destruction");
    editor->closeButtonPressed();
    drainMessages();
    check(!editor && counts.destroyed == 1 && closeCalls == 1, "Repeated X closes release the editor exactly once");
    check(plugin.getActiveEditor() == nullptr, "Processor no longer retains an active editor");
    check(!elka::PluginEditorWindow::isOpenForProcess(GetCurrentProcessId()), "No editor remains after close");
    checkAudio(plugin);

    open();
    check(counts.created == 2 && plugin.getActiveEditor() != nullptr, "Closed editor can be recreated");
    check(elka::PluginEditorWindow::isOpenForProcess(GetCurrentProcessId(), &plugin), "Reopened editor is detected again");
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

void checkPlacement()
{
    EditorCounts counts;
    TestPlugin plugin(counts);
    const auto directory = juce::File::getCurrentWorkingDirectory().getChildFile("artifacts/verification")
        .getNonexistentChildFile("ElkaEditorPlacementChecks", {}, false);
    check(directory.createDirectory().wasOk(), "Placement test directory is writable");
    const auto file = directory.getChildFile("editor.bounds");
    const auto other = directory.getChildFile("other.bounds");
    juce::Rectangle<int> expected;
    {
        elka::PluginEditorWindow window(plugin, "First", [] {}, file);
        window.setBounds(110, 140, 620, 430);
        expected = window.getBounds();
        check(!file.exists(), "Placement is not written continuously while the editor is open");
        checkAudio(plugin);
    }
    check(file.existsAsFile(), "Closing the editor saves its position and size");
    {
        elka::PluginEditorWindow window(plugin, "First", [] {}, file);
        check(window.getBounds() == expected, "Recreated editor restores the exact position and size");
        window.setBounds(160, 180, 720, 480);
        expected = window.getBounds();
        window.closeButtonPressed();
    }
    drainMessages();
    {
        elka::PluginEditorWindow window(plugin, "First", [] {}, file);
        check(window.getBounds() == expected, "X-close also saves the latest geometry");
        check(!other.exists(), "Separate editor placements are independent");
    }
    file.replaceWithText("100000 100000 620 430");
    {
        elka::PluginEditorWindow window(plugin, "First", [] {}, file);
        auto screens = juce::Desktop::getInstance().getDisplays().getRectangleList(true);
        screens.clipTo(window.getBounds());
        check(!screens.isEmpty(), "Off-screen editor is restored to a connected monitor");
    }
    file.replaceWithText("invalid bounds");
    {
        elka::PluginEditorWindow window(plugin, "First", [] {}, file);
        check(window.getWidth() >= 420 && window.getHeight() >= 280, "Invalid geometry retains default editor dimensions");
    }
    check(elka::PluginEditorWindow::placementFileFor(plugin, "ID 1") !=
        elka::PluginEditorWindow::placementFileFor(plugin, "ID 2"), "Each VST instance has a separate placement key");
    checkAudio(plugin);
    check(directory.deleteRecursively(), "Temporary placement test data removed");
}
}

int main()
{
    try
    {
        juce::ScopedJuceInitialiser_GUI gui;
        checkOwnerLifecycle();
        checkPlacement();
        std::cout << checks << " plugin editor checks passed\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAILED: " << error.what() << '\n';
        return 1;
    }
}
