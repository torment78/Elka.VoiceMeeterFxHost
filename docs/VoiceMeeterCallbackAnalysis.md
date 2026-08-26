# VoiceMeeter Callback Details

This page documents the VoiceMeeter Remote Audio Callback contract used by the
current native engine.

## API Lifecycle

Normal callback operation uses:

- `VBVMR_Login()` and `VBVMR_Logout()`
- `VBVMR_AudioCallbackRegister()`
- `VBVMR_AudioCallbackStart()`
- `VBVMR_AudioCallbackStop()`
- `VBVMR_AudioCallbackUnregister()`

The important callback commands are:

- `VBVMR_CBCOMMAND_STARTING`
- `VBVMR_CBCOMMAND_ENDING`
- `VBVMR_CBCOMMAND_CHANGE`
- `VBVMR_CBCOMMAND_BUFFER_IN`
- `VBVMR_CBCOMMAND_BUFFER_OUT`
- `VBVMR_CBCOMMAND_BUFFER_MAIN`

Starting/change notifications provide format information. Buffer commands carry
the realtime audio block.

## Modes

- **Input** is the pre-strip input insert callback.
- **Output** is the output-bus insert callback.
- **Main** reads inputs and buses and writes VoiceMeeter outputs.

The application selects one normal callback mode at a time according to active
work. Changing the visible UI canvas does not itself stop work on another side.

ASIO Patch is a separate engine mode. It fully disconnects the normal callback
before opening the VoiceMeeter Insert Virtual ASIO driver.

## Buffer Format

VoiceMeeter provides channel-separated, non-interleaved 32-bit float pointers:

```cpp
float* audiobuffer_r[128];
float* audiobuffer_w[128];
```

Each channel pointer contains `audiobuffer_nbs` samples. VoiceMeeter owns the
buffers, clock, sample rate, and callback schedule.

## Potato Input Ranges

| Source | Start | Count |
| --- | ---: | ---: |
| Hardware Input 1 | 0 | 2 |
| Hardware Input 2 | 2 | 2 |
| Hardware Input 3 | 4 | 2 |
| Hardware Input 4 | 6 | 2 |
| Hardware Input 5 | 8 | 2 |
| Virtual Input 1 | 10 | 8 |
| Virtual AUX | 18 | 8 |
| Virtual VAIO 3 | 26 | 8 |

Potato Input Insert therefore exposes 34 channels.

## Potato Output Ranges

| Bus | Start | Count |
| --- | ---: | ---: |
| A1 | 0 | 8 |
| A2 | 8 | 8 |
| A3 | 16 | 8 |
| A4 | 24 | 8 |
| A5 | 32 | 8 |
| B1 | 40 | 8 |
| B2 | 48 | 8 |
| B3 | 56 | 8 |

Potato Output Insert exposes 64 channels. Standard and Banana expose the ranges
available to their editions.

For Main callback passthrough, the current output-bus read offset is derived
from the callback counts:

```cpp
outputReadOffset = audiobuffer_nbi - audiobuffer_nbo;
```

## Engine Preparation

Delay buffers, routing snapshots, plugin bus buffers, and scratch storage are
prepared outside the realtime callback. Memory is allocated only for configured
work where practical, then reused for each block.

The callback copies or claims the relevant channels, applies delay/gain and
direct routes, processes the active VST graph, and writes the completed return
channels before returning to VoiceMeeter.

## Realtime Restrictions

The callback must not perform operations that can block unpredictably. The
audio path avoids:

- UI access
- file or network I/O
- plugin scanning or editor creation
- normal logging
- avoidable heap allocation
- waiting on UI locks

Plugin loading, state persistence, scanning, authorization, editor windows, and
graph preparation happen outside the callback.

## Timing Limits

The maximum callback time is determined by sample rate and block size:

```text
available time = block samples / sample rate
```

Higher sample rates and smaller buffers reduce the deadline sharply. VoiceMeeter
callback scheduling can click at extreme combinations even when measured FX
Host processing remains short. This is a system/driver/callback limit, not proof
of a VST overrun.

Use the status line and VoiceMeeter buffer settings together when choosing a
stable format. ASIO Patch can be tested as an alternative path.

## Synchronous Audio

The processed block must be returned before the callback exits. UI, scanning,
and preparation can be asynchronous, but the block itself cannot be handed to a
background worker and returned later without introducing a deliberate buffered
transport.
