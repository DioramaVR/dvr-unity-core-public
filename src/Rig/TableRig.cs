using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using VrUiKit;

namespace Dvr
{
    /// <summary>
    /// The standard table rig (docs/RIG.md, vr-urz.2): Diorama, Turntable and
    /// Orbit, with Cinema outside a world. The game gives a <see cref="PivotHost"/>
    /// (Diorama, Turntable) or a <see cref="CharacterHost"/> (Orbit), and offers
    /// its modes with its own defaults:
    /// <code>
    /// rig = new TableRig(vr, Config, host)
    ///     .Table(t => t.TableHeight = 0.75f, old: new OldKey("TableHeight", "VR", "TableHeight", 0.9f))
    ///     .Offer(RigMode.Diorama, t => { t.Scale = 40; t.MinScale = 5; t.MaxScale = 300; }, null,
    ///            new OldKey("Scale", "VR", "Scale", 40f))
    ///     .Offer(RigMode.Turntable, t => t.Scale = 40);
    /// rig.PrepareEye = cam => ...;          // per-eye tweaks that stay the game's
    /// // each frame, after VrInput.Update: rig.Update(); then submit (SubmitEyes / QueuePreviousFrame)
    /// </code>
    /// The core's older <see cref="VrRig"/> is unchanged; ror2-vr and deathsdoor-vr
    /// keep using it until they move over (CharacterHost.From wraps their IDioramaHost).
    /// </summary>
    internal sealed class TableRig : ITrackingSpace
    {
        private readonly VrSession vr;
        private readonly RigHost host;
        private readonly PivotHost pivotHost;
        private readonly CharacterHost characterHost;
        public readonly RigSettings Settings;
        private readonly TableModel model;
        private readonly TableTuning tuning = new TableTuning();
        private EyeTuning eyeTuning = new EyeTuning();
        private readonly List<RigMode> offered = new List<RigMode>();
        private readonly Camera[] eyes = new Camera[2];
        private readonly RenderTexture[] targets = new RenderTexture[2];
        private readonly IntPtr[] nativeTargets = new IntPtr[2];
        public readonly VrDesktop Desktop = new VrDesktop();

        /// <summary>Per eye, each frame, after the rig set it up: the game's own tweaks (GI, post-processing...).</summary>
        public Action<Camera> PrepareEye;
        /// <summary>Layers the eyes never draw, from the game camera's mask (default: the UI layer, 5).</summary>
        public int EyeLayersOff = 1 << 5;
        /// <summary>MSAA samples for the eye textures (read when they are made).</summary>
        public int Msaa = 1;
        /// <summary>False keeps zoom, pinch and table moves out of the config (sims).</summary>
        public bool SaveChanges = true;
        /// <summary>No longer used (kept so mods that set it still build): Diorama and Turntable have their own bindings (TableActions.RegisterTableModes), and the rig never touches VrInput.SwapSticks, which is left for a player's "Swap sticks".</summary>
        public bool ManagesStickSwap;
        /// <summary>Small spheres where the controllers are (ror2-vr and deathsdoor-vr had them from VrRig).</summary>
        public bool HandMarkers;
        /// <summary>Cap the shadow distance at the cut's fitted far plane while the cut is on (tpm: frame time); put back outside it.</summary>
        public bool CapShadows = true;

        public bool Active { get; private set; }
        public bool InWorld { get; private set; }
        public RigMode Mode => model.Mode;
        public TableTuning Tuning => tuning;
        public EyeTuning EyeSettings => eyeTuning;
        public bool Dragging => model.Dragging;
        public float Scale { get => model.Scale; set => model.Scale = value; }
        public Vector3 Anchor => model.Anchor;
        public float Yaw => model.Yaw;
        public Vector3 TableCentre => model.TableCentre;
        /// <summary>The cut's centre and radius in world units while Turntable (or Orbit's cut) is on; radius 0 otherwise.</summary>
        public float CutRadius { get; private set; }

        public RenderTexture Left => targets[0];
        public RenderTexture Right => targets[1];
        public Camera Eye(int i) => eyes[i];
        public float ResolutionScale { get; private set; }

        // Cinema: tracking space at 1:1 in an empty spot below the world, where
        // VrCinema shows the game's picture on a big screen.
        public static readonly Vector3 CinemaOrigin = new Vector3(0, -300, 0);

        public TableRig(VrSession vr, ConfigFile config, RigHost host)
        {
            this.vr = vr;
            this.host = host;
            pivotHost = host as PivotHost;
            characterHost = host as CharacterHost;
            if (pivotHost == null && characterHost == null) throw new ArgumentException("TableRig: the host must be a PivotHost or a CharacterHost");
            Settings = new RigSettings(config);
            model = new TableModel(pivotHost != null);
            // Home's "Recentre table" quick action (a game's own Recentre row wins over it).
            VrUiKit.MenuModel.RecentreTable = () => model.Recenter();
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject(VrCore.Name + (i == 0 ? " Eye L" : " Eye R"));
                UnityEngine.Object.DontDestroyOnLoad(go);
                var cam = go.AddComponent<Camera>();
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.depth = -10;
                cam.enabled = false;
                eyes[i] = cam;
            }
            VrInput.ChordFired += CancelGripDrag;
        }

        // ---- set-up ----

        private bool sharedBound;

        /// <summary>The settings shared by every mode (table height and distance, and the eyes'), with the game's defaults and old keys. Optional; before Offer.</summary>
        public TableRig Table(Action<TableTuning> defaults = null, Action<EyeTuning> eyes = null, params OldKey[] old)
        {
            var t = new TableTuning();
            defaults?.Invoke(t);
            var e = new EyeTuning();
            eyes?.Invoke(e);
            Settings.BindShared(t, e, old);
            foreach (var row in Settings.GraphicsRows()) VrSettings.Add(row);
            sharedBound = true;
            VrUiMenu.MarkDirty();   // late registration (a mod that makes the rig once VR starts) still shows
            return this;
        }

        /// <summary>
        /// Offer a mode, in menu order, with the game's defaults for it. It goes on
        /// the Mode tab (VrModes) with its rows; controls names the game's action
        /// sections the mode uses besides the table's.
        /// </summary>
        public TableRig Offer(RigMode mode, Action<TableTuning> defaults = null, string[] controls = null, params OldKey[] old)
        {
            if (mode == RigMode.Orbit && characterHost == null) throw new ArgumentException("TableRig: Orbit needs a CharacterHost");
            if (mode == RigMode.Turntable && pivotHost == null) throw new ArgumentException("TableRig: Turntable needs a PivotHost");
            if (!sharedBound) Table();
            var t = new TableTuning();
            // The game camera's own sky behind Diorama, as every game had it; Turntable's plain colour.
            if (mode != RigMode.Turntable) t.Background = Backdrop.Game;
            defaults?.Invoke(t);
            Settings.BindMode(mode, t, old);
            offered.Add(mode);
            var vm = VrModes.Add(mode.ToString(), mode.ToString(), Describe(mode));
            foreach (var row in Settings.Rows(mode, () => CylinderMask.Available)) vm.Option(row);
            vm.Controls(new[] { "Table" }.Concat(controls ?? Array.Empty<string>()).ToArray());
            VrUiMenu.MarkDirty();
            return this;
        }

        public IReadOnlyList<RigMode> Offered => offered;

        private static string Describe(RigMode m) => m switch
        {
            RigMode.Diorama => "The whole world as a model: move, scale and turn it",
            RigMode.Turntable => "A circle of the world on a table that spins; the rest fades out",
            _ => "Your character on the table: circle round them with a grip",
        };

        // ---- the mapping ----

        private bool cinema = true;
        public float WorldScale => cinema ? 1f : model.Scale;
        private Vector3 ActiveAnchor => cinema ? CinemaOrigin + model.TableCentre : model.Anchor;
        private float ActiveYaw => cinema ? 0f : model.Yaw;

        public Vector3 TrackingToWorld(Vector3 t) => RigMath.TrackingToWorld(t, ActiveAnchor, ActiveYaw, WorldScale, model.TableCentre);
        public Vector3 WorldToTracking(Vector3 w) => RigMath.WorldToTracking(w, ActiveAnchor, ActiveYaw, WorldScale, model.TableCentre);
        public Quaternion TrackingToWorld(Quaternion q) => Quaternion.AngleAxis(ActiveYaw, Vector3.up) * q;
        public Pose TrackingToWorld(Pose p)
        {
            var pos = TrackingToWorld(p.UnityPosition);
            var rot = TrackingToWorld(p.UnityRotation);
            return new Pose(new System.Numerics.Vector3(pos.x, pos.y, pos.z), new System.Numerics.Quaternion(rot.x, rot.y, rot.z, rot.w));
        }

        // ---- controls from outside ----

        public void Recenter() => model.Recenter();

        /// <summary>Save the current scale as this mode's (a game that sets Scale itself: DD's F6 / F7). Zoom and pinch save their own.</summary>
        public void SaveScale()
        {
            if (!SaveChanges) return;
            Settings.Set(model.Mode, "Scale", model.Scale);
            configScale = model.Scale;
        }
        /// <summary>Turn the table about its centre; positive is clockwise from above.</summary>
        public void Turn(float degrees) => model.Turn(degrees);

        /// <summary>Undo a drag in progress (called on the menu chord).</summary>
        public void CancelGripDrag()
        {
            if (!model.Dragging) return;
            model.CancelDrag();
            if (pivotHost != null) PushPivot();
        }

        // ---- each frame ----

        private RigMode? lastMode;
        private float configScale = float.NaN, configHeight = float.NaN, configDistance = float.NaN;
        private bool leftGripped, rightGripped;
        private float nextDiagnostic;

        /// <summary>Each frame, after VrInput.Update and after the game has moved its camera / character.</summary>
        public void Update()
        {
            var main = host.GameCamera;
            Active = vr.HeadValid;
            InWorld = Active && main != null && host.InWorld && offered.Count > 0;
            cinema = !InWorld;
            eyeTuning = Settings.Eyes();
            Desktop.Update(InWorld ? main : null, InWorld && !eyeTuning.DesktopView);
            if (!Active)
            {
                SetEyes(false);
                Desktop.Restore();
                CylinderCull.Update(false, 0, 0, 0);
                SetMarkers(false);
                RestoreShadows();
                model.Anchored = false;
                return;
            }
            SetResolution(eyeTuning.EyeResolution);

            var mode = CurrentMode();
            Settings.Fill(mode, tuning);
            if (lastMode != mode)
            {
                float ground = pivotHost != null && pivotHost.TryGetPivot(out var f, out _) ? f.y : float.NaN;
                model.SetMode(mode, ground);
                configScale = float.NaN;   // the new mode's own scale
                lastMode = mode;
                VrCore.Info($"[rig] mode {mode}");
            }
            // A config value wins only when it changes (a menu row, a file edit);
            // zoom, pinch and the grips own the live values otherwise and save them on release.
            if (tuning.Scale != configScale) { configScale = tuning.Scale; model.Scale = Mathf.Clamp(tuning.Scale, tuning.MinScale, tuning.MaxScale); }
            if (tuning.TableHeight != configHeight) model.TableHeight = configHeight = tuning.TableHeight;
            if (tuning.TableDistance != configDistance) model.TableDistance = configDistance = tuning.TableDistance;

            if (InWorld) Step(main);
            else model.Anchored = false;
            PlaceEyes(main);

            if (Time.unscaledTime >= nextDiagnostic)
            {
                nextDiagnostic = Time.unscaledTime + 10f;
                VrCore.Info($"[rig] {(InWorld ? mode.ToString() : "Cinema")} anchor {model.Anchor} yaw {model.Yaw:F0} scale {model.Scale:F1} table h {model.TableHeight:F2} d {model.TableDistance:F2}");
            }
        }

        private RigMode CurrentMode()
        {
            var id = VrModes.Current?.Id;
            foreach (var m in offered) if (m.ToString() == id) return m;
            return offered.Count > 0 ? offered[0] : RigMode.Diorama;
        }

        private void Step(Camera main)
        {
            var input = ReadInput();
            // What the game has taken this frame never reaches the table (nor Sticks).
            var skip = host.Taken;
            if ((skip & TableInputs.Turn) != 0) { input.Turn = 0; input.SnapLeft = input.SnapRight = false; }
            if ((skip & TableInputs.Zoom) != 0) input.Zoom = 0;
            if ((skip & TableInputs.Pan) != 0) input.Pan = Vector2.zero;
            if (pivotHost != null)
            {
                // The game echoes the sticks, or drives turn / zoom itself.
                skip |= pivotHost.Sticks(new TableSticks
                {
                    Turn = input.Turn, SnapLeft = input.SnapLeft, SnapRight = input.SnapRight,
                    Zoom = input.Zoom, Pan = input.Pan,
                });
                bool have = pivotHost.TryGetPivot(out var focal, out var gameYaw);
                model.StepPivot(input, tuning, have, focal, gameYaw, pivotHost.GameTurns, skip,
                    pivotHost.FollowGroundHeight && !model.Dragging);
                model.FollowGameZoom(pivotHost.GameZoomDistance, tuning);
                if (model.PanWorld != Vector3.zero && !pivotHost.Pan(model.PanWorld))
                {
                    model.Anchor += new Vector3(model.PanWorld.x, 0, model.PanWorld.z);
                    model.PushPivot = true;
                }
                if (model.PushPivot) PushPivot();
                if (model.Turned != 0) pivotHost.Turned(model.Turned);
            }
            else
            {
                if (!characterHost.TryGetFocus(out var feet, out bool grounded))
                {
                    // No focus (lobby, death cam): what the game camera looks at.
                    feet = main.transform.position + main.transform.forward * 20f;
                    grounded = true;
                }
                model.StepCharacter(input, tuning, feet, grounded, main.transform.eulerAngles.y, skip);
            }
            if (SaveChanges && model.CommitScale) { Settings.Set(model.Mode, "Scale", model.Scale); configScale = model.Scale; }
            if (SaveChanges && model.CommitTable)
            {
                Settings.Set(null, "TableHeight", model.TableHeight); configHeight = model.TableHeight;
                Settings.Set(null, "TableDistance", model.TableDistance); configDistance = model.TableDistance;
            }
            if (VrInput.Pressed(TableActions.Recentre)) model.Recenter();
        }

        private void PushPivot()
        {
            var actual = pivotHost.SetPivot(model.Anchor, model.Yaw);
            model.Anchor = new Vector3(actual.x, model.Anchor.y, actual.z);
        }

        private TableInput ReadInput()
        {
            var l = vr.LeftHand;
            var r = vr.RightHand;
            bool free = !host.GripsBusy && !VrUi.MenuOpen && !VrUi.GrabbingWindow && !VrUi.CarryingPanel;
            bool lg = free && l.Valid && VrInput.HeldOnSide(TableActions.MoveTable, true);
            bool rg = free && r.Valid && VrInput.HeldOnSide(TableActions.MoveTable, false);
            if (lg && !leftGripped) l.Buzz(vr.System);
            if (rg && !rightGripped) r.Buzz(vr.System);
            leftGripped = lg;
            rightGripped = rg;
            return new TableInput
            {
                Dt = Time.unscaledDeltaTime,
                LeftGrip = lg, RightGrip = rg,
                Left = l.Position, Right = r.Position,
                PointerIsLeft = VrInput.LeftHanded,
                HeadForward = vr.HeadRotation * Vector3.forward,
                Turn = VrInput.Value(TableActions.TurnRight) - VrInput.Value(TableActions.TurnLeft),
                SnapRight = VrInput.Pressed(TableActions.TurnRight),
                SnapLeft = VrInput.Pressed(TableActions.TurnLeft),
                Zoom = VrInput.Value(TableActions.ZoomIn) - VrInput.Value(TableActions.ZoomOut),
                Pan = new Vector2(VrInput.Value(TableActions.PanRight) - VrInput.Value(TableActions.PanLeft),
                                  VrInput.Value(TableActions.PanUp) - VrInput.Value(TableActions.PanDown)),
            };
        }

        // ---- the eyes ----

        private static readonly Color LightColour = new Color(0.86f, 0.87f, 0.85f);
        private static readonly Color DarkColour = new Color(0.08f, 0.09f, 0.11f);

        private Color Background()
        {
            switch (tuning.Background)
            {
                case Backdrop.Dark: return DarkColour;
                case Backdrop.Passthrough:
                    return ColorUtility.TryParseHtmlString("#" + (tuning.KeyColour ?? "").Trim().TrimStart('#'), out var c) ? c : new Color(0, 0.6f, 0.2f);
                default: return LightColour;
            }
        }

        private void PlaceEyes(Camera main)
        {
            bool turntable = InWorld && model.Mode == RigMode.Turntable;
            bool cut = turntable || (InWorld && model.Mode == RigMode.Orbit && tuning.Cut);
            float scale = WorldScale;
            float radius = cut ? (turntable ? tuning.Radius : tuning.CutRadius) * scale : 0;
            CutRadius = radius;
            float above = Mathf.Max(tuning.CutAbove * scale, tuning.CutMinAboveWorld), below = tuning.CutBelow * scale;
            bool mask = cut && CylinderMask.Available;
            float near = 0.02f * scale, farMax = InWorld ? Mathf.Max(60f * scale, 2000f) : 100f;
            int worldMask = InWorld ? (Desktop.GameMask(main) & ~EyeLayersOff) | (1 << VrCore.VisualLayer) : 0;
            var centre = model.Anchor;
            bool plain = InWorld && (turntable || tuning.Background != Backdrop.Game);
            float furthest = 0;
            for (int i = 0; i < 2; i++)
            {
                var cam = eyes[i];
                var eye = vr.GetEyePose(i);
                cam.transform.SetPositionAndRotation(TrackingToWorld(eye.UnityPosition), TrackingToWorld(eye.UnityRotation));
                float far = farMax;
                Matrix4x4? cull = null;
                if (cut)
                {
                    // Without the mask shader the far plane and the culling frustum are
                    // the only cut for big meshes (terrain); with it, the eyes keep their
                    // full frustum so our own panels beside the circle aren't cut too.
                    if (mask) far = CylinderCull.FitFar(cam, centre, radius, above, below, near, farMax);
                    else cull = RigMath.FitFrustum(cam.worldToCameraMatrix, vr.EyeTangents(i), centre, radius, above, below, near, ref far);
                }
                furthest = Mathf.Max(furthest, far);
                cam.nearClipPlane = near;
                cam.farClipPlane = far;
                cam.projectionMatrix = vr.GetProjection(i, near, far);
                if (cull is Matrix4x4 m) cam.cullingMatrix = m; else cam.ResetCullingMatrix();
                if (InWorld)
                {
                    cam.cullingMask = worldMask;
                    if (plain) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Background(); }
                    else { cam.clearFlags = main.clearFlags; cam.backgroundColor = main.backgroundColor; }
                    VrPipeline.CopyEyeSettings(cam, main);
                    if (!eyeTuning.EyePostProcessing) VrPipeline.SetPostProcessing(cam, false);
                }
                else
                {
                    cam.cullingMask = 1 << VrCore.VisualLayer;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = DarkColour;
                }
                VrPipeline.SetShadows(cam, eyeTuning.EyeShadows);
                PrepareEye?.Invoke(cam);
            }
            if (cut && CapShadows) VrPipeline.CapShadowDistance(furthest); else RestoreShadows();
            UpdateMarkers();
            CylinderCull.Update(cut, centre.x, centre.z, radius);
            var colour = plain ? Background() : main != null ? main.backgroundColor : Color.black;
            CylinderMask.Update(cut, centre.x, centre.z, radius, tuning.Edge == EdgeStyle.Soft ? radius * 0.15f : 0f, colour, eyes);
            SetEyes(true);
        }

        private void RestoreShadows() => VrPipeline.CapShadowDistance(null);

        // ---- hand markers (VrRig's, for ror2-vr and deathsdoor-vr) ----
        private readonly GameObject[] handMarkers = new GameObject[2];

        private void UpdateMarkers()
        {
            if (!HandMarkers) { SetMarkers(false); return; }
            var hands = new[] { vr.LeftHand, vr.RightHand };
            for (int i = 0; i < 2; i++)
            {
                var h = hands[i];
                if (handMarkers[i] == null) handMarkers[i] = VrVisuals.Sphere(VrCore.Name + (i == 0 ? " Hand L" : " Hand R"), new Color(0.9f, 0.85f, 0.7f, 0.85f));
                var go = handMarkers[i];
                if (go.activeSelf != h.Valid) go.SetActive(h.Valid);
                if (!h.Valid) continue;
                go.transform.position = TrackingToWorld(h.Position);
                go.transform.localScale = Vector3.one * ((h.GripHeld ? 0.04f : 0.025f) * WorldScale);
            }
        }

        private void SetMarkers(bool on)
        {
            foreach (var go in handMarkers)
                if (go != null && !on && go.activeSelf) go.SetActive(false);
        }

        // ---- the game's old view-mode setting ----

        /// <summary>
        /// Once, after the Offers: the mode the player had chosen in the game's own
        /// old setting, mapped to a standard mode and selected (tpm:
        /// <c>rig.OldMode("Diorama", "View", "VrView", ("VrView", RigMode.Diorama),
        /// ("Diorama", RigMode.Turntable), ("Tabletop", RigMode.Turntable))</c>).
        /// oldDefault has the old key's type (a string works for an enum the mod no
        /// longer binds; pass the enum value if the mod still binds it).
        /// </summary>
        public TableRig OldMode(string section, string key, object oldDefault, params (string from, RigMode to)[] map)
        {
            var id = Settings.MigrateMode(section, key, oldDefault, map.Where(m => offered.Contains(m.to)));
            if (id != null) VrModes.Select(id);
            return this;
        }

        private void SetResolution(float scale)
        {
            scale = Mathf.Clamp(scale, 0.3f, 1.5f);
            if (targets[0] != null && Mathf.Abs(scale - ResolutionScale) < 0.001f) return;
            ResolutionScale = scale;
            int w = Math.Max(64, (int)(vr.EyeWidth * scale)), h = Math.Max(64, (int)(vr.EyeHeight * scale));
            for (int i = 0; i < 2; i++)
            {
                var old = targets[i];
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = eyes[i].name,
                    antiAliasing = Mathf.Clamp(Msaa, 1, 8),
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                };
                rt.Create();
                eyes[i].targetTexture = rt;
                targets[i] = rt;
                nativeTargets[i] = rt.GetNativeTexturePtr();
                if (old != null) old.Release();
            }
            VrCore.Info($"[rig] eye textures {w}x{h} ({scale:P0} of SteamVR's {vr.EyeWidth}x{vr.EyeHeight}), msaa {targets[0].antiAliasing}");
        }

        private bool eyesOn;

        private void SetEyes(bool on)
        {
            eyesOn = on;
            foreach (var cam in eyes)
                if (cam.enabled != on) cam.enabled = on;
        }

        /// <summary>Direct mode: submit right after the eyes render.</summary>
        public void SubmitEyes() { if (eyesOn) vr.SubmitNow(nativeTargets[0], nativeTargets[1]); }

        /// <summary>Threaded mode: hand the previous frame to the render thread (empty if the eyes were off).</summary>
        public void QueuePreviousFrame() =>
            vr.QueueRenderThreadFrame(eyesOn ? nativeTargets[0] : IntPtr.Zero, eyesOn ? nativeTargets[1] : IntPtr.Zero);
    }
}
