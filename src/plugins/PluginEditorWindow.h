#pragma once

#include <juce_audio_processors/juce_audio_processors.h>
#include <algorithm>
#include <functional>
#include <stdexcept>
#include <utility>

#if JUCE_WINDOWS
#include <windows.h>
#endif

namespace elka
{
class PluginEditorWindow final : public juce::DocumentWindow
{
public:
    PluginEditorWindow(juce::AudioPluginInstance& plugin, const juce::String& title,
                       std::function<void()> closeEditor, juce::File savedPlacement = {})
        : juce::DocumentWindow(
              title,
              juce::Colours::black,
              juce::DocumentWindow::closeButton | juce::DocumentWindow::minimiseButton,
              true),
          onClose(std::move(closeEditor)), placementFile(std::move(savedPlacement))
    {
        auto* editor = plugin.createEditorAndMakeActive();
        if (editor == nullptr)
            throw std::runtime_error("The loaded plugin did not provide an editor window.");

        setUsingNativeTitleBar(true);
        setResizable(true, false);
        setContentOwned(editor, true);

        const int width = std::max(420, editor->getWidth());
        const int height = std::max(280, editor->getHeight());
        centreWithSize(width, height);
        if (placementFile.existsAsFile())
            restoreWindowStateFromString(placementFile.loadFileAsString());
        setVisible(true);
       #if JUCE_WINDOWS
        SetPropW(static_cast<HWND>(getWindowHandle()), EditorProperty, reinterpret_cast<HANDLE>(&plugin));
       #endif
        bringToFront();
    }

    ~PluginEditorWindow() override
    {
        // UI state only. Audio callbacks never access this file or editor bounds.
        if (placementFile != juce::File())
        {
            const auto state = getWindowStateAsString();
            if (state.isNotEmpty() && placementFile.getParentDirectory().createDirectory().wasOk())
                placementFile.replaceWithText(state);
        }
       #if JUCE_WINDOWS
        if (auto hwnd = static_cast<HWND>(getWindowHandle())) RemovePropW(hwnd, EditorProperty);
       #endif
        // Destroy the plugin view while its full host window (including resize
        // controls and virtual callbacks) still exists, not in a base destructor.
        clearContentComponent();
    }

    static juce::File placementFileFor(const juce::AudioPluginInstance& plugin, const juce::String& title)
    {
        const auto local = juce::SystemStats::getEnvironmentVariable("LOCALAPPDATA", {});
        if (local.isEmpty()) return {};
        const auto identity = plugin.getPluginDescription().createIdentifierString() + "|" + title;
        return juce::File(local).getChildFile("ElkaSoft/VoiceMeeterFxHost/EditorWindows")
            .getChildFile(juce::String::toHexString(identity.hashCode64()) + ".bounds");
    }

   #if JUCE_WINDOWS
    // Window-only query: no audio IPC or message-thread wait, including sandboxed editors.
    static bool isOpenForProcess(DWORD process, const void* plugin = nullptr)
    {
        if (process == 0) return false;
        struct Search { DWORD process; const void* plugin; bool found; } search { process, plugin, false };
        EnumWindows([](HWND window, LPARAM data) -> BOOL
        {
            auto& search = *reinterpret_cast<Search*>(data);
            DWORD owner = 0;
            GetWindowThreadProcessId(window, &owner);
            if (owner != search.process || !IsWindowVisible(window)) return TRUE;
            const auto identity = GetPropW(window, EditorProperty);
            if (identity && (!search.plugin || identity == search.plugin))
            {
                search.found = true;
                return FALSE;
            }
            return TRUE;
        }, reinterpret_cast<LPARAM>(&search));
        return search.found;
    }
   #endif

    void closeButtonPressed() override
    {
        setVisible(false);
        // Release the editor after the native close event unwinds. A removed or
        // replaced window must not run a pending close against its former owner.
        juce::Component::SafePointer<PluginEditorWindow> window(this);
        juce::MessageManager::callAsync([window]
        {
            if (window != nullptr)
            {
                auto closeEditor = window->onClose;
                closeEditor();
            }
        });
    }

    void bringToFront()
    {
        toFront(true);
       #if JUCE_WINDOWS
        if (auto* hwnd = static_cast<HWND>(getWindowHandle()))
        {
            ShowWindow(hwnd, SW_SHOWNORMAL);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
       #endif
    }

private:
   #if JUCE_WINDOWS
    inline static constexpr auto EditorProperty = L"ElkaFxHost.PluginEditor";
   #endif
    std::function<void()> onClose;
    juce::File placementFile;
};
}
