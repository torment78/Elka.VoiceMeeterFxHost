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
                       std::function<void()> closeEditor)
        : juce::DocumentWindow(
              title,
              juce::Colours::black,
              juce::DocumentWindow::closeButton | juce::DocumentWindow::minimiseButton,
              true),
          onClose(std::move(closeEditor))
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
        setVisible(true);
        bringToFront();
    }

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
    std::function<void()> onClose;
};
}
