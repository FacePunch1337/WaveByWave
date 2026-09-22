# Fleet contact regression checks

Run after Unity has compiled the project's packages, with .NET 10 installed:

```powershell
dotnet run --project Tools/ShipFleetChecks/ShipFleetChecks.csproj -c Release
```

For a different Unity installation, pass `-p:UnityEditorData="C:/path/to/Editor/Data"`.

The harness links the production `EnemyShipSpatialIndex.cs` and Unity's math libraries.
It supplies only the snapshot struct's data shape. The index is now a broad phase,
not a collision solver. Checks cover swept candidate coverage, negative coordinates,
reindexing, spawn clearance and cleanup with 1,000 hulls. It does not measure Unity FPS.

Neither player nor enemy movement is stopped by circles. In Unity, run
`Tools > Wave by Wave > Physics > Check fleet contacts and targeting` to exercise
the actual native collider solver (five-metre gap, contact, rotation, height,
pose updates and removal), and target selection beyond the old detection range.
The check uses temporary preview-scene objects and never edits saved prefabs.
Also run `Tools > Wave by Wave > Enemies > Check pursuit, edges and hull contacts`
for inward steering at all ranges, authored-width hull sweeps, side-by-side sliding,
and skeleton edge following on rotated surfaces.
