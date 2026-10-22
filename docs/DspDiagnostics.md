# DSP Performance And Overrun Log

Available from the unsigned 0.8.3.2 development build, with the average/peak
display and refresh improvements described below added in 0.8.3.3. Both sections
are above the normal Log in the sidebar and start collapsed. Click a section
heading to open or close it.

## DSP Performance

- **All VSTs** lists processing VSTs ranked by average percentage of their
  audio-cycle budget. Five rows fit in the list; scroll to inspect the rest.
  Each row includes the plugin name, callback and VST ID on one line.
- Select **Input**, **Output**, **Main**, or **ASIO Patch** to inspect one cycle.
- **Cycle budget** is the available time for one block: samples divided by sample
  rate. For example, 256 samples at 48,000 Hz gives about 5.333 ms.
- **Average** shows recent average elapsed processing per block in milliseconds
  and as a percentage of the cycle budget. **Peak (held)** shows the highest
  recorded block in both units, without averaging it down or hiding it between
  refreshes. Peak values remain visible until the audio format changes or the
  host restarts; a plugin reload also resets that plugin's peak.
- Every callback summary and VST row shows its average and held peak. Values
  over 100% remain visible in red; only the bar length stops at 100%.
- Hover anywhere over a row for its full name, ID, callback, cycle budget,
  average, held peak, accumulated budget-overrun count and measurement details.
  These hover details stay available as the values refresh.
- Callback processing includes host work. The VST average subtotal includes all
  processing VSTs in that cycle, not only the five displayed rows.

Input, Output, Main, and ASIO have separate budgets. **All VSTs** shows the
average and peak for every active callback above the plugin list, and lists
the VST average totals by callback below. It does not add independent callbacks
into one percentage or add individual plugin peaks into a fictitious total peak.

An occasional 7.2 ms block can coexist with a low average, but consistently
taking 7.2 ms against a 2.667 ms budget must produce an average around 270%.
The average and peak are different measurements; the overrun log preserves
individual bad blocks instead of the average.

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

The visible dashboard refreshes at a target of 30 Hz (about every 33.3 ms). Native
timing snapshots are published at up to 60 Hz on audio-block boundaries, and the
background reader polls about every 16.7 ms. Overrun detection still runs per block;
repeated log entries are grouped roughly once per second. These are diagnostic
refresh intervals, not audio sample rates or buffer settings.

The persistent log is stored at:

```text
%LOCALAPPDATA%\ElkaSoft\VoiceMeeterFxHost\dsp-overruns.log
```

The file rotates at about 5 MB, keeping one previous file as
`dsp-overruns.log.1`. Clearing the displayed entries does not delete these
files. If the bounded event queue fills, or overlapping callbacks prevent a
measurement, the panel reports the lost-event or skipped-measurement count.
