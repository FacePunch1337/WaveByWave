# Fleet contact regression checks

Run after Unity has compiled the project's packages, with .NET 10 installed:

```powershell
dotnet run --project Tools/ShipFleetChecks/ShipFleetChecks.csproj -c Release
```

For a different Unity installation, pass `-p:UnityEditorData="C:/path/to/Editor/Data"`.

The harness links the production `EnemyShipSpatialIndex.cs` and Unity's math libraries.
It supplies only the snapshot struct's data shape; it does not mock the contact solver.
Cases cover multi-cell swept motion, negative coordinates, reindexing, cleanup,
different player hull sizes, overlap recovery and 1,000 converging hulls over 30 steps.
The exhaustive final check verifies that no pair overlaps. Reported duration measures
this CPU algorithm only, not in-game FPS, PhysX, networking or GPU rendering.
