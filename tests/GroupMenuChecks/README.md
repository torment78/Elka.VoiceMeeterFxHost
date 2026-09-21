# Group Menu Checks

Windows-only regression checks for the actual WPF group editor. No VoiceMeeter
connection or plugin engine is started. Reload is simulated at the control-layer
boundary to check menu targeting, replacement slots, layout changes, and Cancel.

```powershell
dotnet run --project tests/GroupMenuChecks/GroupMenuChecks.csproj -c Debug
```

Also checks menu parity, independent bypass/power controls, and that minimizing
pins preserves the graph and keeps hidden-pin cables attached during movement.
Actual plugin reload and audible continuity still require testing in FX Host.
