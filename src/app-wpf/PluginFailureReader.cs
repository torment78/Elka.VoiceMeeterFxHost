using System.Runtime.InteropServices;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class PluginFailureReader
{
    internal const int SlotCount = PluginCapacity.Slots;
    internal delegate int ReadFailures(int[] flags, int count);
    private readonly ReadFailures _read;
    private readonly int[] _scratch = new int[SlotCount];
    private readonly int[] _flags = new int[SlotCount];
    internal bool Supported { get; private set; } = true;
    internal bool IsFailed(int slot) => slot >= 0 && slot < SlotCount && _flags[slot] != 0;

    internal PluginFailureReader(ReadFailures? read = null) => _read = read ?? ElkaFx_GetPluginNodeFailures;

    internal bool TryRead()
    {
        if (!Supported) return false;
        try
        {
            if (_read(_scratch, SlotCount) != 0) return false;
            Array.Copy(_scratch, _flags, SlotCount);
            return true;
        }
        catch (EntryPointNotFoundException) { Supported = false; }
        catch (DllNotFoundException) { Supported = false; }
        catch (BadImageFormatException) { Supported = false; }
        catch (SEHException) { Supported = false; }
        return false;
    }

    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetPluginNodeFailures([Out] int[] flags, int count);
}
