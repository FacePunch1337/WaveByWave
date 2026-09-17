using Unity.Netcode.Components;

namespace WaveByWave.Player
{
    /// <summary>
    /// Responsive owner-authoritative transform for trusted Steam co-op players.
    /// Combat and inventory remain server authoritative.
    /// </summary>
    public sealed class OwnerNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative() => false;
    }
}
