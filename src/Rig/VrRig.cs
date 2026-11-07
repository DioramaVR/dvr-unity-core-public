using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// Tabletop diorama rig (ror2-vr's, after tpm-vr's VrRig via deathsdoor-vr):
    /// two eye cameras placed so the stage sits on a virtual table in front of
    /// you. A real metre is <see cref="Scale"/> world units and the table centre
    /// follows the host's focus (the player). Unlike the game's camera the table's turn is yours:
    /// it only changes when you turn it (two-hand twist, right stick flick).
    ///
    /// Mapping from tracking space t (metres) to world w:
    ///     w = Anchor + R(Yaw) * Scale * (t - TableCentre)
    ///
    /// Controls (TPM scheme): one grip drags the stage (let go and it eases back
    /// to the focus; up/down drags keep the new table height), both grips
    /// scale and turn it about the hands, right stick left/right flicks a turn,
    /// right stick up/down zooms. RigConfig.OneGripOrbits swaps the one-hand
    /// drag for re2-vr's orbit round the focus.
    ///
    /// Game specifics are behind IDioramaHost, settings in RigConfig, and the
    /// render pipeline's camera settings in VrPipeline.
    /// </summary>
    internal sealed class VrRig : ITrackingSpace
    {
        /// <summary>Our laser, markers and panels (VrCore.VisualLayer; 31 unless the mod changes it).</summary>
        public static int VisualLayer => VrCore.VisualLayer;

        private readonly VrSession vr;
        private readonly IDioramaHost host;
        private readonly RigConfig cfg;
        private readonly Camera[] eyes = new Camera[2];
        public RenderTexture Left { get; private set; }
        /// <summary>Per eye, each frame, after the rig has set it up from the game camera: the mod's own tweaks (background, GI, post-processing).</summary>
        public System.Action<Camera> PrepareEye;
        public RenderTexture Right { get; private set; }

        public float Scale;
        public Vector3 TableCentre;             // tracking space, metres
        public Vector3 Anchor { get; private set; }
        public float Yaw { get; private set; }
        public bool Active { get; private set; }
        /// <summary>True while one or both grips are moving the table.</summary>
        public bool Dragging => dragHands != 0;
        private bool anchored;
        private float nextDiagnostic;

        // Kept while the table follows the focus: a horizontal pan that eases back
        // to zero, and a height offset (world units) that stays.
        public Vector3 PanOffset { get; private set; }
        public float HeightOffset { get; private set; }
        private float configScale = -1;
        private float focusY;

        private int dragHands;
        private bool leftGripped, rightGripped;
        private Vector3 dragWorldPivot;
        private float dragScale, dragYaw, dragHandDistance, dragHandAngle;
        private readonly GameObject[] handMarkers = new GameObject[2];

        public VrRig(VrSession vr, IDioramaHost host, RigConfig config)
        {
            this.vr = vr;
            this.host = host;
            cfg = config;
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject(i == 0 ? VrCore.Name + " Eye L" : VrCore.Name + " Eye R");
                Object.DontDestroyOnLoad(go);
                var cam = go.AddComponent<Camera>();
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.depth = -50;
                cam.enabled = false;
                var rt = new RenderTexture(vr.EyeWidth, vr.EyeHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = go.name,
                    antiAliasing = Mathf.Clamp(cfg.EyeMsaa.Value, 1, 8),
                };
                rt.Create();
                cam.targetTexture = rt;
                eyes[i] = cam;
                if (i == 0) Left = rt; else Right = rt;
            }
            VrCore.Info($"[vr] eye textures {vr.EyeWidth}x{vr.EyeHeight} msaa {Left.antiAliasing}");
        }

        public Quaternion YawRotation => Quaternion.Euler(0, Yaw, 0);
        public Vector3 TrackingToWorld(Vector3 t) => Anchor + YawRotation * ((t - TableCentre) * Scale);
        public Quaternion TrackingToWorld(Quaternion q) => YawRotation * q;
        public Vector3 TrackingDirToWorld(Vector3 d) => YawRotation * d;
        public Vector3 WorldToTracking(Vector3 w) => TableCentre + Quaternion.Inverse(YawRotation) * (w - Anchor) / Scale;
        public Pose TrackingToWorld(Pose p)
        {
            var pos = TrackingToWorld(p.UnityPosition);
            var rot = TrackingToWorld(p.UnityRotation);
            return new Pose(new System.Numerics.Vector3(pos.x, pos.y, pos.z), new System.Numerics.Quaternion(rot.x, rot.y, rot.z, rot.w));
        }
        float ITrackingSpace.WorldScale => Scale;

        /// <summary>Back on the focus, facing the way the game camera faces; drops pan and height (not the scale).</summary>
        public void Recenter()
        {
            anchored = false;
            PanOffset = Vector3.zero;
            HeightOffset = 0;
            dragHands = 0;
            orbitSpin = 0;
        }

        /// <summary>Turn the table about its centre; positive is clockwise seen from above.</summary>
        public void Turn(float degrees) => Yaw -= degrees;

        /// <summary>Call in LateUpdate, after the game has moved the player.</summary>
        public void Update()
        {
            // The config value only wins when it changes (config edit); zoom and
            // two-hand scaling own Scale otherwise and commit on release.
            if (cfg.Scale.Value != configScale) { configScale = cfg.Scale.Value; Scale = configScale; }
            // Likewise the table's place: the orbit owns it while it moves it.
            if (cfg.TableHeight.Value != configHeight) tableHeight = configHeight = cfg.TableHeight.Value;
            if (cfg.TableDistance.Value != configDistance) tableDistance = configDistance = cfg.TableDistance.Value;
            TableCentre = new Vector3(0, tableHeight, tableDistance);
            var main = host.GameCamera;
            bool haveFocus = host.TryGetFocus(out var focus, out bool grounded);
            if (!haveFocus && main != null)
            {
                // No focus (menus, lobby, death cam): show what the game camera looks at.
                focus = main.transform.position + main.transform.forward * 20f;
                grounded = true;
            }
            Active = vr.HeadValid && main != null;
            if (!Active)
            {
                SetEyes(false);
                SetMarkers(false);
                anchored = false;
                dragHands = 0;
                return;
            }

            // Vertical follow only from the ground, so jumps don't bob the table.
            if (!anchored || grounded || Mathf.Abs(focus.y - focusY) > 30f) focusY = focus.y;
            var target = new Vector3(focus.x, focusY + HeightOffset, focus.z) + PanOffset;
            if (!anchored)
            {
                Anchor = target;
                Yaw = main.transform.eulerAngles.y;
                anchored = true;
                VrCore.Info($"[vr] table centred on {focus}, yaw {Yaw:F0}, scale {Scale}");
            }
            else if (!UpdateGripDrag(focus))
            {
                UpdateStick();
                float dt = Time.unscaledDeltaTime;
                CoastOrbit(dt);
                float k = 1f - Mathf.Exp(-cfg.FollowSpeed.Value * dt);
                // The pan from a drag eases back so the focus returns to the centre.
                PanOffset = Vector3.Lerp(PanOffset, Vector3.zero, 1f - Mathf.Exp(-1.5f * dt));
                target = new Vector3(focus.x, focusY + HeightOffset, focus.z) + PanOffset;
                // Snap on stage changes and teleports.
                Anchor = (target - Anchor).sqrMagnitude > cfg.SnapDistance * cfg.SnapDistance ? target : Vector3.Lerp(Anchor, target, k);
            }
            UpdateMarkers();

            float near = 0.03f * Scale, far = Mathf.Max(60f * Scale, 2000f);
            bool skybox = RenderSettings.skybox != null;
            for (int i = 0; i < 2; i++)
            {
                vr.GetEyePose(i, out var p, out var q);
                var cam = eyes[i];
                cam.transform.SetPositionAndRotation(TrackingToWorld(p), TrackingToWorld(q));
                cam.nearClipPlane = near;
                cam.farClipPlane = far;
                cam.projectionMatrix = vr.GetProjection(i, near, far);
                cam.cullingMask = main.cullingMask | (1 << VisualLayer);
                cam.clearFlags = skybox ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                cam.backgroundColor = RenderSettings.fogColor;
                VrPipeline.CopyEyeSettings(cam, main);
                PrepareEye?.Invoke(cam);
            }
            SetEyes(true);

            if (Time.unscaledTime >= nextDiagnostic)
            {
                nextDiagnostic = Time.unscaledTime + 10f;
                VrCore.Info($"[vr] head {vr.HeadPosition} anchor {Anchor} yaw {Yaw:F0} scale {Scale:F1} focus {(haveFocus ? focus.ToString() : "-")}");
            }
        }

        // Right stick (rebindable): left/right spins the table, speeding up to
        // TurnSpeed and coasting to a stop when let go (tpm-vr's turntable);
        // up/down zooms.
        private float spin;
        private const float SpinAcceleration = 360f, SpinCoast = 0.25f;

        private void UpdateStick()
        {
            float dt = Time.unscaledDeltaTime;
            float input = VrUiKit.VrInput.Value(cfg.TurnRight) - VrUiKit.VrInput.Value(cfg.TurnLeft);
            if (Mathf.Abs(input) > 0.2f)
            {
                float target = (Mathf.Abs(input) - 0.2f) / 0.8f * Mathf.Sign(input) * cfg.TurnSpeed.Value;
                spin = Mathf.MoveTowards(spin, target, SpinAcceleration * dt);
            }
            else spin *= Mathf.Exp(-dt / SpinCoast);
            if (Mathf.Abs(spin) < 0.5f) spin = 0;
            Turn(spin * dt);

            float y = VrUiKit.VrInput.Value(cfg.ZoomIn) - VrUiKit.VrInput.Value(cfg.ZoomOut);
            if (Mathf.Abs(y) > 0.25f)
            {
                // Up zooms in (fewer world units per metre).
                y = (Mathf.Abs(y) - 0.25f) / 0.75f * Mathf.Sign(y);
                Scale = Mathf.Clamp(Scale * Mathf.Exp(-y * cfg.ZoomSpeed.Value * dt), cfg.MinScale, cfg.MaxScale);
                zoomed = true;
            }
            else if (zoomed)
            {
                zoomed = false;
                CommitScale();
            }
        }
        private bool zoomed;

        /// <summary>Undo a drag in progress (the VR menu chord grips both hands).</summary>
        public void CancelGripDrag()
        {
            if (dragHands == 0) return;
            Anchor = dragStartAnchor;
            Yaw = dragStartYaw;
            Scale = dragStartScale;
            dragHands = 0;
            cancelled = true;
        }
        private Vector3 dragStartAnchor;
        private float dragStartYaw, dragStartScale;
        private bool cancelled;

        private void CommitScale()
        {
            if (Mathf.Abs(Scale - cfg.Scale.Value) < 0.01f) return;
            configScale = Scale;
            cfg.Scale.Value = Scale;   // saved to the cfg, so it sticks next launch
        }

        /// <summary>
        /// One gripping hand drags the stage (any direction; up/down sets the table
        /// height); two also scale it (pull apart = bigger) and turn it about their
        /// midpoint. Returns true while a drag owns the table.
        /// </summary>
        private bool UpdateGripDrag(Vector3 focus)
        {
            var l = vr.LeftHand;
            var r = vr.RightHand;
            bool free = !host.GripsBusy;
            // Grips stay off after a cancelled drag until both are let go.
            bool anyGrip = VrUiKit.VrInput.HeldOnSide(cfg.MoveTable, true) || VrUiKit.VrInput.HeldOnSide(cfg.MoveTable, false);
            if (cancelled && !anyGrip) cancelled = false;
            free &= !cancelled && !VrUiKit.VrUi.MenuOpen;
            bool lg = free && l.Valid && VrUiKit.VrInput.HeldOnSide(cfg.MoveTable, true), rg = free && r.Valid && VrUiKit.VrInput.HeldOnSide(cfg.MoveTable, false);
            int hands = (lg ? 1 : 0) + (rg ? 1 : 0);
            if (lg && !leftGripped) l.Buzz(vr.System);
            if (rg && !rightGripped) r.Buzz(vr.System);
            leftGripped = lg;
            rightGripped = rg;

            // One hand orbits instead of dragging: the drag below sees no hands
            // (so a two-hand drag ends cleanly when one hand lets go).
            bool orbit = cfg.OneGripOrbits && hands == 1;
            UpdateOrbit(orbit, orbit ? (lg ? l : r).Position : Vector3.zero);
            if (orbit) hands = 0;
            else if (hands == 2) orbitSpin = 0;

            Vector3 pivot = Vector3.zero;
            float distance = 0, angle = 0;
            if (hands == 2)
            {
                pivot = (l.Position + r.Position) * 0.5f;
                var span = r.Position - l.Position;
                distance = Mathf.Max(new Vector2(span.x, span.z).magnitude, 0.01f);
                angle = Mathf.Atan2(span.x, span.z) * Mathf.Rad2Deg;
            }
            else if (hands == 1)
            {
                pivot = (lg ? l : r).Position;
            }

            if (hands != dragHands)
            {
                if (dragHands == 0) { dragStartAnchor = Anchor; dragStartYaw = Yaw; dragStartScale = Scale; }
                if (dragHands == 2 && hands < 2) CommitScale();
                dragHands = hands;
                if (hands == 0)
                {
                    // Keep the height; the horizontal pan eases back to the focus.
                    var off = Anchor - new Vector3(focus.x, focusY, focus.z);
                    HeightOffset = off.y;
                    PanOffset = new Vector3(off.x, 0, off.z);
                    VrCore.Info($"[vr] drag released: pan {PanOffset}, height {HeightOffset:F1}, yaw {Yaw:F0}, scale {Scale:F1}");
                    return false;
                }
                // Remember which world point is under the pivot; it stays glued there.
                dragWorldPivot = TrackingToWorld(pivot);
                dragScale = Scale;
                dragYaw = Yaw;
                dragHandDistance = distance;
                dragHandAngle = angle;
                return true;
            }
            if (hands == 0) return false;

            if (hands == 2)
            {
                Scale = Mathf.Clamp(dragScale * dragHandDistance / distance, cfg.MinScale, cfg.MaxScale);
                Yaw = dragYaw - Mathf.DeltaAngle(dragHandAngle, angle) * cfg.TwistGain;
            }
            // Solve the anchor so dragWorldPivot maps to the current pivot.
            Anchor = dragWorldPivot - YawRotation * ((pivot - TableCentre) * Scale);
            return true;
        }

        // Orbit (re2-vr): the hand's movement since the grab, in tracking axes.
        private float tableHeight, tableDistance, configHeight = float.NaN, configDistance = float.NaN;
        private bool orbiting;
        private Vector3 orbitHand;
        private float orbitYaw, orbitHeight, orbitDistance, orbitSpin;
        private const float OrbitDeadZone = 0.02f;   // metres of up/down or forward/back hand movement ignored

        private void UpdateOrbit(bool on, Vector3 hand)
        {
            if (!on)
            {
                if (!orbiting) return;
                // Let go: orbitSpin keeps turning the table (CoastOrbit).
                orbiting = false;
                configHeight = cfg.TableHeight.Value = tableHeight;
                configDistance = cfg.TableDistance.Value = tableDistance;
                VrCore.Info($"[vr] orbit released: yaw {Yaw:F0}, table height {tableHeight:F2} distance {tableDistance:F2}, coasting {orbitSpin:F0} deg/s");
                return;
            }
            if (!orbiting)
            {
                orbiting = true;
                orbitHand = hand;
                orbitYaw = Yaw;
                orbitHeight = tableHeight;
                orbitDistance = tableDistance;
                orbitSpin = 0;
                return;
            }
            float dt = Time.unscaledDeltaTime;
            var d = hand - orbitHand;
            // Turning is the usual intent, so it's the strongest; distance and
            // height are damped, and a sideways sweep swallows the up/down and
            // forward/back drift that comes with it (OrbitSidewaysLock of the
            // sideways movement, plus a small dead zone), so turning leaves them be.
            float ignore = OrbitDeadZone + cfg.OrbitSidewaysLock * Mathf.Abs(d.x);
            float dy = Mathf.Sign(d.y) * Mathf.Max(0f, Mathf.Abs(d.y) - ignore);
            float dz = Mathf.Sign(d.z) * Mathf.Max(0f, Mathf.Abs(d.z) - ignore);
            tableDistance = Mathf.Clamp(orbitDistance + dz * cfg.OrbitDistanceGain, 0.1f, 2f);
            tableHeight = Mathf.Clamp(orbitHeight + dy * cfg.OrbitHeightGain, 0f, 2f);
            // Sideways is an arc at the table distance: the table turns with the hand.
            float target = orbitYaw + cfg.OrbitTurnGain * Mathf.Rad2Deg * d.x / Mathf.Max(tableDistance, 0.2f);
            float step = Mathf.DeltaAngle(Yaw, target) * (1f - Mathf.Exp(-cfg.OrbitFollow * dt));
            Yaw += step;
            if (dt > 0) orbitSpin += (step / dt - orbitSpin) * Mathf.Min(1f, dt * 12f);
        }

        private void CoastOrbit(float dt)
        {
            if (orbiting) return;
            if (Mathf.Abs(orbitSpin) < 1f) { orbitSpin = 0; return; }
            Yaw += orbitSpin * dt;
            orbitSpin *= Mathf.Exp(-cfg.OrbitDamping * dt);
        }

        // Small spheres where the controllers are, so you can see what you grab with.
        private void UpdateMarkers()
        {
            var hands = new[] { vr.LeftHand, vr.RightHand };
            for (int i = 0; i < 2; i++)
            {
                var h = hands[i];
                if (handMarkers[i] == null) handMarkers[i] = VrVisuals.Sphere(i == 0 ? VrCore.Name + " Hand L" : VrCore.Name + " Hand R", new Color(0.9f, 0.85f, 0.7f, 0.85f));
                var go = handMarkers[i];
                if (go.activeSelf != h.Valid) go.SetActive(h.Valid);
                if (!h.Valid) continue;
                go.transform.position = TrackingToWorld(h.Position);
                go.transform.localScale = Vector3.one * ((h.GripHeld ? 0.04f : 0.025f) * Scale);
            }
        }

        private void SetMarkers(bool on)
        {
            foreach (var go in handMarkers)
                if (go != null && !on && go.activeSelf) go.SetActive(false);
        }

        public Camera Eye(int i) => eyes[i];

        private void SetEyes(bool on)
        {
            foreach (var cam in eyes)
                if (cam.enabled != on) cam.enabled = on;
        }
    }
}
