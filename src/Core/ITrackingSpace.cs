namespace Dvr
{
    /// <summary>
    /// Tracking space (the play space, metres) to the game world the eyes render.
    /// The core's VrRig implements it; a mod with its own rig (tpm-vr's museum
    /// rig) implements it to use SpectatorCam and the rest.
    /// </summary>
    internal interface ITrackingSpace
    {
        Pose TrackingToWorld(Pose p);
        /// <summary>World units per tracking metre.</summary>
        float WorldScale { get; }
    }
}
