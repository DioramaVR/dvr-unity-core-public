using UnityEngine;

namespace Dvr
{
    /// <summary>The three standard control modes (docs/RIG.md). Cinema is automatic, not a mode.</summary>
    internal enum RigMode { Diorama, Turntable, Orbit }

    internal enum TurnStyle { Smooth, Snap }
    /// <summary>Orbit's one grip: circle round the character, or Diorama's drag (eases back).</summary>
    internal enum OneGrip { Orbit, Drag }
    /// <summary>What is behind the world: a plain colour, the passthrough key, or the game camera's own clear (sky).</summary>
    internal enum Backdrop { Light, Dark, Passthrough, Game }
    internal enum EdgeStyle { Sharp, Soft }

    /// <summary>
    /// Every tunable of the table, as plain values: <see cref="RigSettings"/>
    /// fills one from the config each frame, and tests set them directly. The
    /// initial values are the core's defaults (RIG.md section 2).
    /// </summary>
    internal sealed class TableTuning
    {
        // Shared by every mode ([Dvr.Table]).
        public float TableHeight = 0.75f, TableDistance = 0.6f;
        public bool FollowGameCamera = true;

        // Every mode.
        public float Scale = 40f, MinScale = 3f, MaxScale = 400f;
        public TurnStyle Turn = TurnStyle.Smooth;
        public float TurnSpeed = 120f, SnapAngle = 45f, TurnRamp = 0.25f, SpinCoast = 0.5f;
        public float ZoomSpeed = 1.2f, PanSpeed = 0.5f, TwistGain = 1f, DeadZone = 0.25f;

        // Turntable.
        public float Radius = 0.6f;
        public EdgeStyle Edge = EdgeStyle.Soft;
        public Backdrop Background = Backdrop.Light;
        public string KeyColour = "009933";
        public bool Fling = true;
        public float MaxFling = 720f, CircleSpeed = 0.5f, CircleReach = 1f;
        /// <summary>How far the cut reaches above / below the table surface, metres (above: at least CutMinAboveWorld world units).</summary>
        public float CutAbove = 0.6f, CutBelow = 0.15f, CutMinAboveWorld = 60f;

        // Orbit (Death's Door's values, the user's choice 2026-11-06).
        public float OrbitTurn = 4.5f, OrbitHeight = 0.5f, OrbitDistance = 0.75f;
        public float OrbitSmoothing = 14f, OrbitCoast = 2.5f, OrbitSidewaysLock = 0.5f;
        public float FollowSpeed = 4f, SnapDistance = 100f;
        public OneGrip OneGrip = OneGrip.Orbit;
        public bool Cut;
        public float CutRadius = 0.5f;

        public TableTuning Clone() => (TableTuning)MemberwiseClone();

        public const float MinTableHeight = 0.3f, MaxTableHeight = 1.5f, MinTableDistance = 0.1f, MaxTableDistance = 2f;
    }

    /// <summary>One frame's input to the table, in tracking space. The rig fills it from VrInput.</summary>
    internal struct TableInput
    {
        public float Dt;
        /// <summary>Grips that may move the table (already off while the menu is open, a panel is carried, or the game wants them).</summary>
        public bool LeftGrip, RightGrip;
        public Vector3 Left, Right;
        public bool PointerIsLeft;
        public Vector3 HeadForward;
        /// <summary>The turn axis (-1 left .. 1 right) and, for Snap, this frame's flicks.</summary>
        public float Turn;
        public bool SnapLeft, SnapRight;
        /// <summary>The zoom axis (up = in) and the pan stick (x right, y away).</summary>
        public float Zoom;
        public Vector2 Pan;
    }

    /// <summary>
    /// The table's state and its controls, for both kinds of game (RIG.md
    /// section 1), with nothing of Unity's native side: the rig feeds it the
    /// hosts' view and the input, and reads back the mapping and the requests.
    /// </summary>
    internal sealed class TableModel
    {
        public RigMode Mode { get; private set; } = RigMode.Diorama;
        /// <summary>True for a camera-pivot game (IPivotHost), false for a character game.</summary>
        public readonly bool Pivot;

        // The mapping.
        public Vector3 Anchor;
        public float Yaw;
        public float Scale = 40f;
        public float TableHeight = 0.75f, TableDistance = 0.6f;
        /// <summary>Turntable: the circle moved across the table by the stick (metres, x / z).</summary>
        public Vector2 CircleOffset;
        public bool Anchored;

        public Vector3 TableCentre => new Vector3(CircleOffset.x, TableHeight, TableDistance + CircleOffset.y);
        public Vector3 TrackingToWorld(Vector3 t) => RigMath.TrackingToWorld(t, Anchor, Yaw, Scale, TableCentre);
        public Vector3 WorldToTracking(Vector3 w) => RigMath.WorldToTracking(w, Anchor, Yaw, Scale, TableCentre);

        // This frame's requests to the rig (reset by each Step).
        /// <summary>The anchor moved sideways: tell the game's pivot (pivot games).</summary>
        public bool PushPivot;
        /// <summary>Degrees the table was turned this frame by the stick or the turntable grip (positive clockwise from above).</summary>
        public float Turned;
        /// <summary>Pan wanted this frame, world units (pivot games, Diorama). The rig lets the game do it, or moves the anchor.</summary>
        public Vector3 PanWorld;
        /// <summary>Scale / table height and distance settled this frame: save them.</summary>
        public bool CommitScale, CommitTable;

        public int DragHands { get; private set; }
        public bool Dragging => DragHands != 0;
        public bool Orbiting => orbiting;
        public float SpinSpeed => spin;

        public TableModel(bool pivot) { Pivot = pivot; }

        /// <summary>Switch mode: drops a drag in progress and the turntable's circle offset.</summary>
        public void SetMode(RigMode mode, float groundY = float.NaN)
        {
            if (mode == Mode) return;
            Mode = mode;
            DragHands = 0;
            orbiting = false;
            spin = 0;
            CircleOffset = Vector2.zero;
            // Turntable keeps the ground on the table surface.
            if (mode == RigMode.Turntable && !float.IsNaN(groundY)) Anchor.y = groundY;
        }

        /// <summary>Back on the focus, facing the way the game faces; drops pan, height offset and the circle offset (not the scale).</summary>
        public void Recenter()
        {
            Anchored = false;
            PanOffset = Vector3.zero;
            HeightOffset = 0;
            DragHands = 0;
            orbitSpin = 0;
            spin = 0;
            CircleOffset = Vector2.zero;
        }

        /// <summary>Turn the table about its centre; positive is clockwise seen from above.</summary>
        public void Turn(float degrees)
        {
            Yaw -= degrees;
            Turned += degrees;
        }

        // ---- a drag in progress ----
        private enum DragKind { World, Slide, Rim, ScaleTurn }
        private DragKind kind;
        private Vector3 startAnchor, dragWorldPivot;
        private float startYaw, startScale, startHeight, startDistance;
        private float dragScale, dragYaw, dragHandDistance, dragHandAngle, rimRate;
        private bool cancelled;

        /// <summary>Undo the drag in progress (the menu chord grips both hands). Grips stay off until both are let go.</summary>
        public void CancelDrag()
        {
            if (DragHands == 0) return;
            Anchor = startAnchor; Yaw = startYaw; Scale = startScale;
            TableHeight = startHeight; TableDistance = startDistance;
            DragHands = 0;
            orbiting = false;
            orbitSpin = 0;
            cancelled = true;
            PushPivot = Pivot;
        }

        // ---- a pivot game: Diorama and Turntable ----

        /// <summary>
        /// One frame for a camera-pivot game. focal / gameYaw are the game's pivot
        /// when it has one (havePivot). gameTurns: the game turns (cata's Q / E),
        /// and the table follows the game's yaw. skip: stick inputs the game took or
        /// did itself this frame. followGround: the height follows focal.y while no
        /// grip holds the table (timberborn's terrain).
        /// </summary>
        public void StepPivot(in TableInput i, TableTuning t, bool havePivot, Vector3 focal, float gameYaw, bool gameTurns = false,
            TableInputs skip = TableInputs.None, bool followGround = false)
        {
            BeginFrame();
            if (!Anchored)
            {
                if (!havePivot) return;
                Anchor = focal;
                Yaw = gameYaw;
                Anchored = true;
                lastGameYaw = gameYaw;
                return;
            }
            if (gameTurns && havePivot && !float.IsNaN(lastGameYaw)) Yaw += RigMath.DeltaAngle(lastGameYaw, gameYaw);
            lastGameYaw = havePivot ? gameYaw : float.NaN;

            if (PivotGrips(i, t)) { spin = 0; return; }
            Sticks(i, t, pan: true, skip);
            if (t.FollowGameCamera && havePivot)
            {
                // Sideways only: the height is ours (grip or setting), unless it follows the ground.
                Anchor.x = focal.x;
                Anchor.z = focal.z;
            }
            if (followGround && havePivot) Anchor.y = focal.y;
        }

        // ---- following the game's own zoom (cata) ----
        private float lastGameZoom = float.NaN;
        private bool gameZoomed;

        /// <summary>
        /// Follow the game camera's zoom distance: while it changes, the scale
        /// changes by the same ratio; when it settles (or stops being given), the
        /// scale is saved. Null: not following now.
        /// </summary>
        public void FollowGameZoom(float? distance, TableTuning t)
        {
            float d = distance ?? float.NaN;
            if (!float.IsNaN(d) && d > 0 && !float.IsNaN(lastGameZoom) && lastGameZoom > 0 && Mathf.Abs(d - lastGameZoom) > 1e-5f)
            {
                Scale = Mathf.Clamp(Scale * d / lastGameZoom, t.MinScale, t.MaxScale);
                gameZoomed = true;
            }
            else if (gameZoomed)
            {
                gameZoomed = false;
                CommitScale = true;
            }
            lastGameZoom = d;
        }
        private float lastGameYaw = float.NaN;

        private bool PivotGrips(in TableInput i, TableTuning t)
        {
            bool lg = i.LeftGrip, rg = i.RightGrip;
            if (cancelled) { if (!lg && !rg) cancelled = false; return false; }
            int hands = (lg ? 1 : 0) + (rg ? 1 : 0);
            Vector3 pivot = default;
            float distance = 0, angle = 0;
            if (hands == 2)
            {
                pivot = (i.Left + i.Right) * 0.5f;
                var span = i.Right - i.Left;
                distance = Mathf.Max(span.magnitude, 0.01f);
                angle = RigMath.Heading(span);
            }
            else if (hands == 1) pivot = lg ? i.Left : i.Right;

            bool turntable = Mode == RigMode.Turntable;
            bool pointerHand = hands == 1 && lg == i.PointerIsLeft;
            // Turntable's off grip: the hand's angle round the table centre.
            if (hands == 1 && turntable && !pointerHand) angle = RigMath.Heading(pivot - TableCentre);

            if (hands != DragHands)
            {
                if (DragHands == 0) StartValues();
                if (DragHands == 2 && hands < 2) CommitScale = true;
                if (DragHands == 1 && hands == 0 && kind == DragKind.Rim && t.Fling)
                    spin = Mathf.Clamp(rimRate, -t.MaxFling, t.MaxFling);
                if (hands == 0 && (kind == DragKind.World)) FoldHeightIntoTable();
                rimRate = 0;
                DragHands = hands;
                if (hands == 0) return false;
                kind = hands == 2 ? (turntable ? DragKind.ScaleTurn : DragKind.World)
                    : turntable ? (pointerHand ? DragKind.Slide : DragKind.Rim)
                    : DragKind.World;
                dragWorldPivot = TrackingToWorld(pivot);
                dragScale = Scale;
                dragYaw = Yaw;
                dragHandDistance = distance;
                dragHandAngle = angle;
                return true;
            }
            if (hands == 0) return false;

            switch (kind)
            {
                case DragKind.ScaleTurn:
                    Scale = Mathf.Clamp(dragScale * dragHandDistance / distance, t.MinScale, t.MaxScale);
                    Yaw = dragYaw - RigMath.DeltaAngle(dragHandAngle, angle) * t.TwistGain;
                    break;
                case DragKind.Rim:
                {
                    float newYaw = dragYaw - RigMath.DeltaAngle(dragHandAngle, angle);
                    float step = -RigMath.DeltaAngle(Yaw, newYaw);   // Turn's sign
                    float dt = Mathf.Max(i.Dt, 1e-4f);
                    rimRate = Mathf.Lerp(rimRate, step / dt, 0.3f);
                    Turned += step;
                    Yaw = newYaw;
                    break;
                }
                case DragKind.Slide:
                {
                    var slid = RigMath.SolveAnchor(dragWorldPivot, pivot, Yaw, Scale, TableCentre);
                    Anchor = new Vector3(slid.x, startAnchor.y, slid.z);
                    PushPivot = true;
                    break;
                }
                default:
                    if (hands == 2)
                    {
                        Scale = Mathf.Clamp(dragScale * dragHandDistance / distance, t.MinScale, t.MaxScale);
                        Yaw = dragYaw - RigMath.DeltaAngle(dragHandAngle, angle) * t.TwistGain;
                    }
                    Anchor = RigMath.SolveAnchor(dragWorldPivot, pivot, Yaw, Scale, TableCentre);
                    PushPivot = true;
                    break;
            }
            return true;
        }

        /// <summary>
        /// A drag's up / down becomes the table height (one shared setting, saved),
        /// instead of a world offset: lowering the anchor by d world units is the
        /// same picture as raising the table by d / Scale metres.
        /// </summary>
        private void FoldHeightIntoTable()
        {
            float dy = startAnchor.y - Anchor.y;
            if (Mathf.Abs(dy) < 1e-4f) return;
            float h = Mathf.Clamp(TableHeight + dy / Scale, TableTuning.MinTableHeight, TableTuning.MaxTableHeight);
            float applied = h - TableHeight;
            TableHeight = h;
            Anchor.y += applied * Scale;
            CommitTable = true;
        }

        // ---- the sticks, both kinds ----
        private float spin;
        private bool zooming;

        private void Sticks(in TableInput i, TableTuning t, bool pan, TableInputs skip = TableInputs.None)
        {
            float dt = i.Dt;
            bool turn = (skip & TableInputs.Turn) == 0;
            if (t.Turn == TurnStyle.Snap)
            {
                spin = 0;
                if (turn && i.SnapRight) Turn(t.SnapAngle);
                if (turn && i.SnapLeft) Turn(-t.SnapAngle);
            }
            else
            {
                spin = RigMath.Spin(spin, turn ? RigMath.Curve(i.Turn, t.DeadZone) : 0, t.TurnSpeed, t.TurnRamp, t.SpinCoast, dt);
                if (spin != 0) Turn(spin * dt);
            }

            float z = (skip & TableInputs.Zoom) == 0 ? RigMath.Curve(i.Zoom, t.DeadZone) : 0;
            if (z != 0)
            {
                Scale = RigMath.Zoom(Scale, z, t.ZoomSpeed, dt, t.MinScale, t.MaxScale);
                zooming = true;
            }
            else if (zooming)
            {
                zooming = false;
                CommitScale = true;
            }

            if (!pan || (skip & TableInputs.Pan) != 0) return;
            var p = RigMath.Curve(i.Pan, t.DeadZone);
            if (p == Vector2.zero) return;
            var dir = RigMath.HeadRelative(p, i.HeadForward);
            if (Mode == RigMode.Turntable)
            {
                // tpm-vr: pushing away brings the circle (and the world) towards you, as if you moved.
                var o = CircleOffset - new Vector2(dir.x, dir.z) * (t.CircleSpeed * dt);
                CircleOffset = o.magnitude > t.CircleReach ? o.normalized * t.CircleReach : o;
            }
            else PanWorld += RigMath.Yaw(dir, Yaw) * (t.PanSpeed * dt * Scale);
        }

        // ---- a character game: Orbit (and Diorama's drag, for a game that offers it) ----
        public Vector3 PanOffset { get; private set; }
        public float HeightOffset { get; private set; }
        private float focusY;
        private bool orbiting;
        private Vector3 orbitHand;
        private float orbitYaw, orbitHeight, orbitDistance, orbitSpin;
        private const float OrbitDeadZone = 0.02f;
        private const float OrbitMinDistance = 0.1f, OrbitMaxDistance = 2f, OrbitMinHeight = 0f, OrbitMaxHeight = 2f;

        /// <summary>
        /// One frame for a character game (from the core's VrRig, ror2-vr / deathsdoor-vr):
        /// the table follows the focus (the character's feet), only following it up or
        /// down from the ground. viewYaw is the way the game faces, for the first frame.
        /// </summary>
        public void StepCharacter(in TableInput i, TableTuning t, Vector3 focus, bool grounded, float viewYaw, TableInputs skip = TableInputs.None)
        {
            BeginFrame();
            if (!Anchored || grounded || Mathf.Abs(focus.y - focusY) > 30f) focusY = focus.y;
            var target = new Vector3(focus.x, focusY + HeightOffset, focus.z) + PanOffset;
            if (!Anchored)
            {
                Anchor = target;
                Yaw = viewYaw;
                Anchored = true;
                return;
            }
            if (CharacterGrips(i, t, focus)) return;
            Sticks(i, t, pan: false, skip);
            float dt = i.Dt;
            CoastOrbit(t, dt);
            PanOffset = Vector3.Lerp(PanOffset, Vector3.zero, 1f - Mathf.Exp(-1.5f * dt));
            target = new Vector3(focus.x, focusY + HeightOffset, focus.z) + PanOffset;
            float k = 1f - Mathf.Exp(-t.FollowSpeed * dt);
            Anchor = (target - Anchor).sqrMagnitude > t.SnapDistance * t.SnapDistance ? target : Vector3.Lerp(Anchor, target, k);
        }

        private bool CharacterGrips(in TableInput i, TableTuning t, Vector3 focus)
        {
            bool lg = i.LeftGrip, rg = i.RightGrip;
            if (cancelled) { if (!lg && !rg) cancelled = false; lg = rg = false; }
            int hands = (lg ? 1 : 0) + (rg ? 1 : 0);

            bool orbit = Mode == RigMode.Orbit && t.OneGrip == OneGrip.Orbit && hands == 1;
            UpdateOrbit(orbit, orbit ? (lg ? i.Left : i.Right) : Vector3.zero, t, i.Dt);
            if (orbit) hands = 0;
            else if (hands == 2) orbitSpin = 0;

            Vector3 pivot = default;
            float distance = 0, angle = 0;
            if (hands == 2)
            {
                pivot = (i.Left + i.Right) * 0.5f;
                var span = i.Right - i.Left;
                distance = Mathf.Max(new Vector2(span.x, span.z).magnitude, 0.01f);
                angle = RigMath.Heading(span);
            }
            else if (hands == 1) pivot = lg ? i.Left : i.Right;

            if (hands != DragHands)
            {
                if (DragHands == 0 && !orbiting) StartValues();
                if (DragHands == 2 && hands < 2) CommitScale = true;
                DragHands = hands;
                if (hands == 0)
                {
                    // Keep the height; the sideways pan eases back to the focus.
                    var off = Anchor - new Vector3(focus.x, focusY, focus.z);
                    HeightOffset = off.y;
                    PanOffset = new Vector3(off.x, 0, off.z);
                    return orbit;
                }
                dragWorldPivot = TrackingToWorld(pivot);
                dragScale = Scale;
                dragYaw = Yaw;
                dragHandDistance = distance;
                dragHandAngle = angle;
                return true;
            }
            if (hands == 0) return orbit;
            if (hands == 2)
            {
                Scale = Mathf.Clamp(dragScale * dragHandDistance / distance, t.MinScale, t.MaxScale);
                Yaw = dragYaw - RigMath.DeltaAngle(dragHandAngle, angle) * t.TwistGain;
            }
            Anchor = RigMath.SolveAnchor(dragWorldPivot, pivot, Yaw, Scale, TableCentre);
            return true;
        }

        // re2-vr's orbit: the hand's movement since the grab, in tracking axes.
        // Sideways turns the table (strongest, eased, coasting when let go);
        // up / down sets the table height and forward / back its distance, both
        // damped, and a sideways sweep swallows the drift that comes with it.
        private void UpdateOrbit(bool on, Vector3 hand, TableTuning t, float dt)
        {
            if (!on)
            {
                if (!orbiting) return;
                orbiting = false;
                CommitTable = true;
                return;
            }
            if (!orbiting)
            {
                orbiting = true;
                StartValues();
                orbitHand = hand;
                orbitYaw = Yaw;
                orbitHeight = TableHeight;
                orbitDistance = TableDistance;
                orbitSpin = 0;
                return;
            }
            var d = hand - orbitHand;
            float ignore = OrbitDeadZone + t.OrbitSidewaysLock * Mathf.Abs(d.x);
            float dy = Mathf.Sign(d.y) * Mathf.Max(0f, Mathf.Abs(d.y) - ignore);
            float dz = Mathf.Sign(d.z) * Mathf.Max(0f, Mathf.Abs(d.z) - ignore);
            TableDistance = Mathf.Clamp(orbitDistance + dz * t.OrbitDistance, OrbitMinDistance, OrbitMaxDistance);
            TableHeight = Mathf.Clamp(orbitHeight + dy * t.OrbitHeight, OrbitMinHeight, OrbitMaxHeight);
            float target = orbitYaw + t.OrbitTurn * Mathf.Rad2Deg * d.x / Mathf.Max(TableDistance, 0.2f);
            float step = RigMath.DeltaAngle(Yaw, target) * (1f - Mathf.Exp(-t.OrbitSmoothing * dt));
            Yaw += step;
            if (dt > 0) orbitSpin += (step / dt - orbitSpin) * Mathf.Min(1f, dt * 12f);
        }

        private void CoastOrbit(TableTuning t, float dt)
        {
            if (orbiting) return;
            if (Mathf.Abs(orbitSpin) < 1f) { orbitSpin = 0; return; }
            Yaw += orbitSpin * dt;
            orbitSpin *= Mathf.Exp(-t.OrbitCoast * dt);
        }

        // ---- shared ----
        private void BeginFrame()
        {
            PushPivot = false;
            Turned = 0;
            PanWorld = Vector3.zero;
            CommitScale = CommitTable = false;
        }

        private void StartValues()
        {
            startAnchor = Anchor; startYaw = Yaw; startScale = Scale;
            startHeight = TableHeight; startDistance = TableDistance;
        }
    }
}
