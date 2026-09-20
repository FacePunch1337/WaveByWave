using System.Collections.Generic;
using UnityEngine;

namespace WaveByWave.Generation
{
    public sealed class BakedIslandMeshCollection : ScriptableObject
    {
        [HideInInspector] public List<Mesh> Meshes = new();
    }
}
