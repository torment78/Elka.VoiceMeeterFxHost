# Risks And Limitations

## VoiceMeeter Callback Limits

VoiceMeeter owns the audio clock and callback schedule. Very high sample rates
combined with very small block sizes can produce clicks even when the FX Host
processing time is low. The practical limit depends on the VoiceMeeter edition,
driver, system, and active routing.

Use a stable VoiceMeeter buffer first, then reduce it gradually. ASIO Patch is an
alternative engine path for systems that need different low-latency behavior.

Only one application can own a given VoiceMeeter callback mode. Registration can
fail when another callback client already owns it.

## Incomplete Routes Are Silent

Connecting a source into a VST path claims that audio channel. The path stays
silent until a cable reaches a valid destination. This is intentional and
prevents dry audio from leaking around an unfinished graph.

An empty VST group is also silent until a complete internal chain exists.

## Plugin Safety

VST plugins are third-party native code. They can crash, deadlock, allocate,
perform disk or network access, or block on licensing.

Known risky vendor patterns can use the sandbox worker, which protects the main
WPF process from many plugin failures. It cannot make a faulty plugin
realtime-safe, and normal in-process plugins can still crash the main host.

## Plugin Authorization

UAD, Waves, Slate, iLok, and similar plugins can take longer to initialize while
their vendor software checks licensing or account state. Keep UA Connect, iLok
License Manager, and related vendor services correctly authenticated.

The app log distinguishes scan, probe, worker startup, state restoration, and
editor operations so a licensing delay is not mistaken for a frozen scan.

## Plugin State

The host saves JUCE state data, presets, and parameter fallbacks where exposed.
Not every VST implements state storage correctly, and state formats can change
between plugin versions. Keep exported saves before updating important plugins.

## Plugin Latency

Lookahead limiters, linear-phase EQs, convolution processors, and oversampling
plugins can report significant latency. VoiceMeeter's callback API does not
provide a DAW-style automatic latency compensation contract for this graph.

## Channel Layouts

The UI can expose stereo, wider multichannel, and stereo sidechain pins. A VST
must accept the requested JUCE bus layout. Some plugins advertise a layout but
fail when the host activates it.

Use stereo when a wider layout fails. Sidechain pins work only when the plugin
provides a usable sidechain bus.

## VST2

VST2 support requires valid legacy SDK headers at build time. The released app
is x64 and cannot load 32-bit plugin binaries. Prefer VST3 when both formats are
available.

## ASIO Patch Ownership

ASIO Patch and the normal VoiceMeeter callback do not run together. While ASIO
Patch is active, Output, Main, In -> Out, and Out -> Out controls are disabled.
Stop ASIO Patch to return to those callback routes.

The selected ASIO Patch inputs correspond to VoiceMeeter `Patch.insert` state.
An input with Patch.insert enabled needs an active insert host to return audio.

## Resource Use

Large VSTs can reserve substantial private memory, and sandboxed VSTs appear as
a separate worker process in Task Manager. The app status uses Task
Manager-style process CPU and working-set memory, while vendor plugins can also
reserve memory outside the visible main process.
