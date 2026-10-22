using System.Globalization;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed record HostStatusSnapshot(bool Running, int SampleRate, int BlockSize,
    double? CpuPercent, long? PrivateWorkingSetBytes, double? EnginePeakMicroseconds, string Detail)
{
    public static HostStatusSnapshot Empty { get; } = new(false, 0, 0, null, null, null, "Starting native engine...");
    public string StateText => Running ? "Running" : "Stop";
    public string StateColor => Running ? "#55C27A" : "#E15F5F";
    public string SampleRateText => SampleRate > 0 ? (SampleRate / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + " kHz" : "--";
    public string BufferText => BlockSize > 0 ? BlockSize.ToString(CultureInfo.InvariantCulture) + " samples" : "--";
    public string CycleText => SampleRate > 0 && BlockSize > 0
        ? (1000d * BlockSize / SampleRate).ToString("0.000", CultureInfo.InvariantCulture) + " ms" : "--";
    public string CpuText => CpuPercent is { } cpu ? cpu.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "--";
    public string RamText => PrivateWorkingSetBytes is { } bytes
        ? (bytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture) + " MB" : "--";
    public string EnginePeakText => EnginePeakMicroseconds is { } peak ? peak.ToString("0", CultureInfo.InvariantCulture) + " us" : "--";
}
