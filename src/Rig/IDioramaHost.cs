using BepInEx.Configuration;
using UnityEngine;

namespace Dvr
{
    /// <summary>What the table rig needs from the game. Each mod implements it (Ror2Game in ror2-vr).</summary>
    internal interface IDioramaHost
    {
        /// <summary>The game's own camera (render settings are copied from it); null when there is nothing to show.</summary>
        Camera GameCamera { get; }
        /// <summary>The point the table centres on (the player's feet) and whether that point stands on the ground.</summary>
        bool TryGetFocus(out Vector3 focus, out bool grounded);
        /// <summary>True while the game's own UI wants the grips.</summary>
        bool GripsBusy { get; }
    }

    /// <summary>
    /// The rig's settings, as the mod's own config entries (each mod keeps its
    /// section and key names, so existing .cfg files still load). Fill in the
    /// entries; the limits and action names have defaults.
    /// </summary>
    internal sealed class RigConfig
    {
        /// <summary>World units per real metre. Zoom and two-hand pinch write it back when they let go.</summary>
        public ConfigEntry<float> Scale;
        /// <summary>Height of the focus above the player's floor, metres.</summary>
        public ConfigEntry<float> TableHeight;
        /// <summary>Table centre in front of the standing spot, metres.</summary>
        public ConfigEntry<float> TableDistance;
        /// <summary>How fast the table re-centres on the focus (1/s).</summary>
        public ConfigEntry<float> FollowSpeed;
        /// <summary>Top speed the stick spins the table at, degrees a second.</summary>
        public ConfigEntry<float> TurnSpeed;
        /// <summary>Stick zoom speed.</summary>
        public ConfigEntry<float> ZoomSpeed;
        /// <summary>MSAA samples for the eye textures (read once, at start).</summary>
        public ConfigEntry<int> EyeMsaa;

        public float MinScale = 3f, MaxScale = 400f;
        /// <summary>A jump of the focus further than this (world units) snaps the table instead of easing.</summary>
        public float SnapDistance = 100f;
        /// <summary>Degrees the table turns per degree the two grips twist (both-grip drag).</summary>
        public float TwistGain = 1f;

        /// <summary>
        /// re2-vr's orbit instead of the TPM one-hand drag: one grip circles round
        /// the focus (sideways turns the table, strongest; up/down sets the table
        /// height and forward/back its distance, both damped). The table stays
        /// centred on the focus, the turn eases after the hand and coasts on release.
        /// Writes TableHeight / TableDistance back when let go. Both grips are unchanged.
        /// </summary>
        public bool OneGripOrbits;
        public float OrbitTurnGain = 1.5f, OrbitHeightGain = 0.5f, OrbitDistanceGain = 0.33f;
        /// <summary>Up/down and forward/back hand drift ignored per metre of sideways movement, so a turn doesn't also move the table.</summary>
        public float OrbitSidewaysLock = 0.5f;
        /// <summary>How quickly the turn catches up with the hand, and how quickly a thrown turn slows (1/s).</summary>
        public float OrbitFollow = 14f, OrbitDamping = 2.5f;

        // VrUiKit action names the rig reads (see TableActions.Register).
        public string MoveTable = TableActions.MoveTable;
        public string TurnLeft = TableActions.TurnLeft, TurnRight = TableActions.TurnRight;
        public string ZoomIn = TableActions.ZoomIn, ZoomOut = TableActions.ZoomOut;
    }

    /// <summary>The table's VrUiKit actions, with the TPM scheme's default bindings.</summary>
    internal static class TableActions
    {
        public const string MoveTable = "table.move", TurnLeft = "turn.left", TurnRight = "turn.right",
            ZoomIn = "zoom.in", ZoomOut = "zoom.out", Recentre = "table.recentre";

        /// <summary>
        /// The table's actions for a **character game** (Orbit) and for VrRig: turn
        /// and zoom on the pointer stick (RoR2). Register after VrInput.Init, before
        /// VrInput.Load; `context` is the mod's "playing" context name.
        /// stickBindings false leaves turn and zoom unbound, for a game whose pointer
        /// stick aims (Death's Door, RE2): Orbit turns with the grip, and the player
        /// can still bind them. recentre: a default binding for Recentre (DD:
        /// "Pointer.StickClick"); unbound when null.
        /// **Camera-pivot games use <see cref="RegisterTableModes"/> instead.**
        /// </summary>
        public static void Register(string context = "Playing", bool stickBindings = true, string recentre = null)
        {
            VrUiKit.VrInput.Register(MoveTable, "Move table (both: scale, turn)", "Table", "Table", "Pointer.Grip", "Off.Grip");
            if (stickBindings)
            {
                VrUiKit.VrInput.Register(TurnRight, "Turn table right", "Table", context, "Pointer.StickRight");
                VrUiKit.VrInput.Register(TurnLeft, "Turn table left", "Table", context, "Pointer.StickLeft");
                VrUiKit.VrInput.Register(ZoomIn, "Zoom in", "Table", context, "Pointer.StickUp");
                VrUiKit.VrInput.Register(ZoomOut, "Zoom out", "Table", context, "Pointer.StickDown");
            }
            else
            {
                VrUiKit.VrInput.Register(TurnRight, "Turn table right", "Table", context);
                VrUiKit.VrInput.Register(TurnLeft, "Turn table left", "Table", context);
                VrUiKit.VrInput.Register(ZoomIn, "Zoom in", "Table", context);
                VrUiKit.VrInput.Register(ZoomOut, "Zoom out", "Table", context);
            }
            RegisterRecentre(recentre);
        }

        /// <summary>
        /// The table's actions for a **camera-pivot game** (Diorama and Turntable,
        /// the user's rule, 2026-11-06: your dominant hand moves through the world;
        /// rotating and scaling involve your off hand):
        /// - off stick: turn (left / right) and zoom (up / down);
        /// - pointer stick: pan in Diorama, move the circle in Turntable;
        /// - grips: move, scale and turn the table, as everywhere.
        /// Use it in place of Register and RegisterPan, and drop the mod's own
        /// "pan.*" actions. A player's "Swap sticks" (VrInput.SwapSticks) flips the
        /// two sticks; the rig itself never sets it.
        /// </summary>
        public static void RegisterTableModes(string context = "Playing", string recentre = null)
        {
            VrUiKit.VrInput.Register(MoveTable, "Move table (both: scale, turn)", "Table", "Table", "Pointer.Grip", "Off.Grip");
            VrUiKit.VrInput.Register(TurnRight, "Turn table right", "Table", context, "Off.StickRight");
            VrUiKit.VrInput.Register(TurnLeft, "Turn table left", "Table", context, "Off.StickLeft");
            VrUiKit.VrInput.Register(ZoomIn, "Zoom in", "Table", context, "Off.StickUp");
            VrUiKit.VrInput.Register(ZoomOut, "Zoom out", "Table", context, "Off.StickDown");
            VrUiKit.VrInput.Register(PanUp, "Pan away (Turntable: move the circle)", "Table", context, "Pointer.StickUp");
            VrUiKit.VrInput.Register(PanDown, "Pan towards you", "Table", context, "Pointer.StickDown");
            VrUiKit.VrInput.Register(PanLeft, "Pan left", "Table", context, "Pointer.StickLeft");
            VrUiKit.VrInput.Register(PanRight, "Pan right", "Table", context, "Pointer.StickRight");
            RegisterRecentre(recentre);
        }

        /// <summary>The pan stick's actions, for TableRig's Diorama (and Turntable's circle). The ids are the ones the pivot mods registered themselves.</summary>
        public const string PanUp = "pan.up", PanDown = "pan.down", PanLeft = "pan.left", PanRight = "pan.right";

        // Recentre is in no context (it works everywhere); unbound unless the game names a binding.
        private static void RegisterRecentre(string binding)
        {
            if (string.IsNullOrEmpty(binding)) VrUiKit.VrInput.Register(Recentre, "Recentre table", "Table", null);
            else VrUiKit.VrInput.Register(Recentre, "Recentre table", "Table", null, binding);
        }

        /// <summary>
        /// Superseded by <see cref="RegisterTableModes"/>, which puts pan on the
        /// pointer stick (the standard since 2026-11-06). Kept so existing calls
        /// build: it registers the pan actions on the off stick, as before.
        /// </summary>
        public static void RegisterPan(string context = "Playing")
        {
            VrUiKit.VrInput.Register(PanUp, "Pan away", "Table", context, "Off.StickUp");
            VrUiKit.VrInput.Register(PanDown, "Pan towards you", "Table", context, "Off.StickDown");
            VrUiKit.VrInput.Register(PanLeft, "Pan left", "Table", context, "Off.StickLeft");
            VrUiKit.VrInput.Register(PanRight, "Pan right", "Table", context, "Off.StickRight");
        }
    }
}
