#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace VrUiKit;

// Which hand, by job rather than side: the pointer hand holds the laser, the
// off hand is the other one. Left-handed play swaps the sides, so every
// binding mirrors with it.
enum HandRole { Pointer, Off }

enum VrControl { Trigger, Grip, Primary, Secondary, StickClick, StickUp, StickDown, StickLeft, StickRight }

// One input: a control on a hand.
readonly struct VrBinding : IEquatable<VrBinding>
{
    public readonly HandRole Role;
    public readonly VrControl Control;

    public VrBinding(HandRole role, VrControl control) { Role = role; Control = control; }

    public override string ToString() => $"{Role}.{Control}";

    public static bool TryParse(string text, out VrBinding binding)
    {
        binding = default;
        var parts = text.Trim().Split('.');
        if (parts.Length != 2 || !Enum.TryParse<HandRole>(parts[0], out var role) || !Enum.TryParse<VrControl>(parts[1], out var control)) return false;
        binding = new VrBinding(role, control);
        return true;
    }

    // Short label for the menu, by side (Quest Touch names: A / B on the right, X / Y on the left).
    public string Label(bool leftHanded)
    {
        bool left = (Role == HandRole.Pointer) == leftHanded;
        string name = Control switch
        {
            VrControl.Trigger => "trigger",
            VrControl.Grip => "grip",
            VrControl.Primary => left ? "X" : "A",
            VrControl.Secondary => left ? "Y" : "B",
            VrControl.StickClick => "stick click",
            VrControl.StickUp => "stick up",
            VrControl.StickDown => "stick down",
            VrControl.StickLeft => "stick left",
            VrControl.StickRight => "stick right",
            _ => Control.ToString(),
        };
        return (left ? "L " : "R ") + name;
    }

    public bool Equals(VrBinding other) => Role == other.Role && Control == other.Control;
    public override bool Equals(object obj) => obj is VrBinding b && Equals(b);
    public override int GetHashCode() => (int)Role * 32 + (int)Control;
}

// A thing the player does (Pause, Rotate item, Move table...). Each game
// declares its own; the player can rebind them. Actions in the same group are
// used at the same time, so sharing a control there is a conflict; an action
// with no group is active everywhere and conflicts with any group.
sealed class VrAction
{
    public string Id, Name, Section, Group;
    public VrBinding[] Defaults = Array.Empty<VrBinding>();
    public List<VrBinding> Bindings = new();
}

// What the game's VR mod reads from one controller each frame.
struct ControllerState
{
    public bool Valid;
    public bool TriggerHeld, GripHeld, PrimaryHeld, SecondaryHeld, StickClickHeld;
    // One-frame presses the host already detected (scripted input may only pulse these).
    public bool PrimaryPressed, SecondaryPressed, StickClickPressed, FlickLeft, FlickRight;
    public float Trigger, Grip, StickX, StickY;
}

// Controller input through rebindable actions. The game calls Update once a
// frame with both controllers, then asks Pressed / Held / Released / Value.
//
// The chord (both grips and both triggers) always opens the VR menu. A trigger
// pulled while both grips are held does nothing else, and once the chord fires
// every action stays quiet until all four are let go.
static class VrInput
{
    const float StickHeld = 0.5f, StickPress = 0.7f, StickRearm = 0.3f;
    const int ControlCount = 9;
    const float CaptureTimeout = 8f;

    static readonly List<VrAction> actions = new();
    static readonly Dictionary<string, VrAction> byId = new();
    static readonly bool[,] held = new bool[2, ControlCount], pressed = new bool[2, ControlCount], released = new bool[2, ControlCount];
    static readonly float[,] value = new float[2, ControlCount];
    static readonly bool[,] stickArmed = { { true, true, true, true }, { true, true, true, true } };
    static readonly bool[] triggerBlocked = new bool[2];
    static bool chordHeld, consumed, leftHanded;

    static ConfigEntry<string> presetEntry;
    static string dir;
    static IVrUiHost host;

    public static IReadOnlyList<VrAction> Actions => actions;
    // The laser hand's trigger, whatever it's bound to (holding a hotkey bar button down).
    public static bool PointerTriggerHeld => held[0, (int)VrControl.Trigger];
    // The frame it went down (picking a window's pin while arranging).
    internal static bool PointerTriggerPressed => !consumed && pressed[0, (int)VrControl.Trigger];
    // The grips and the laser hand's stick, whatever they're bound to: they carry
    // the VR menu and the hotkey bar (VrUi.CarryPanels), as they carry Free-mode windows.
    internal static bool GripPressed(bool offHand) => !consumed && pressed[offHand ? 1 : 0, (int)VrControl.Grip];
    internal static bool GripHeld(bool offHand) => held[offHand ? 1 : 0, (int)VrControl.Grip];
    internal static float PointerStickX => value[0, (int)VrControl.StickRight] - value[0, (int)VrControl.StickLeft];
    internal static float PointerStickY => value[0, (int)VrControl.StickUp] - value[0, (int)VrControl.StickDown];

    // A hand's own stick (x right, y up), whatever "swap sticks" does to the
    // actions: what carries a panel or a window held in that hand.
    static readonly float[] rawX = new float[2], rawY = new float[2];
    internal static (float x, float y) HandStick(bool offHand) => (Dead(rawX[offHand ? 1 : 0]), Dead(rawY[offHand ? 1 : 0]));
    static float Dead(float v) => Math.Abs(v) < 0.2f ? 0 : v;
    // The action slot a hand's stick feeds (swapped sticks feed the other hand's).
    static int StickSlot(bool offHand) => (offHand ? 1 : 0) ^ (SwapSticks ? 1 : 0);

    // While a panel is carried, the grip holding it and that hand's stick belong
    // to the carry: the game's actions on them don't fire.
    static int claimedHand = -1, claimedStick = -1;
    internal static void Claim(bool offHand, bool stick) { claimedHand = offHand ? 1 : 0; claimedStick = stick ? StickSlot(offHand) : -1; unclaim = false; }
    // Kept through the frame the grip comes up, so the game sees no release either.
    internal static void Unclaim() => unclaim = true;
    static bool unclaim;
    // Only a hand's stick (a window held by the mod's own grip action: its grip must stay the game's).
    internal static void ClaimStick(bool offHand) { stickOnly = StickSlot(offHand); unclaimStick = false; }
    internal static void UnclaimStick() => unclaimStick = true;
    static int stickOnly = -1;
    static bool unclaimStick;
    static bool Claimed(VrBinding b) =>
        ((int)b.Role == claimedHand && b.Control == VrControl.Grip) ||
        (b.Control >= VrControl.StickUp && ((int)b.Role == claimedStick || (int)b.Role == stickOnly));
    public static bool LeftHanded => leftHanded;
    // Raised the frame the chord fires (e.g. to undo a table drag the grips started).
    public static event Action ChordFired;

    public static void Init(ConfigFile config, string folder)
    {
        presetEntry = config.Bind("VrUi", "ControlPreset", "Default", "Controller preset the current bindings came from (files in BepInEx/config/" + folder + ").");
        dir = Path.Combine(Path.GetDirectoryName(config.ConfigFilePath), folder);
    }

    // Declare an action. Bindings are strings like "Pointer.Trigger" or "Off.StickUp".
    public static VrAction Register(string id, string name, string section, string group, params string[] defaults)
    {
        var a = new VrAction
        {
            Id = id, Name = name, Section = section, Group = group,
            Defaults = defaults.Select(d => VrBinding.TryParse(d, out var b) ? b : throw new ArgumentException($"bad binding '{d}' for {id}")).ToArray(),
        };
        a.Bindings = a.Defaults.ToList();
        actions.Add(a);
        byId[id] = a;
        return a;
    }

    // Call after registering every action: saved bindings replace the defaults.
    public static void Load()
    {
        try { if (dir != null && File.Exists(WorkingPath)) Apply(File.ReadAllText(WorkingPath)); }
        catch (Exception) { /* keep defaults */ }
    }

    // ---- per-frame state ----

    // The two thumbsticks trade places (a game mode where the off hand's stick
    // should do what the pointer hand's normally does, and the other way round).
    // Buttons and stick clicks stay where they are.
    public static bool SwapSticks;

    public static void Update(IVrUiHost h, ControllerState left, ControllerState right)
    {
        host = h;
        leftHanded = h.LeftHanded;
        if (unclaim) { claimedHand = -1; claimedStick = -1; unclaim = false; }
        if (unclaimStick) { stickOnly = -1; unclaimStick = false; }
        var pointer = leftHanded ? left : right;
        var off = leftHanded ? right : left;
        rawX[0] = pointer.Valid ? pointer.StickX : 0; rawY[0] = pointer.Valid ? pointer.StickY : 0;
        rawX[1] = off.Valid ? off.StickX : 0; rawY[1] = off.Valid ? off.StickY : 0;
        if (SwapSticks)
        {
            (pointer.StickX, off.StickX) = (off.StickX, pointer.StickX);
            (pointer.StickY, off.StickY) = (off.StickY, pointer.StickY);
            (pointer.FlickLeft, off.FlickLeft) = (off.FlickLeft, pointer.FlickLeft);
            (pointer.FlickRight, off.FlickRight) = (off.FlickRight, pointer.FlickRight);
        }
        bool bothGrips = pointer.Valid && off.Valid && pointer.GripHeld && off.GripHeld;
        Read(0, pointer, bothGrips);
        Read(1, off, bothGrips);

        bool chord = bothGrips && pointer.TriggerHeld && off.TriggerHeld;
        if (chord && !chordHeld)
        {
            consumed = true;
            ChordFired?.Invoke();
            // While a button is being rebound the chord just cancels that.
            if (capturing) { capturing = false; VrUiMenu.MarkDirty(); }
            else VrUi.ToggleMenu();
            h.Log("[input] chord: VR menu");
        }
        chordHeld = chord;
        if (consumed && !pointer.TriggerHeld && !off.TriggerHeld && !pointer.GripHeld && !off.GripHeld) consumed = false;

        UpdateCapture();
        if (!capturing) VrUi.CarryPanels(h);
    }

    static void Read(int r, ControllerState s, bool bothGrips)
    {
        var now = new bool[ControlCount];
        var val = new float[ControlCount];
        var pulse = new bool[ControlCount];
        if (s.Valid)
        {
            if (bothGrips && s.TriggerHeld) triggerBlocked[r] = true;
            if (!s.TriggerHeld) triggerBlocked[r] = false;
            now[(int)VrControl.Trigger] = s.TriggerHeld && !triggerBlocked[r];
            now[(int)VrControl.Grip] = s.GripHeld;
            now[(int)VrControl.Primary] = s.PrimaryHeld || s.PrimaryPressed;
            now[(int)VrControl.Secondary] = s.SecondaryHeld || s.SecondaryPressed;
            now[(int)VrControl.StickClick] = s.StickClickHeld || s.StickClickPressed;
            val[(int)VrControl.Trigger] = now[(int)VrControl.Trigger] ? Math.Max(s.Trigger, 0.01f) : 0;
            val[(int)VrControl.Grip] = s.GripHeld ? Math.Max(s.Grip, 0.01f) : 0;
            for (int i = (int)VrControl.Primary; i <= (int)VrControl.StickClick; i++) val[i] = now[i] ? 1 : 0;
            pulse[(int)VrControl.Primary] = s.PrimaryPressed;
            pulse[(int)VrControl.Secondary] = s.SecondaryPressed;
            pulse[(int)VrControl.StickClick] = s.StickClickPressed;

            float[] dirs = { Math.Max(0, s.StickY), Math.Max(0, -s.StickY), Math.Max(0, -s.StickX), Math.Max(0, s.StickX) };
            for (int d = 0; d < 4; d++)
            {
                int i = (int)VrControl.StickUp + d;
                val[i] = dirs[d];
                now[i] = dirs[d] > StickHeld;
                // A push past StickPress is one press, re-armed near the centre.
                if (stickArmed[r, d] && dirs[d] > StickPress) { stickArmed[r, d] = false; pulse[i] = true; }
                else if (!stickArmed[r, d] && dirs[d] < StickRearm) stickArmed[r, d] = true;
            }
            if (s.FlickLeft) pulse[(int)VrControl.StickLeft] = true;
            if (s.FlickRight) pulse[(int)VrControl.StickRight] = true;
        }
        for (int i = 0; i < ControlCount; i++)
        {
            bool stick = i >= (int)VrControl.StickUp;
            pressed[r, i] = pulse[i] || (!stick && now[i] && !held[r, i]);
            released[r, i] = !now[i] && held[r, i];
            held[r, i] = now[i];
            value[r, i] = val[i];
        }
    }

    static bool Quiet => consumed || capturing;

    static bool Any(string id, bool[,] state)
    {
        if (Quiet || !byId.TryGetValue(id, out var a)) return false;
        foreach (var b in a.Bindings)
            if (state[(int)b.Role, (int)b.Control] && !Claimed(b)) return true;
        return false;
    }

    public static bool Pressed(string id) => Any(id, pressed);
    public static bool Held(string id) => Any(id, held);
    // Let go this frame (and nothing else bound to it is still held).
    public static bool Released(string id) => !Held(id) && Any(id, released);

    // 0..1: how far the bound control is pushed (sticks, analog triggers), 1 for buttons.
    public static float Value(string id)
    {
        if (Quiet || !byId.TryGetValue(id, out var a)) return 0;
        float v = 0;
        foreach (var b in a.Bindings) if (!Claimed(b)) v = Math.Max(v, value[(int)b.Role, (int)b.Control]);
        return v;
    }

    // Held on one particular controller (by side), e.g. for two-handed grabs.
    public static bool HeldOnSide(string id, bool leftSide)
    {
        if (Quiet || !byId.TryGetValue(id, out var a)) return false;
        var role = leftSide == leftHanded ? HandRole.Pointer : HandRole.Off;
        foreach (var b in a.Bindings)
            if (b.Role == role && held[(int)role, (int)b.Control] && !Claimed(b)) return true;
        return false;
    }

    // ---- conflicts ----

    public static bool Conflicts(VrAction a, VrBinding b) =>
        actions.Any(o => o != a && o.Bindings.Contains(b) && (o.Group == null || a.Group == null || o.Group == a.Group));

    public static string BindingText(VrAction a) =>
        a.Bindings.Count == 0 ? "(none)" : string.Join(", ", a.Bindings.Select(b => b.Label(leftHanded)));

    // ---- capturing a new binding from the controllers ----

    static bool capturing, captureArmed, captureAdd;
    static VrAction captureAction;
    static VrBinding? captureFirst;
    static float captureUntil;

    public static VrAction Capturing => capturing ? captureAction : null;

    // Waits for everything to be let go, then takes the next control pressed as
    // the action's binding: replacing its bindings, or added to them.
    public static void BeginCapture(VrAction a, bool add)
    {
        capturing = true;
        captureAction = a;
        captureAdd = add;
        captureArmed = false;
        captureFirst = null;
        captureUntil = Time.unscaledTime + CaptureTimeout;
    }

    public static void CancelCapture() => capturing = false;

    static void UpdateCapture()
    {
        if (!capturing) return;
        bool anyHeld = false;
        for (int r = 0; r < 2; r++)
            for (int i = 0; i < ControlCount; i++)
                anyHeld |= held[r, i];
        if (Time.unscaledTime > captureUntil) { capturing = false; VrUiMenu.MarkDirty(); return; }
        if (!captureArmed)
        {
            captureArmed = !anyHeld;
            return;
        }
        if (captureFirst == null)
            for (int r = 0; r < 2 && captureFirst == null; r++)
                for (int i = 0; i < ControlCount; i++)
                    if (pressed[r, i]) { captureFirst = new VrBinding((HandRole)r, (VrControl)i); break; }
        // Decide once everything is let go.
        if (captureFirst == null || anyHeld) return;
        var binding = captureFirst.Value;
        if (!captureAdd) captureAction.Bindings.Clear();
        if (!captureAction.Bindings.Contains(binding)) captureAction.Bindings.Add(binding);
        capturing = false;
        SaveWorking();
        VrUiMenu.MarkDirty();
        host?.Log($"[input] '{captureAction.Id}' bound to {string.Join(", ", captureAction.Bindings)}");
    }

    // ---- saving: the working bindings, and named presets ----

    static string WorkingPath => Path.Combine(dir ?? "", "controls.txt");

    static string Format(string name) =>
        $"# VR controller bindings\nname = {name}\n" + string.Join("", actions.Select(a => $"{a.Id} = {string.Join(", ", a.Bindings)}\n"));

    // Reads "id = binding, binding" lines into the actions (unknown ids are ignored).
    static void Apply(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq < 0 || !byId.TryGetValue(line[..eq].Trim(), out var a)) continue;
            a.Bindings = line[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => VrBinding.TryParse(s, out var b) ? (VrBinding?)b : null)
                .Where(b => b != null).Select(b => b.Value).ToList();
        }
    }

    static void SaveWorking()
    {
        if (dir == null) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(WorkingPath, Format(Preset));
    }

    // Built-in "Default" (every action's defaults), then *.controls.txt files.
    public static List<string> Presets()
    {
        var list = new List<string> { "Default" };
        if (dir != null && Directory.Exists(dir))
            list.AddRange(Directory.GetFiles(dir, "*.controls.txt").OrderBy(f => f)
                .Select(f => Path.GetFileName(f).Replace(".controls.txt", "")).Where(n => n != "Default"));
        return list;
    }

    public static string Preset => presetEntry?.Value ?? "Default";

    public static void StepPreset(int direction)
    {
        var list = Presets();
        int i = Math.Max(0, list.IndexOf(Preset));
        presetEntry.Value = list[(i + direction + list.Count) % list.Count];
    }

    // Replace the current bindings with the selected preset's.
    public static void UsePreset()
    {
        foreach (var a in actions) a.Bindings = a.Defaults.ToList();
        var path = Path.Combine(dir ?? "", Preset + ".controls.txt");
        if (Preset != "Default" && File.Exists(path)) Apply(File.ReadAllText(path));
        SaveWorking();
        host?.Log($"[input] using preset '{Preset}'");
    }

    // Save the current bindings as a new preset file (and select it).
    public static string SaveAsNewPreset()
    {
        if (dir == null) return null;
        int n = 1;
        var existing = Presets();
        while (existing.Contains($"Custom {n}")) n++;
        var name = $"Custom {n}";
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".controls.txt"), Format(name));
        presetEntry.Value = name;
        SaveWorking();
        host?.Log($"[input] saved preset '{name}'");
        return name;
    }

    // Deletes the selected preset file (the built-in Default stays).
    public static void DeletePreset()
    {
        var path = Path.Combine(dir ?? "", Preset + ".controls.txt");
        if (Preset == "Default" || !File.Exists(path)) return;
        File.Delete(path);
        host?.Log($"[input] deleted preset '{Preset}'");
        presetEntry.Value = "Default";
    }
}
