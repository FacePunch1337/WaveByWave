using Unity.Entities;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Core;

namespace WaveByWave.Simulation
{
    public struct RunSimulationState : IComponentData
    {
        public uint Seed;
        public int WaveIndex;
        public float DayTimeRemaining;
        public byte IsNight;
    }

    /// <summary>
    /// Boundary between the NGO session and the DOTS simulation.
    /// NGO owns players, ship and interactions; Entities/Netcode for Entities is reserved
    /// for high-count enemies, projectiles and floating loot.
    /// </summary>
    public sealed class HybridSimulationBridge : MonoBehaviour
    {
        [SerializeField] private NetworkManager networkManager;
        private Entity _stateEntity = Entity.Null;

        private void Awake()
        {
            networkManager ??= GetComponent<NetworkManager>();
        }

        private void Update()
        {
            var shouldSimulate = networkManager != null && networkManager.IsServer &&
                                 SceneManager.GetActiveScene().name == GameScenes.Ocean;

            if (!shouldSimulate)
            {
                DestroyStateEntity();
                return;
            }

            EnsureStateEntity();
            if (_stateEntity == Entity.Null || World.DefaultGameObjectInjectionWorld == null)
                return;

            var manager = World.DefaultGameObjectInjectionWorld.EntityManager;
            if (!manager.Exists(_stateEntity))
                return;

            var state = manager.GetComponentData<RunSimulationState>(_stateEntity);
            state.DayTimeRemaining = Mathf.Max(0f, state.DayTimeRemaining - Time.deltaTime);
            manager.SetComponentData(_stateEntity, state);
        }

        private void EnsureStateEntity()
        {
            if (_stateEntity != Entity.Null || World.DefaultGameObjectInjectionWorld == null)
                return;

            var manager = World.DefaultGameObjectInjectionWorld.EntityManager;
            _stateEntity = manager.CreateEntity(typeof(RunSimulationState));
            manager.SetComponentData(_stateEntity, new RunSimulationState
            {
                Seed = (uint)Random.Range(1, int.MaxValue),
                WaveIndex = 0,
                DayTimeRemaining = 300f,
                IsNight = 0
            });
        }

        private void DestroyStateEntity()
        {
            if (_stateEntity == Entity.Null || World.DefaultGameObjectInjectionWorld == null)
                return;

            var manager = World.DefaultGameObjectInjectionWorld.EntityManager;
            if (manager.Exists(_stateEntity))
                manager.DestroyEntity(_stateEntity);
            _stateEntity = Entity.Null;
        }

        private void OnDisable() => DestroyStateEntity();
    }
}
