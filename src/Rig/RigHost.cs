using System;
using UnityEngine;

namespace Dvr
{
    /// <summary>The table's stick inputs, as flags: what a game has taken this frame, or did itself.</summary>
    [Flags]
    internal enum TableInputs
    {
        None = 0,
        /// <summary>The turn axis and its snap flicks.</summary>
        Turn = 1,
        Zoom = 2,
        /// <summary>The pan stick: Diorama's pan, Turntable's circle.</summary>
        Pan = 4,
        All = Turn | Zoom | Pan,
    }

    /// <summary>This frame's table sticks, as the rig read them (before any dead zone).</summary>
    internal struct TableSticks
    {
        /// <summary>-1 (left) .. 1 (right).</summary>
        public float Turn;
        /// <summary>This frame's flicks, for snap turning or for pressing a key once.</summary>
        public bool SnapLeft, SnapRight;
        /// <summary>-1 (out) .. 1 (in).</summary>
        public float Zoom;
        /// <summary>x right, y away.</summary>
        public Vector2 Pan;
    }

    /// <summary>
    /// What every game gives the table rig (<see cref="TableRig"/>; docs/RIG.md
    /// section 3). These are abstract classes rather than interfaces with default
    /// members, which the Mono mods' runtime (net472) can't run.
    /// </summary>
    internal abstract class RigHost
    {
        /// <summary>The game's own camera: the eyes copy its render settings. Null: nothing to show.</summary>
        public abstract Camera GameCamera { get; }
        /// <summary>False when there is no world for the table (main menu, loading, the world map): Cinema.</summary>
        public abstract bool InWorld { get; }
        /// <summary>True while the game's own controls want the grips (tpm: holding an item).</summary>
        public virtual bool GripsBusy => false;
        /// <summary>No longer used: Diorama and Turntable have their own bindings now (TableActions.RegisterTableModes). Use <see cref="Taken"/>.</summary>
        public virtual bool KeepSticks => false;
        /// <summary>
        /// Table inputs the game has taken this frame: the rig doesn't turn, zoom or
        /// pan with them (tpm: the pointer stick turns an item being placed;
        /// timberborn: the turn axis turns a building). Read every frame.
        /// </summary>
        public virtual TableInputs Taken => TableInputs.None;
    }

    /// <summary>A camera-pivot game (Diorama, Turntable): builders and management games.</summary>
    internal abstract class PivotHost : RigHost
    {
        /// <summary>The game camera's pivot (focal point, world) and its yaw (degrees), or false.</summary>
        public abstract bool TryGetPivot(out Vector3 focal, out float yaw);
        /// <summary>
        /// Move the game's pivot sideways to <paramref name="focal"/> (the game keeps
        /// its height) and, if the game wants it, its yaw. Returns where the pivot
        /// really went (timberborn clamps it to the map), and the table follows that.
        /// </summary>
        public abstract Vector3 SetPivot(Vector3 focal, float yaw);
        /// <summary>
        /// Pan by this much (world units, sideways). Return true if the game did it
        /// itself (its own pan keys: ats, cata, tph); false and the core moves the
        /// pivot (SetPivot) by Pan speed.
        /// </summary>
        public virtual bool Pan(Vector3 worldDelta) => false;
        /// <summary>True: the game does the turning (cata's Q / E) and the table follows the game's yaw.</summary>
        public virtual bool GameTurns => false;
        /// <summary>The table turned by this many degrees (clockwise from above): echo it to the game if its tutorial wants to see it (tpm).</summary>
        public virtual void Turned(float degrees) { }
        /// <summary>
        /// Every frame, the table sticks (less what <see cref="RigHost.Taken"/> took),
        /// before the rig acts on them. The game may press its own keys here:
        /// - to echo them (tpm holds Q / E while turning so its tutorial sees it):
        ///   return None, and the rig still turns and zooms;
        /// - to drive them (cata turns with Q / E and zooms with the wheel):
        ///   return the inputs it did itself, and the rig skips them. With Turn,
        ///   set <see cref="GameTurns"/> so the table follows the game's yaw; with
        ///   Zoom, give <see cref="GameZoomDistance"/> so it follows the game's zoom.
        /// Pan stays <see cref="Pan"/>.
        /// </summary>
        public virtual TableInputs Sticks(TableSticks sticks) => TableInputs.None;
        /// <summary>
        /// The game camera's zoom distance while the table should follow it (cata:
        /// while it zooms, or frames a shot); null otherwise. While it changes, the
        /// table's scale changes by the same ratio, and is saved when it settles.
        /// </summary>
        public virtual float? GameZoomDistance => null;
        /// <summary>
        /// True: the table's height follows the pivot's (focal.y) whenever no grip
        /// holds it, so panning over hills keeps the ground on the table
        /// (timberborn returns the eased terrain height under its target as focal.y).
        /// </summary>
        public virtual bool FollowGroundHeight => false;
    }

    /// <summary>A character game (Orbit): the table follows the character.</summary>
    internal abstract class CharacterHost : RigHost
    {
        /// <summary>The point the table centres on (the character's feet) and whether it stands on the ground.</summary>
        public abstract bool TryGetFocus(out Vector3 feet, out bool grounded);

        /// <summary>A CharacterHost over an existing IDioramaHost (ror2-vr, deathsdoor-vr), so they can move over unchanged.</summary>
        public static CharacterHost From(IDioramaHost host, Func<bool> inWorld = null) => new FromDiorama(host, inWorld);

        private sealed class FromDiorama : CharacterHost
        {
            private readonly IDioramaHost host;
            private readonly Func<bool> inWorld;
            public FromDiorama(IDioramaHost host, Func<bool> inWorld) { this.host = host; this.inWorld = inWorld; }
            public override Camera GameCamera => host.GameCamera;
            public override bool InWorld => inWorld == null || inWorld();
            public override bool GripsBusy => host.GripsBusy;
            public override bool TryGetFocus(out Vector3 feet, out bool grounded) => host.TryGetFocus(out feet, out grounded);
        }
    }
}
