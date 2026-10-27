namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private void CheckPluginContainerRestore()
    {
        const string path = @"C:\Plugins\WaveShell1-VST3 12.7_x64.vst3";
        var first = new PluginChoice(8, "First Stereo - Waves [VST3]", "VST3", path + PluginIdentity.ContainerMarker + "00000011");
        var second = new PluginChoice(4, "Second Mono - Waves [VST3]", "VST3", path + PluginIdentity.ContainerMarker + "00000022");
        var choices = new[] { second, first };
        var saved = new PluginNodeSnapshot
        {
            PluginIdentifier = first.Identifier, PluginIndex = 4, PluginFormat = "VST3", Name = first.Name
        };
        Assert(SavedPluginChoice(saved, choices) == first, "shell identity beats stale index and order");
        saved.Name = "Renamed by user";
        Assert(SavedPluginChoice(saved, choices) == first, "shell identity survives display-name changes");
        saved.PluginIdentifier = path + PluginIdentity.ContainerMarker + "00000033";
        saved.Name = first.Name;
        Assert(SavedPluginChoice(saved, choices) is null, "missing shell subtype is not substituted by name/index");
        saved.PluginIdentifier = path;
        Assert(SavedPluginChoice(saved, choices) == first, "legacy path-only save migrates by exact plugin name");
        saved.Name = "WaveShell";
        Assert(SavedPluginChoice(saved, choices) is null, "ambiguous legacy shell is not guessed");
        saved.Name = first.Name;
        Assert(SavedPluginChoice(saved, new[] { first, first with { Index = 12, Identifier = second.Identifier } }) is null,
            "duplicate legacy names are not guessed");
        saved.PluginIdentifier = first.Identifier;
        saved.PluginFormat = "VST";
        Assert(SavedPluginChoice(saved, choices) is null, "VST2 and VST3 identities remain separate");
        Assert(PluginIdentity.Canonical(first.Identifier.Replace('\\', '/').ToLowerInvariant()) ==
               PluginIdentity.Canonical(first.Identifier), "shell paths normalize without discarding subtype");
        Assert(PluginIdentity.Canonical(first.Identifier) != PluginIdentity.Canonical(second.Identifier),
            "shared file does not collapse distinct plugin identities");
        var json = System.Text.Json.JsonSerializer.Serialize(first);
        Assert(System.Text.Json.JsonSerializer.Deserialize<PluginChoice>(json) == first, "shell identity survives JSON save/export");
    }
}
