using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WaveByWave.Enemies
{
    public struct EnemyVisualInterpolation : IComponentData
    {
        public float3 Position, From, To;
        public quaternion Rotation;
        public float SampleTime, Started, Duration;
        public ulong Support;
        public byte Initialized;
    }

    public struct EnemyVisualUpdate
    {
        public Entity Root;
        public float3 Position;
        public quaternion Rotation;
        public float4x4 Surface;
        public ulong Support;
        public uint Seed;
        public float SampleTime, AnimationStarted, Duration, FlashUntil;
        public int FirstRow, FrameCount;
        public byte Loop, Stunned;
    }

    [BurstCompile]
    public struct EnemyVisualPoseJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<EnemyVisualUpdate> Updates;
        // Presentation supplies exactly one update per unique visual root.
        [NativeDisableParallelForRestriction] public ComponentLookup<EnemyVisualInterpolation> Interpolations;
        [NativeDisableParallelForRestriction] public ComponentLookup<EnemyVisualPose> Poses;
        public float Now, RenderTime, EffectTime, RotationBlend, Scale;

        public void Execute(int index)
        {
            var update = Updates[index];
            var interpolation = Interpolations[update.Root];
            if (interpolation.Initialized == 0 || interpolation.Support != update.Support ||
                math.distancesq(interpolation.Position, update.Position) > 25f)
            {
                interpolation.Position = interpolation.From = interpolation.To = update.Position;
                interpolation.Rotation = update.Rotation;
                interpolation.SampleTime = update.SampleTime;
                interpolation.Started = RenderTime;
                interpolation.Duration = 0;
                interpolation.Initialized = 1;
            }
            else
            {
                var progress = interpolation.Duration > 0
                    ? math.saturate((RenderTime - interpolation.Started) / interpolation.Duration) : 1;
                interpolation.Position = math.lerp(interpolation.From, interpolation.To, progress);
                if (interpolation.SampleTime != update.SampleTime)
                {
                    interpolation.From = interpolation.Position;
                    interpolation.To = update.Position;
                    interpolation.Duration = math.clamp(update.SampleTime - interpolation.SampleTime, 0.02f, 0.5f);
                    interpolation.Started = RenderTime;
                    interpolation.SampleTime = update.SampleTime;
                }
                interpolation.Rotation = math.slerp(interpolation.Rotation, update.Rotation, RotationBlend);
            }
            interpolation.Support = update.Support;
            Interpolations[update.Root] = interpolation;
            var matrix = float4x4.TRS(interpolation.Position, interpolation.Rotation, new float3(Scale));
            if (update.Support != 0) matrix = math.mul(update.Surface, matrix);
            var elapsed = math.max(0, Now - update.AnimationStarted) / math.max(0.001f, update.Duration);
            var phase = update.Loop != 0 ? math.frac(elapsed + (update.Seed % 997) / 997f) : math.saturate(elapsed);
            var frame = phase * math.max(0, update.FrameCount - 1);
            var first = (int)math.floor(frame);
            Poses[update.Root] = new EnemyVisualPose
            {
                Matrix = matrix,
                Frame = new float4(update.FirstRow + first,
                    update.FirstRow + math.min(first + 1, update.FrameCount - 1), frame - first,
                    RenderTime < update.FlashUntil ? 1 : 0),
                Time = EffectTime, Stunned = update.Stunned
            };
        }
    }
}
