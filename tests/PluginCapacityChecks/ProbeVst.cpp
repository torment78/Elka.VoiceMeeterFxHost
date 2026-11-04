#include <pluginterfaces/vst2.x/aeffectx.h>
#include <algorithm>
#include <atomic>
#include <cstring>

namespace
{
std::atomic<int> live {}, calls {};
struct Probe
{
    AEffect effect {};
    Probe();
};
VstIntPtr VSTCALLBACK dispatch(AEffect* effect, VstInt32 opcode, VstInt32, VstIntPtr, void* data, float)
{
    switch (opcode)
    {
        case effClose: --live; delete static_cast<Probe*>(effect->object); return 1;
        case effGetVstVersion: return 2400;
        case effGetPlugCategory: return kPlugCategEffect;
        case effGetEffectName: std::strcpy(static_cast<char*>(data), "Capacity probe"); return 1;
        case effGetProductString: std::strcpy(static_cast<char*>(data), "Capacity probe"); return 1;
        case effGetVendorString: std::strcpy(static_cast<char*>(data), "Elka test"); return 1;
        case effGetProgramName: std::strcpy(static_cast<char*>(data), "Default"); return 1;
        default: return 0;
    }
}
void VSTCALLBACK process(AEffect*, float** input, float** output, VstInt32 frames)
{
    ++calls;
    for (int pin = 0; pin < 2; ++pin) std::copy_n(input[pin], frames, output[pin]);
}
Probe::Probe()
{
    effect.magic = kEffectMagic;
    effect.dispatcher = dispatch;
    effect.processReplacing = process;
    effect.numPrograms = 1;
    effect.numInputs = effect.numOutputs = 2;
    effect.flags = effFlagsCanReplacing;
    effect.object = this;
    effect.uniqueID = 0x454c4350;
    effect.version = 1;
    ++live;
}
}
extern "C" __declspec(dllexport) AEffect* VSTPluginMain(audioMasterCallback) { return &(new Probe)->effect; }
extern "C" __declspec(dllexport) int ProbeLive() { return live.load(); }
extern "C" __declspec(dllexport) int ProbeCalls() { return calls.load(); }
