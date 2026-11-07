#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using NVec = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace VrUiKit;

// The pane that shows the game's whole UI (Panel mode), moved by hand: while
// the VR menu is open it is carried like the menu (VrUi.CarryPanels), and it
// stays where it was left, per key ("Panel"; the mod passes null where the pane
// must stay put, e.g. over the cinema screen). The size is kept as a factor on
// the mod's own width, so its "Panel width" setting still works.
static class VrPane
{
    static ConfigFile config;
    static readonly Dictionary<string, ConfigEntry<string>> entries = new();

    static string key;           // what's shown this frame, null: fixed
    static int shownFrame = -1;
    static Pose pose;            // tracking space, centre
    static float width, aspect = 9f / 16;
    static float baseWidth = 1;
    static bool carried;

    // Saved for the current key: pose and size factor.
    static Pose? savedPose;
    static float savedScale = 1;
    static string loadedKey;

    internal static void Init(ConfigFile file)
    {
        config = file;
        VrSettings.Add(new ActionSetting("UI", "Game UI panel", () => "Reset place", Reset)
        {
            When = () => VrUi.Mode == VrUi.UiMode.Panel,
        });
    }

    // Each frame the pane is drawn: its default placement (tracking space,
    // width in metres) and height / width. Returns where to draw it.
    public static (Pose pose, float width) Place(string paneKey, Pose defaultPose, float defaultWidth, float heightOverWidth, bool shown)
    {
        aspect = heightOverWidth;
        baseWidth = defaultWidth;
        if (paneKey == null)
        {
            key = null;
            return (defaultPose, defaultWidth);
        }
        if (paneKey != loadedKey) Load(paneKey);
        key = paneKey;
        if (shown) shownFrame = Time.frameCount;
        if (!carried)
        {
            // On a wrist: savedPose is relative to that hand, and the pane rides along.
            pose = savedPose is Pose s && wrist != 0 && host != null ? (wrist == 1 ? HandDrag.OffHand(host) : HandDrag.PointerHand(host)).Compose(s)
                : savedPose ?? defaultPose;
            width = defaultWidth * savedScale;
        }
        return (pose, width);
    }

    // 0: in the room; 1: on the off hand's wrist; 2: on the pointer hand's wrist.
    static int wrist;
    static bool carriedByOffHand;

    // Letting go now would put it on the other hand's wrist: any part of it within
    // VrUi.SnapReach of that wrist, the windows' and bars' rule.
    static bool WouldSnap(IVrUiHost h, bool byOffHand)
    {
        var other = byOffHand ? HandDrag.PointerHand(h) : HandDrag.OffHand(h);
        return HandDrag.DistanceTo(pose, Width, Height, VrUi.WristPoint(other)) < VrUi.SnapReach;
    }

    // ---- the outline while it can be moved (VR menu open), from VrUi.Update ----

    static UiOutline outline;

    internal static void UpdateOutline(IVrUiHost h, bool hovered)
    {
        if (h == null || !CanCarry) { outline?.Hide(); return; }
        outline ??= new UiOutline("Panel");
        var state = carried ? (WouldSnap(h, carriedByOffHand) ? UiOutline.State.Snap : UiOutline.State.Held)
            : hovered ? UiOutline.State.Hover : UiOutline.State.Idle;
        float s = h.WorldScale;
        outline.Show(h, h.TrackingToWorld(pose), Width * s, Height * s, state);
    }

    // ---- carried (VrUi.CarryPanels) ----

    // Whether a pane has ever been placed (its pose and width mean something).
    internal static bool Known => loadedKey != null;
    internal static bool CanCarry => key != null && shownFrame >= Time.frameCount - 1 && VrUiMenu.Open;
    internal static Pose CarryPose => pose;
    internal static float Width => width;
    internal static float Height => width * aspect;
    internal static float DistanceTo(NVec point) => CanCarry ? HandDrag.DistanceTo(pose, Width, Height, point) : float.MaxValue;
    internal static void Resize(float factor) => width = Math.Clamp(width * factor, baseWidth * 0.3f, baseWidth * 3f);
    internal static void BeginCarry(bool byOffHand = false) { carried = true; carriedByOffHand = byOffHand; }
    internal static void CarryTo(Pose p) => pose = p;

    // Let go: near the other hand's wrist it goes on that wrist (as the hotkey bars and windows do).
    internal static void EndCarry(IVrUiHost host, bool byOffHand)
    {
        carried = false;
        var other = byOffHand ? HandDrag.PointerHand(host) : HandDrag.OffHand(host);
        if (WouldSnap(host, byOffHand))
        {
            wrist = byOffHand ? 2 : 1;
            savedPose = other.Inverse().Compose(pose);
        }
        else
        {
            wrist = 0;
            savedPose = pose;
        }
        savedScale = baseWidth > 0 ? width / baseWidth : 1;
        Save();
        host.Log(wrist != 0
            ? $"[vrui] game UI panel put on the {(wrist == 1 ? "off" : "pointer")} hand's wrist, width {width:F2} m"
            : $"[vrui] game UI panel placed at {pose.Position}, width {width:F2} m");
    }

    // Ray (world space) against the pane, for grabbing it with the laser.
    internal static bool Raycast(IVrUiHost host, NVec origin, NVec dir, out float distance)
    {
        distance = 0;
        if (!CanCarry) return false;
        var world = host.TrackingToWorld(pose);
        float s = host.WorldScale;
        return UiGeometry.QuadHit(world, Width * s, Height * s, origin, dir, out _, out _, out distance);
    }

    static void Reset()
    {
        foreach (var e in entries.Values) e.Value = "";
        savedPose = null;
        savedScale = 1;
        wrist = 0;
        host?.Log("[vrui] game UI panel back to its default place");
    }

    static IVrUiHost host => VrUi.Host;

    // ---- kept in the config: "x y z qx qy qz qw scale wrist" (wrist: 0 room, 1 off hand, 2 pointer hand; the pose relative to that hand) ----

    static ConfigEntry<string> Entry(string k)
    {
        if (config == null) return null;
        if (!entries.TryGetValue(k, out var e))
            entries[k] = e = config.Bind("VrUi", "PanePlace " + k, "", "Where the game's UI panel is (moved in VR while the VR menu is open; empty: the default place).");
        return e;
    }

    static void Load(string k)
    {
        loadedKey = k;
        savedPose = null;
        savedScale = 1;
        wrist = 0;
        var parts = (Entry(k)?.Value ?? "").Split(' ');
        if (parts.Length != 8 && parts.Length != 9) return;
        var f = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) return;
        savedPose = new Pose(new NVec(f[0], f[1], f[2]), NQuat.Normalize(new NQuat(f[3], f[4], f[5], f[6])));
        savedScale = Math.Clamp(f[7], 0.3f, 3f);
        if (parts.Length == 9) wrist = Math.Clamp((int)f[8], 0, 2);
    }

    static void Save()
    {
        var e = Entry(key);
        if (e == null || savedPose is not Pose p) return;
        string F(float x) => x.ToString("0.####", CultureInfo.InvariantCulture);
        e.Value = $"{F(p.Position.X)} {F(p.Position.Y)} {F(p.Position.Z)} {F(p.Rotation.X)} {F(p.Rotation.Y)} {F(p.Rotation.Z)} {F(p.Rotation.W)} {F(savedScale)} {wrist}";
    }
}
