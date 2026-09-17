namespace WaveByWave.Player
{
    public interface IPlayerInteractable
    {
        string GetInteractionPrompt(NetworkPlayerController player);
        void Interact(NetworkPlayerController player);
    }
}
