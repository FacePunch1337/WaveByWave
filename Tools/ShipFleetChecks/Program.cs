using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using WaveByWave.Enemies;

namespace WaveByWave.Enemies
{
    public struct DotsEnemyShipState { public int Id; public float Health; public float3 Position; }
}

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; }
    private static void Main()
    {
        var grid = new EnemyShipSpatialIndex(10);
        var candidates = new List<int>();
        grid.Set(1, float2.zero);
        grid.Set(2, new float2(50,0));
        grid.CollectCandidates(1, float3.zero, new Vector3(100,0,0), candidates);
        Check(candidates.Contains(2) && !candidates.Contains(1), "Swept broadphase omitted a crossed hull or included self.");
        Check(!grid.IsClear(new float2(19,0)) && grid.IsClear(new float2(25,0)), "Spawn clearance");
        grid.Set(2, new float2(-50,-50));
        grid.CollectCandidates(1, float3.zero, new Vector3(100,0,0), candidates);
        Check(!candidates.Contains(2), "Reindexing left a stale candidate.");
        Check(!grid.IsClear(new float2(-51,-50)), "Negative cells");
        grid.Clear();
        var positions = new float2[1000];
        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = new float2((i%40)*25-500,(i/40)*25-300);
            grid.Set(i+1,positions[i]);
        }
        for (var i=0; i<positions.Length; i++)
        {
            var movement=math.normalizesafe(-positions[i])*37;
            grid.CollectCandidates(i+1,new float3(positions[i].x,0,positions[i].y),new Vector3(movement.x,0,movement.y),candidates);
            for(var j=0;j<positions.Length;j++)
            {
                if(i==j) continue;
                var t=math.saturate(math.dot(positions[j]-positions[i],movement)/math.max(0.001f,math.lengthsq(movement)));
                if(math.distancesq(positions[i]+t*movement,positions[j])<=400)
                    Check(candidates.Contains(j+1), "Broadphase missed a possible authored contact.");
            }
        }
        grid.Clear();
        grid.CollectCandidates(0,float3.zero,Vector3.zero,candidates);
        Check(candidates.Count==0,"Cleanup");
        Console.WriteLine($"PASS: {checks} broadphase assertions for 1,000 hulls. Native contact/edge tests run in Unity.");
    }
}
