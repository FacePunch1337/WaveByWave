using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaveByWave.Enemies
{
    public static partial class DotsEnemyPresentation
    {
        private static readonly Dictionary<EnemyKind, Entity> HealthBarTemplates = new();

        private static void UpdateHealthBar(View view, DotsEnemyState state, DotsEnemyCatalog catalog)
        {
            var show = state.HealthBar == EnemyHealthBarMode.Show ||
                state.HealthBar == EnemyHealthBarMode.Profile && catalog.ShowHealthBars;
            if (!show || catalog.HealthBarMaterial == null || catalog.HealthBarMesh == null)
            { DestroyHealthBar(view); return; }
            var camera = Camera.main;
            show &= camera != null && math.distancesq(state.Position, camera.transform.position) <=
                catalog.HealthBarDistance * catalog.HealthBarDistance;
            if (!show)
            { DestroyHealthBar(view); return; }
            if (view.HealthBar != Entity.Null) return;
            var manager = _world.EntityManager;
            if (!HealthBarTemplates.TryGetValue(catalog.Kind, out var template))
            {
                template = manager.CreateEntity(typeof(LocalToWorld), typeof(EnemyPartOwner), typeof(EnemyFrameProperty));
                var array = new RenderMeshArray(new[] { catalog.HealthBarMaterial }, new[] { catalog.HealthBarMesh });
                RenderMeshUtility.AddComponents(template, manager,
                    new RenderMeshDescription(ShadowCastingMode.Off, false, MotionVectorGenerationMode.ForceNoMotion,
                        0, uint.MaxValue, LightProbeUsage.Off), array, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                manager.AddComponent<Prefab>(template);
                HealthBarTemplates.Add(catalog.Kind, template);
            }
            view.HealthBar = manager.Instantiate(template);
            manager.SetComponentData(view.HealthBar, new EnemyPartOwner { Root = view.Root, HealthBar = 1 });
        }

        private static void DestroyHealthBar(View view)
        {
            if (view.HealthBar != Entity.Null && _world != null && _world.IsCreated && _world.EntityManager.Exists(view.HealthBar))
                _world.EntityManager.DestroyEntity(view.HealthBar);
            view.HealthBar = Entity.Null;
        }

        private static void DisposeHealthBars()
        {
            if (_world != null && _world.IsCreated)
                foreach (var entity in HealthBarTemplates.Values)
                    if (_world.EntityManager.Exists(entity)) _world.EntityManager.DestroyEntity(entity);
            HealthBarTemplates.Clear();
        }
    }
}
