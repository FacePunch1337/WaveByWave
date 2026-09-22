using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace WaveByWave.Networking
{
    // Also covers server worlds created after the initial bootstrap. The report is
    // throttled; Unity's original batching warning and emergency catch-up stay enabled.
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    public partial class ServerTickDiagnostics : SystemBase
    {
        private bool _configured;
        private double _nextReport;
        private float _worstFrame;
        private int _slowFrames;

        protected override void OnUpdate()
        {
            if (!_configured)
            {
                SteamNetcodeBootstrap.ConfigureServerTickRate(World);
                _configured = true;
                _nextReport = UnityEngine.Time.realtimeSinceStartupAsDouble + 10;
                var rate = SystemAPI.GetSingleton<ClientServerTickRate>();
                Debug.Log($"[NFE timing] {World.Name}: simulation={rate.SimulationTickRate} Hz, " +
                    $"catch-up steps={rate.MaxSimulationStepsPerFrame}, max batch={rate.MaxSimulationStepBatchSize}, " +
                    $"graphics={SystemInfo.graphicsDeviceType}.");
            }
            var settings = SystemAPI.GetSingleton<ClientServerTickRate>();
            var frame = UnityEngine.Time.unscaledDeltaTime;
            _worstFrame = Mathf.Max(_worstFrame, frame);
            if (frame > settings.SimulationFixedTimeStep * settings.MaxSimulationStepsPerFrame) _slowFrames++;
            if (UnityEngine.Time.realtimeSinceStartupAsDouble < _nextReport) return;
            if (_slowFrames > 0)
                Debug.Log($"[NFE timing] {_slowFrames} frames exceeded the catch-up budget in 10 s; " +
                    $"worst={_worstFrame * 1000:F1} ms; simulation={settings.SimulationTickRate} Hz, " +
                    $"catch-up steps={settings.MaxSimulationStepsPerFrame}.");
            _nextReport = UnityEngine.Time.realtimeSinceStartupAsDouble + 10;
            _slowFrames = 0;
            _worstFrame = 0;
        }
    }
}
