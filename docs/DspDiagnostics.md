# DSP Performance And Overrun Log

Available from the unsigned 0.8.3.2 development build. Both sections are above
the normal Log in the sidebar and start collapsed. Click a section heading to
open or close it.

## DSP Performance

- **All VSTs** shows the five processing VSTs using the largest percentage of
  their audio-cycle budget. Each row includes the plugin name and its VST ID.
- Select **Input**, **Output**, **Main**, or **ASIO Patch** to inspect one cycle.
- **Audio cycle** is the available time for one block: samples divided by sample
  rate. For example, 256 samples at 48,000 Hz gives about 5.333 ms.
- Row percentages and times show recent average elapsed node-processing time.
  Hover over a row for its peak and accumulated budget-overrun count.
- The header includes host processing work. The VST subtotal includes all
  processing VSTs in that cycle, not only the five displayed rows.

Input, Output, Main, and ASIO have separate budgets. **All VSTs** shows the
busiest cycle in its header and lists the VST totals by cycle below; it does
not add independent callbacks into one percentage.

These are elapsed processing measurements, not Windows Task Manager CPU
percentages. A sandboxed plugin's time includes communication with its worker
and any scheduling waits. Powered-off or unconnected plugins that are not
processing audio do not appear as active processing rows.

## DSP Overrun Log

An entry is recorded when a VST node or the complete callback takes longer than
its nominal audio-cycle budget. Entries show the timestamp, plugin name and
ID or callback name, budget, processing duration, usage, and excess time.

Repeated overruns for the same node and format are grouped approximately once
per second. Each entry shows the occurrence count and the worst measured event
in that group. The panel keeps the latest 200 entries. A VST overrun and its
callback overrun can both be logged; they are not two independent audio glitches.

An overrun is a timing warning, not proof of an audible glitch. A low measured
time also cannot rule out a driver, device, or callback-delivery problem.

## Background Detection And Files

Detection and file logging continue when both panels are closed or FX Host is
in the tray. Panel rendering stops while hidden. Formatting and disk writes
run outside the audio threads; the audio path records only bounded timing data.
Timing measurement has a small overhead and does not change routing or samples.

The persistent log is stored at:

```text
%LOCALAPPDATA%\ElkaSoft\VoiceMeeterFxHost\dsp-overruns.log
```

The file rotates at about 5 MB, keeping one previous file as
`dsp-overruns.log.1`. Clearing the displayed entries does not delete these
files. If the bounded event queue fills, or overlapping callbacks prevent a
measurement, the panel reports the lost-event or skipped-measurement count.
