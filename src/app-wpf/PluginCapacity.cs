namespace Elka.VoiceMeeterFxHost.App;

internal static class PluginCapacity
{
    // Matches HostedPluginCapacity in plugins/PluginCapacity.h.
    internal const int Slots = 200;
    internal const int DspRows = 4 * (Slots + 1);
    internal const int LevelValues = Slots * 32 * 2;
}
