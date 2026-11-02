using System.Runtime.InteropServices;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class AudioThreadPriorityDiagnostics
{
    internal delegate int ReadPriority(int[] before, int[] after, int count, out int mode);
    private const int Unseen = -9999;
    private static readonly string[] StreamNames = ["InputInsert", "OutputInsert", "Main", "InsertAsio"];
    private readonly int[] _before = new int[4];
    private readonly int[] _after = new int[4];
    private readonly Action<string> _log;
    private readonly ReadPriority _read;
    private int _reported;
    private bool _unavailable;

    public AudioThreadPriorityDiagnostics(Action<string> log, ReadPriority? read = null)
    {
        _log = log;
        _read = read ?? ElkaFx_GetAudioThreadPriorityInfo;
    }

    // Called only by the existing control/UI status poll, including while in tray.
    public void Poll()
    {
        if (_unavailable || _reported == 15) return;
        try
        {
            if (_read(_before, _after, 4, out var mode) != 4) return;
            for (var stream = 0; stream < 4; stream++)
            {
                var bit = 1 << stream;
                if ((_reported & bit) != 0 || _before[stream] == Unseen || _after[stream] == Unseen) continue;
                var modeName = mode switch { 1 => "off", 2 => "legacy", _ => "guarded" };
                _log($"Audio thread priority stream={StreamNames[stream]} before={_before[stream]} after={_after[stream]} mode={modeName}");
                _reported |= bit;
            }
        }
        catch (DllNotFoundException) { _unavailable = true; }
        catch (EntryPointNotFoundException) { _unavailable = true; }
    }

    [DllImport("ElkaVoiceMeeterFxHost.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_GetAudioThreadPriorityInfo(
        [Out] int[] before, [Out] int[] after, int count, out int mode);
}
