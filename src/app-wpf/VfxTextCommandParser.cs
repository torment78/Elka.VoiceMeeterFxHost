using System.Globalization;
using System.Text.RegularExpressions;

namespace Elka.VoiceMeeterFxHost.App;

internal enum VfxTextCommandTargetKind
{
    Strip,
    Bus,
    Vst
}

internal enum VfxTextCommandProperty
{
    Enable,
    Delay,
    Volume,
    Route,
    RouteEnable,
    RouteMuteNormal,
    Bypass,
    InputGain,
    OutputGain,
    MainGain,
    GainScale,
    DryGain,
    WetGain,
    Mix,
    Width,
    InputPan,
    OutputPan,
    DryPan,
    WetPan,
    Threshold,
    Ratio,
    Attack,
    Release,
    Hold,
    Knee,
    Range,
    MakeupGain,
    Ceiling,
    Lookahead,
    Rate,
    Depth,
    Feedback,
    Drive,
    Tone,
    Mode,
    Ab,
    Program,
    Editor,
    Reload,
    Parameter
}

internal enum VfxTextCommandOperator
{
    Set,
    Add,
    Subtract
}

internal sealed record VfxTextCommand(
    VfxTextCommandTargetKind TargetKind,
    string Target,
    VfxTextCommandChannelSelection Channels,
    VfxTextCommandProperty Property,
    VfxTextCommandOperator Operator,
    string ValueText,
    string SourceText,
    int? PluginParameterIndex = null);

internal sealed record VfxTextCommandChannelSelection(bool IsAll, IReadOnlyList<int> OneBasedChannels)
{
    public IEnumerable<int> GetZeroBasedChannels(int channelCount)
    {
        if (IsAll)
        {
            return Enumerable.Range(0, channelCount);
        }

        return OneBasedChannels
            .Where(channel => channel >= 1 && channel <= channelCount)
            .Select(channel => channel - 1)
            .Distinct();
    }
}

internal sealed record VfxRouteDestinationText(string BusTarget, int OneBasedChannel);

internal static partial class VfxTextCommandParser
{
    public static IReadOnlyList<VfxTextCommand> ParseScript(string script)
    {
        var commands = new List<VfxTextCommand>();
        foreach (var rawCommand in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(rawCommand))
            {
                continue;
            }

            commands.Add(ParseCommand(rawCommand));
        }

        return commands;
    }

    public static double ParseNumber(string valueText, string valueName)
    {
        var text = valueText.Trim().TrimEnd('%');
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2].Trim();
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            throw new InvalidOperationException($"{valueName} must be a number.");
        }

        return value;
    }

    public static bool ParseBoolean(string valueText)
    {
        return valueText.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => throw new InvalidOperationException("Value must be 1/0, true/false, or on/off.")
        };
    }

    public static VfxRouteDestinationText ParseRouteDestination(string valueText)
    {
        var match = RouteDestinationPattern().Match(valueText);
        if (!match.Success)
        {
            throw new InvalidOperationException("Route destination must be Bus(B1).Ch(3).");
        }

        var busTarget = match.Groups["bus"].Value.Trim();
        if (!int.TryParse(match.Groups["channel"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)
            || channel < 1)
        {
            throw new InvalidOperationException("Route destination channel must be 1 or higher.");
        }

        return new VfxRouteDestinationText(busTarget, channel);
    }

    private static VfxTextCommand ParseCommand(string rawCommand)
    {
        var vstMatch = VstCommandPattern().Match(rawCommand);
        if (vstMatch.Success)
        {
            int? parameterIndex = null;
            if (vstMatch.Groups["parameterIndex"].Success)
            {
                if (!int.TryParse(vstMatch.Groups["parameterIndex"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIndex))
                {
                    throw new InvalidOperationException("VST parameter index must be a non-negative whole number.");
                }

                parameterIndex = parsedIndex;
            }

            return new VfxTextCommand(
                VfxTextCommandTargetKind.Vst,
                vstMatch.Groups["target"].Value.Trim(),
                new VfxTextCommandChannelSelection(IsAll: true, []),
                ParseProperty(vstMatch.Groups["property"].Value),
                ParseOperator(vstMatch.Groups["op"].Value),
                vstMatch.Groups["value"].Value.Trim(),
                rawCommand,
                parameterIndex);
        }

        var match = CommandPattern().Match(rawCommand);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Unsupported VFX command: {rawCommand}");
        }

        var targetKind = ParseTargetKind(match.Groups["targetKind"].Value);
        var target = match.Groups["target"].Value.Trim();
        var channels = ParseChannels(match.Groups["channels"].Value);
        var property = ParseProperty(match.Groups["property"].Value);
        var op = ParseOperator(match.Groups["op"].Value);
        var valueText = match.Groups["value"].Value.Trim();

        return new VfxTextCommand(targetKind, target, channels, property, op, valueText, rawCommand);
    }

    private static VfxTextCommandTargetKind ParseTargetKind(string value)
    {
        return value.Equals("Strip", StringComparison.OrdinalIgnoreCase)
            ? VfxTextCommandTargetKind.Strip
            : VfxTextCommandTargetKind.Bus;
    }

    private static VfxTextCommandProperty ParseProperty(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "enable" or "enabled" => VfxTextCommandProperty.Enable,
            "delay" or "delayms" or "ms" => VfxTextCommandProperty.Delay,
            "volume" or "vol" or "gain" => VfxTextCommandProperty.Volume,
            "route" => VfxTextCommandProperty.Route,
            "routeenable" or "routeenabled" => VfxTextCommandProperty.RouteEnable,
            "routemute" or "muteroute" or "mutenormal" or "routemutenormal" => VfxTextCommandProperty.RouteMuteNormal,
            "bypass" or "bypassed" => VfxTextCommandProperty.Bypass,
            "inputgain" or "ingain" => VfxTextCommandProperty.InputGain,
            "outputgain" or "outgain" => VfxTextCommandProperty.OutputGain,
            "maingain" or "plugingain" or "gain" => VfxTextCommandProperty.MainGain,
            "gainscale" or "scale" => VfxTextCommandProperty.GainScale,
            "drygain" => VfxTextCommandProperty.DryGain,
            "wetgain" => VfxTextCommandProperty.WetGain,
            "mix" or "drywet" => VfxTextCommandProperty.Mix,
            "width" or "stereowidth" or "outputwidth" => VfxTextCommandProperty.Width,
            "inputpan" or "inpan" => VfxTextCommandProperty.InputPan,
            "outputpan" or "outpan" => VfxTextCommandProperty.OutputPan,
            "drypan" => VfxTextCommandProperty.DryPan,
            "wetpan" => VfxTextCommandProperty.WetPan,
            "threshold" or "thresh" => VfxTextCommandProperty.Threshold,
            "ratio" or "compressionratio" => VfxTextCommandProperty.Ratio,
            "attack" or "attacktime" => VfxTextCommandProperty.Attack,
            "release" or "releasetime" => VfxTextCommandProperty.Release,
            "hold" or "holdtime" => VfxTextCommandProperty.Hold,
            "knee" or "kneewidth" => VfxTextCommandProperty.Knee,
            "range" or "reductionrange" => VfxTextCommandProperty.Range,
            "makeupgain" or "makeup" => VfxTextCommandProperty.MakeupGain,
            "ceiling" or "outputceiling" => VfxTextCommandProperty.Ceiling,
            "lookahead" or "lookaheadtime" => VfxTextCommandProperty.Lookahead,
            "rate" or "speed" => VfxTextCommandProperty.Rate,
            "depth" => VfxTextCommandProperty.Depth,
            "feedback" => VfxTextCommandProperty.Feedback,
            "drive" => VfxTextCommandProperty.Drive,
            "tone" => VfxTextCommandProperty.Tone,
            "mode" => VfxTextCommandProperty.Mode,
            "ab" or "compare" => VfxTextCommandProperty.Ab,
            "program" or "preset" => VfxTextCommandProperty.Program,
            "editor" => VfxTextCommandProperty.Editor,
            "reload" => VfxTextCommandProperty.Reload,
            "parameter" => VfxTextCommandProperty.Parameter,
            _ => throw new InvalidOperationException($"Unsupported VFX property: {value}")
        };
    }

    private static VfxTextCommandOperator ParseOperator(string value)
    {
        return value switch
        {
            "+=" => VfxTextCommandOperator.Add,
            "-=" => VfxTextCommandOperator.Subtract,
            _ => VfxTextCommandOperator.Set
        };
    }

    private static VfxTextCommandChannelSelection ParseChannels(string value)
    {
        var text = value.Trim();
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase) || text == "*")
        {
            return new VfxTextCommandChannelSelection(IsAll: true, []);
        }

        var channels = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rangeParts = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rangeParts.Length == 2
                && int.TryParse(rangeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
                && int.TryParse(rangeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)
                && start <= end)
            {
                channels.AddRange(Enumerable.Range(start, end - start + 1));
                continue;
            }

            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
            {
                throw new InvalidOperationException($"Channel must be a number, range, or All: {value}");
            }

            channels.Add(channel);
        }

        if (channels.Count == 0)
        {
            throw new InvalidOperationException("At least one channel must be selected.");
        }

        return new VfxTextCommandChannelSelection(IsAll: false, channels);
    }

    [GeneratedRegex(
        @"^\s*VFX\.VST\((?<target>\d+)\)\.(?:(?<property>Parameter)\((?<parameterIndex>\d+)\)|(?<property>Enable|Enabled|Bypass|Bypassed|InputGain|InGain|OutputGain|OutGain|MainGain|PluginGain|Gain|GainScale|Scale|DryGain|WetGain|Mix|DryWet|Width|StereoWidth|OutputWidth|InputPan|InPan|OutputPan|OutPan|DryPan|WetPan|Threshold|Thresh|Ratio|CompressionRatio|Attack|AttackTime|Release|ReleaseTime|Hold|HoldTime|Knee|KneeWidth|Range|ReductionRange|MakeupGain|Makeup|Ceiling|OutputCeiling|Lookahead|LookaheadTime|Rate|Speed|Depth|Feedback|Drive|Tone|Mode|AB|Compare|Program|Preset|Editor|Reload))\s*(?<op>\+=|-=|=)\s*(?<value>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VstCommandPattern();

    [GeneratedRegex(
        @"^\s*VFX\.(?<targetKind>Strip|Bus)\((?<target>[^)]+)\)\.Ch\((?<channels>[^)]+)\)\.(?<property>Enable|Enabled|Delay|DelayMs|Ms|Volume|Vol|Gain|Route|RouteEnable|RouteEnabled|RouteMute|MuteRoute|MuteNormal|RouteMuteNormal)\s*(?<op>\+=|-=|=)\s*(?<value>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();

    [GeneratedRegex(
        @"^\s*Bus\((?<bus>[^)]+)\)\.Ch\((?<channel>\d+)\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RouteDestinationPattern();
}
