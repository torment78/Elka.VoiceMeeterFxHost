#include <windows.h>
#include <pluginterfaces/vst2.x/aeffectx.h>
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>

namespace
{
struct Probe
{
    AEffect effect {};
    std::atomic<float> gain { 0.5f };
    float chunk = 0.5f;
    ERect bounds {0, 0, 120, 260};
    HWND editor = nullptr;
    HWND dialog = nullptr;
    DWORD captureDelay = 0;
    bool throwOnAudio = false;
    HANDLE captureStarted = nullptr;
    Probe();
    ~Probe() { if (captureStarted) CloseHandle(captureStarted); if (dialog) DestroyWindow(dialog); if (editor) DestroyWindow(editor); }
};
VstIntPtr VSTCALLBACK dispatch(AEffect* effect, VstInt32 opcode, VstInt32, VstIntPtr length, void* data, float)
{
    auto& self = *static_cast<Probe*>(effect->object);
    switch (opcode)
    {
        case effClose: delete &self; return 1;
        case effGetVstVersion: return 2400;
        case effGetPlugCategory: return kPlugCategEffect;
        case effGetEffectName: std::strcpy(static_cast<char*>(data), "Native worker probe"); return 1;
        case effGetProductString: std::strcpy(static_cast<char*>(data), "Native worker probe"); return 1;
        case effGetVendorString: std::strcpy(static_cast<char*>(data), "Elka test"); return 1;
        case effGetProgramName: std::strcpy(static_cast<char*>(data), "Default"); return 1;
        case effGetParamName: std::strcpy(static_cast<char*>(data), "Mix"); return 1;
        case effGetParamDisplay: std::snprintf(static_cast<char*>(data), 8, "%.2f", self.gain.load()); return 1;
        case effGetChunk:
            if (self.captureStarted) SetEvent(self.captureStarted);
            if (self.captureDelay)
            {
                const auto end = GetTickCount64() + self.captureDelay;
                while (GetTickCount64() < end)
                {
                    MSG message {};
                    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
                    { TranslateMessage(&message); DispatchMessageW(&message); }
                    Sleep(5);
                }
            }
            self.chunk = self.gain.load(); *static_cast<void**>(data) = &self.chunk; return sizeof(float);
        case effSetChunk:
            if (length != sizeof(float) || !data) return 0;
            std::memcpy(&self.chunk, data, sizeof(float)); self.gain.store(self.chunk); return 1;
        case effEditGetRect: *static_cast<ERect**>(data) = &self.bounds; return 1;
        case effEditOpen:
            self.editor = CreateWindowExW(0, L"STATIC", L"Synthetic plugin editor", WS_CHILD | WS_VISIBLE,
                0, 0, 260, 120, static_cast<HWND>(data), nullptr, GetModuleHandleW(nullptr), nullptr);
            // Exercise the same unowned top-level dialog path used by activation windows.
            self.dialog = CreateWindowExW(WS_EX_TOOLWINDOW, L"STATIC", L"Elka test plugin dialog", WS_OVERLAPPED | WS_VISIBLE,
                20, 20, 160, 60, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
            return 1;
        case effEditClose:
            if (self.dialog) DestroyWindow(self.dialog);
            if (self.editor) DestroyWindow(self.editor);
            self.editor = self.dialog = nullptr;
            return 1;
        default: return 0;
    }
}
void VSTCALLBACK process(AEffect* effect, float** input, float** output, VstInt32 frames)
{
    if (static_cast<Probe*>(effect->object)->throwOnAudio) throw 42;
    const auto gain = static_cast<Probe*>(effect->object)->gain.load();
    for (int pin = 0; pin < 2; ++pin)
        for (int i = 0; i < frames; ++i) output[pin][i] = input[pin][i] * gain;
}
void VSTCALLBACK setParameter(AEffect* effect, VstInt32, float value) { static_cast<Probe*>(effect->object)->gain.store(value); }
float VSTCALLBACK getParameter(AEffect* effect, VstInt32) { return static_cast<Probe*>(effect->object)->gain.load(); }
Probe::Probe()
{
    wchar_t setting[512] {};
    if (GetEnvironmentVariableW(L"ELKA_TEST_COLD_LOAD_MS", setting, 512))
        Sleep(std::clamp(_wtoi(setting), 0, 40000));
    throwOnAudio = GetEnvironmentVariableW(L"ELKA_TEST_THROW_ON_AUDIO", setting, 512) && _wtoi(setting) == 1;
    if (GetEnvironmentVariableW(L"ELKA_TEST_ACTIVATION_DELAY_MS", setting, 512))
    {
        const auto delay = std::clamp(_wtoi(setting), 0, 60000);
        auto window = CreateWindowExW(WS_EX_TOOLWINDOW, L"STATIC", L"Synthetic activation test",
            WS_OVERLAPPED | WS_VISIBLE, 20, 20, 240, 70, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        Sleep(delay);
        if (window) DestroyWindow(window);
    }
    if (GetEnvironmentVariableW(L"ELKA_TEST_CAPTURE_DELAY_MS", setting, 512))
        captureDelay = std::clamp(_wtoi(setting), 0, 5000);
    if (GetEnvironmentVariableW(L"ELKA_TEST_CAPTURE_STARTED", setting, 512))
        captureStarted = OpenEventW(EVENT_MODIFY_STATE, FALSE, setting);
    effect.magic = kEffectMagic;
    effect.dispatcher = dispatch;
    effect.processReplacing = process;
    effect.setParameter = setParameter;
    effect.getParameter = getParameter;
    effect.numParams = effect.numPrograms = 1;
    effect.numInputs = effect.numOutputs = 2;
    effect.flags = effFlagsCanReplacing | effFlagsProgramChunks | effFlagsHasEditor;
    effect.object = this;
    effect.uniqueID = 0x454c4e57;
    effect.version = 1;
}
}
extern "C" __declspec(dllexport) AEffect* VSTPluginMain(audioMasterCallback) { return &(new Probe)->effect; }
