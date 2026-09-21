using System;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using WaveByWave.Enemies;

// The harness exercises the production spatial-index source outside Unity. Only
// the snapshot data shape is supplied here; no physics or algorithm is mocked.
namespace WaveByWave.Enemies
{
    public struct DotsEnemyShipState { public int Id; public float Health; public float3 Position; }
}

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); _checks++; }

    private static void Main()
    {
        var grid = new EnemyShipSpatialIndex(10);
        grid.Set(1, new float2(0, 0));
        grid.Set(2, new float2(50, 0));
        var move = grid.LimitMotion(1, new float3(0, 0, 0), new Vector3(100, 0, 0));
        Check(move.x > 29.9f && move.x < 30, "Swept hull prevents tunnelling across multiple cells");
        Check(grid.LimitMotion(1, float3.zero, new Vector3(-5, 0, 0)).x == -5,
            "Movement away from another hull remains unrestricted");
        Check(!grid.IsClear(new float2(19, 0)) && grid.IsClear(new float2(25, 0)), "Spawn clearance");
        grid.Set(2, new float2(-50, -50));
        Check(grid.IsClear(new float2(50, 0)), "Reindexing removes an old cell entry");
        Check(!grid.IsClear(new float2(-51, -50)), "Negative world coordinates");
        grid.Clear();
        Check(grid.IsClear(float2.zero), "Scene cleanup");
        grid.Set(2, new float2(50, 0));
        var playerMove = grid.LimitMotion(0, float3.zero, new Vector3(100, 0, 0), 20);
        Check(playerMove.x > 19.9f && playerMove.x < 20, "A larger player hull uses its own radius");
        grid.Set(2, new float2(10, 0));
        Check(grid.LimitMotion(0, float3.zero, new Vector3(5, 0, 0)).x == 0, "Overlapping hulls cannot move deeper");
        Check(grid.LimitMotion(0, float3.zero, new Vector3(-5, 0, 0)).x == -5, "Overlapping hulls can separate");
        grid.Clear();

        const int count = 1000;
        var positions = new float2[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = new float2((i % 40) * 25 - 500, (i / 40) * 25 - 300);
            grid.Set(i + 1, positions[i]);
        }
        var watch = Stopwatch.StartNew();
        for (var step = 0; step < 30; step++)
        for (var i = 0; i < count; i++)
        {
            var p = positions[i];
            var d = math.normalizesafe(-p) * 1.2f;
            var accepted = grid.LimitMotion(i + 1, new float3(p.x, 0, p.y), new Vector3(d.x, 0, d.y));
            positions[i] += new float2(accepted.x, accepted.z);
            grid.Set(i + 1, positions[i]);
        }
        watch.Stop();
        for (var i = 0; i < count; i++)
        for (var j = i + 1; j < count; j++)
            Check(math.distancesq(positions[i], positions[j]) >= 399.99f, "Fleet hull overlap");

        Console.WriteLine($"PASS: {_checks} assertions; 1,000 hulls, 30 movement steps, no overlaps.");
        Console.WriteLine($"Spatial index only: {watch.Elapsed.TotalMilliseconds:F1} ms total (not a Unity FPS measurement).");
    }
}
