# Group Routing Checks

```powershell
dotnet run --project tests/GroupRoutingChecks/GroupRoutingChecks.csproj
```

Uses the production grouping code and saved graph models without starting the
audio engine. Verifies the exact effective cables before and after grouping,
upstream/downstream/middle insertions, fan-out, channel swaps, sidechains,
moving between groups, parallel branches, serialization, empty pre-wired groups,
port-capacity failure without mutation, and non-overlapping member positions.
