using BepInEx.Logging;

namespace Dvr
{
    /// <summary>
    /// The core's link to the mod that compiles it in: the log to write to and
    /// the name its GameObjects carry ("Ror2Vr Eye L", "TpmVr Spectator").
    /// Call <see cref="Init"/> first thing in the plugin's Load/Awake.
    /// </summary>
    internal static partial class VrCore
    {
        public static ManualLogSource Log { get; private set; }

        /// <summary>Prefix for objects the core creates; SpectatorCam and CylinderMask find the eyes by it.</summary>
        public static string Name { get; private set; } = "Dvr";

        /// <summary>
        /// The layer our own objects go on (laser, hand markers, panels, the
        /// cylinder mask). The eyes always draw it; the game's camera should
        /// not. 31 by default. Set it before the first object is made if the
        /// game's renderers mask 31 out (cataclismo-vr uses 0).
        /// </summary>
        public static int VisualLayer = 31;

        public static void Init(ManualLogSource log, string name)
        {
            Log = log;
            Name = name;
            log?.LogInfo($"dvr-unity-core {Version}");
        }

        internal static void Info(string message) => Log?.LogInfo(message);
        internal static void Warn(string message) => Log?.LogWarning(message);
        internal static void Error(string message) => Log?.LogError(message);

        /// <summary>
        /// Same Unity object. Under IL2CPP this compares the native pointers (the
        /// interop wrappers can differ for one object, and it's cheaper than the
        /// managed == that calls into the runtime); under Mono it is plain ==.
        /// </summary>
        public static bool Same(UnityEngine.Object a, UnityEngine.Object b)
        {
#if DVR_IL2CPP
            return a != null && b != null && a.Pointer == b.Pointer;
#else
            return a != null && a == b;
#endif
        }

        /// <summary>
        /// A key for a Unity object in a set: its native pointer under IL2CPP, its
        /// instance ID under Mono, its EntityId on Unity 6.5+ (DVR_ENTITYID), where
        /// GetInstanceID is obsolete as an error.
        /// </summary>
        public static long Key(UnityEngine.Object o)
        {
#if DVR_IL2CPP
            return (long)o.Pointer;
#elif DVR_ENTITYID
            return (long)o.GetEntityId().GetRawData();
#else
            return o.GetInstanceID();
#endif
        }
    }
}
