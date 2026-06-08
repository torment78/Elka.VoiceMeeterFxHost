using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class SinglePingWindow : Window
{
    private const string DllName = "ElkaVoiceMeeterFxHost.Native.dll";
    private const int PingTimeoutMilliseconds = 12_000;
    private const int ExternalPingStatusDetected = 1;
    private const int ExternalPingStatusTimeout = 2;
    private readonly VoicemeeterKind _kind;

    public SinglePingWindow(VoicemeeterKind kind)
    {
        InitializeComponent();
        _kind = kind;
        PopulateEndpoints();
        UpdateSelectionText();
    }

    private void PopulateEndpoints()
    {
        PingInputComboBox.Items.Clear();
        ReturnOutputComboBox.Items.Clear();

        foreach (var endpoint in LoadWasapiDevices(ElkaFx_ListWasapiRenderDevices))
        {
            PingInputComboBox.Items.Add(endpoint);
        }

        foreach (var endpoint in LoadWasapiDevices(ElkaFx_ListWasapiCaptureDevices))
        {
            ReturnOutputComboBox.Items.Add(endpoint);
        }

        PingInputComboBox.SelectedItem = PickPreferredDevice(PingInputComboBox.Items.OfType<WasapiDeviceChoice>(), render: true)
            ?? (PingInputComboBox.Items.Count > 0 ? PingInputComboBox.Items[0] : null);
        ReturnOutputComboBox.SelectedItem = PickPreferredDevice(ReturnOutputComboBox.Items.OfType<WasapiDeviceChoice>(), render: false)
            ?? (ReturnOutputComboBox.Items.Count > 0 ? ReturnOutputComboBox.Items[0] : null);

        ResultTextBlock.Text = PingInputComboBox.Items.Count == 0 || ReturnOutputComboBox.Items.Count == 0
            ? "No active Windows render/capture devices were found."
            : "Ready. Select a Windows send device and capture return, then click Ping.";
    }

    private void EndpointComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionText();
    }

    private async void PingButton_Click(object sender, RoutedEventArgs e)
    {
        if (PingInputComboBox.SelectedItem is not WasapiDeviceChoice render ||
            ReturnOutputComboBox.SelectedItem is not WasapiDeviceChoice capture)
        {
            ResultTextBlock.Text = "Pick a send device and listen device first.";
            return;
        }

        SetControlsEnabled(false);
        ResultTextBlock.Text =
            $"Sending pulse to: {render.DisplayName}\n" +
            $"Listening on: {capture.DisplayName}\n\n" +
            "Running external WASAPI ping...";

        try
        {
            var result = await Task.Run(() => RunExternalPing(render, capture));
            ResultTextBlock.Text = FormatPingResult(render, capture, result);
        }
        catch (Exception ex)
        {
            ResultTextBlock.Text = $"External ping failed: {ex.Message}";
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private static PingRunResult RunExternalPing(WasapiDeviceChoice render, WasapiDeviceChoice capture)
    {
        var nativeResult = new NativeExternalWasapiPingResult();
        var status = new StringBuilder(512);
        var code = ElkaFx_RunExternalWasapiPing(
            render.DeviceId,
            capture.DeviceId,
            PingTimeoutMilliseconds,
            ref nativeResult,
            status,
            status.Capacity);

        return new PingRunResult(code, status.ToString(), nativeResult);
    }

    private static string FormatPingResult(WasapiDeviceChoice render, WasapiDeviceChoice capture, PingRunResult run)
    {
        var result = run.Result;
        if (result.Status == ExternalPingStatusDetected)
        {
            return
                "External ping detected.\n\n" +
                $"Send: {render.DisplayName}\n" +
                $"Listen: {capture.DisplayName}\n" +
                $"Latency: {result.LatencySamples} samples / {result.LatencyMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)} ms\n" +
                $"Sample rate: {result.SampleRate} Hz\n" +
                $"Channels: render {result.RenderChannels}, capture {result.CaptureChannels}\n" +
                $"Peak: {result.PeakPercent}%";
        }

        if (result.Status == ExternalPingStatusTimeout)
        {
            return
                "External ping timed out.\n\n" +
                $"Send: {render.DisplayName}\n" +
                $"Listen: {capture.DisplayName}\n" +
                $"Peak seen: {result.PeakPercent}%\n\n" +
                "Make sure the selected Windows playback device is routed through VoiceMeeter and the selected capture device is the matching VoiceMeeter return, such as B1/B2/B3.";
        }

        return string.IsNullOrWhiteSpace(run.Status)
            ? $"External ping failed with code {run.Code}."
            : run.Status;
    }

    private void SetControlsEnabled(bool enabled)
    {
        PingButton.IsEnabled = enabled;
        PingInputComboBox.IsEnabled = enabled;
        ReturnOutputComboBox.IsEnabled = enabled;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateSelectionText()
    {
        var render = PingInputComboBox.SelectedItem as WasapiDeviceChoice;
        var capture = ReturnOutputComboBox.SelectedItem as WasapiDeviceChoice;
        SelectionTextBlock.Text = render is null || capture is null
            ? string.Empty
            : $"{render.DisplayName} -> {capture.DisplayName}";
    }

    private static WasapiDeviceChoice? PickPreferredDevice(IEnumerable<WasapiDeviceChoice> devices, bool render)
    {
        var choices = devices.ToArray();
        var preferredNames = render
            ? new[] { "voicemeeter input", "voicemeeter aux input", "voicemeeter vaio3 input" }
            : new[] { "voicemeeter output", "voicemeeter aux output", "voicemeeter vaio3 output" };

        return preferredNames
            .Select(name => choices.FirstOrDefault(device => device.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(device => device is not null);
    }

    private static IReadOnlyList<WasapiDeviceChoice> LoadWasapiDevices(Func<StringBuilder, int, int> listDevices)
    {
        var buffer = new StringBuilder(32_768);
        var count = listDevices(buffer, buffer.Capacity);
        if (count < 0 || buffer.Length == 0)
        {
            return [];
        }

        return buffer.ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseWasapiDevice)
            .Where(static device => device is not null)
            .Cast<WasapiDeviceChoice>()
            .ToArray();
    }

    private static WasapiDeviceChoice? ParseWasapiDevice(string line)
    {
        var tab = line.IndexOf('\t', StringComparison.Ordinal);
        if (tab <= 0 || tab >= line.Length - 1)
        {
            return null;
        }

        return new WasapiDeviceChoice(line[..tab], line[(tab + 1)..]);
    }

    private sealed record WasapiDeviceChoice(string DeviceId, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record PingRunResult(int Code, string Status, NativeExternalWasapiPingResult Result);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeExternalWasapiPingResult
    {
        public int Status;
        public int SampleRate;
        public int LatencySamples;
        public int PeakPercent;
        public int RenderChannels;
        public int CaptureChannels;
        public double LatencyMilliseconds;
    }

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_ListWasapiRenderDevices(StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_ListWasapiCaptureDevices(StringBuilder buffer, int bufferChars);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ElkaFx_RunExternalWasapiPing(
        string renderDeviceId,
        string captureDeviceId,
        int timeoutMilliseconds,
        ref NativeExternalWasapiPingResult result,
        StringBuilder status,
        int statusChars);
}
