using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace VrUiKit;

// The hotkey bar's content: named sets of buttons that press the game's keys,
// for things the controllers have no button for and for tutorials that wait for
// a key ("press W", "scroll to zoom"). The game defines the sets and says how to
// press a key in the way it reads input (UseKeys); the core draws the bar
// (VrHotbar), places it and times the presses.
//
// A set is groups of buttons on a grid, so it can be laid out to make sense:
// pan as a cross, rotate left / right side by side, zoom in above zoom out.
// Positions are in button cells (a cell is a button plus its gap); a group's
// label sits above its top row, so leave a row free between stacked groups.
sealed class HotkeySet
{
    public string Name;
    public readonly List<HotkeyGroup> Groups = new();

    // A group of buttons whose top-left cell is (col, row) in the set.
    public HotkeyGroup Group(string label, float col, float row)
    {
        var g = new HotkeyGroup { Set = this, Label = label, Col = col, Row = row };
        Groups.Add(g);
        VrUiMenu.MarkDirty();
        return g;
    }
}

sealed class HotkeyGroup
{
    public HotkeySet Set;
    public string Label;
    public float Col, Row;
    public readonly List<HotkeyButton> Buttons = new();

    // A button at (col, row) inside the group that presses key (see VrHotkeys.UseKeys).
    public HotkeyGroup Button(string label, string key, float col, float row)
        => Add(new HotkeyButton { Label = label, Key = key, Col = col, Row = row });

    // The same, drawn as a picture: art (the game's own sprite, see HotkeyButton.Art) if it
    // resolves, else icon (a VrIcons name, "rotate-ccw"), else the label as text. The
    // label still names the button (the log, VrUi.HotbarPointOf).
    public HotkeyGroup Button(string label, string key, float col, float row, string icon = null, Func<Sprite> art = null)
        => Add(new HotkeyButton { Label = label, Key = key, Col = col, Row = row, Icon = icon, Art = art });

    // A button tied to one of the game's actions rather than a key: at each press it
    // presses the key the game binds to action now (VrHotkeys.UseActions), so the bar
    // follows the player's rebinds; key is pressed when that can't be found.
    public HotkeyGroup Action(string label, string action, string key, float col, float row, string icon = null, Func<Sprite> art = null)
        => Add(new HotkeyButton { Label = label, Action = action, Key = key, Col = col, Row = row, Icon = icon, Art = art });

    // For the button added last: draw it with this VrIcons icon / the game's own sprite.
    public HotkeyGroup Icon(string icon) { Last().Icon = icon; return this; }
    public HotkeyGroup Art(Func<Sprite> art) { Last().Art = art; return this; }
    // For the button added last: what it shows now, as text (the skill in a slot, say):
    // drawn when there's no picture and written in the log. Null or "": the label.
    public HotkeyGroup LiveLabel(Func<string> label) { Last().LiveLabel = label; return this; }

    HotkeyGroup Add(HotkeyButton b)
    {
        b.Group = this;
        Buttons.Add(b);
        VrHotbar.MarkDirty();
        return this;
    }

    HotkeyButton Last() => Buttons.Count > 0 ? Buttons[Buttons.Count - 1]
        : throw new InvalidOperationException($"hotkey group '{Label}': Icon/Art before any Button");
}

sealed class HotkeyButton
{
    public HotkeyGroup Group;
    // Label: the button's name, drawn as text when there's no picture. Key: what UseKeys
    // presses (the fallback when Action is set). Action: the game's action id, or null.
    public string Label, Key, Action;
    public float Col, Row;
    // A VrIcons name, drawn cream; or null.
    public string Icon;
    // The game's own sprite (one it has loaded: found in its UI at run time, never copied
    // out or shipped), drawn in its own colours. Live: the bar asks a few times a second
    // (and at once after VrHotkeys.Refresh) and redraws the button when it gives another
    // sprite, so a slot can show whichever skill is in it now. Keep it cheap: return the
    // sprite of an Image found once, not a search. Null: no art (yet): the icon or label.
    public Func<Sprite> Art
    {
        get => art.Find;
        set => art = new LiveRef<Sprite>(value, s => s != null, ReferenceEquals, "game art");
    }
    LiveRef<Sprite> art = new(null, s => s != null, ReferenceEquals, "game art");

    // What the button shows as text now (HotkeyGroup.LiveLabel); null: Label.
    public Func<string> LiveLabel
    {
        get => liveLabel.Find;
        set => liveLabel = new LiveRef<string>(value, s => !string.IsNullOrEmpty(s), string.Equals, "live label");
    }
    LiveRef<string> liveLabel = new(null, s => !string.IsNullOrEmpty(s), string.Equals, "live label");

    // The game's sprite as last looked at (Look), or null.
    public Sprite GameSprite() => art.Value;
    // The text it shows: the live label as last looked at, else Label.
    public string Shown => liveLabel.Value ?? Label;
    // For the log: Label, with what it shows now when that differs ("Skill 1 (Fireball)").
    public string Describe => liveLabel.Value is string s && s != Label ? $"{Label} ({s})" : Label;

    // Asks the game for its sprite and text now; true if either changed since the last look.
    // No allocation when they come back the same.
    public bool Look() => art.Look() | liveLabel.Look();
    public bool IsLive => art.Find != null || liveLabel.Find != null;

    // The key pressed by the press under way (or being let go), so the same key comes up.
    internal string PressedKey;

    // A wheel key turns one notch per press (and repeats while held) instead of being held down.
    public bool Wheel => VrHotkeys.IsWheel(Key);
}

// A value the game gives that can change: Look() asks find() and says whether the answer
// differs (by same()) from the last one. Answers alive() turns down (null, a destroyed
// Unity object) count as none. find() throwing keeps the last answer, logged once (Failed).
sealed class LiveRef<T> where T : class
{
    public readonly Func<T> Find;
    readonly Func<T, bool> alive;
    readonly Func<T, T, bool> same;
    readonly string what;
    public T Value { get; private set; }
    public Exception Failed { get; private set; }
    public int Asked { get; private set; }

    public LiveRef(Func<T> find, Func<T, bool> alive, Func<T, T, bool> same, string what)
    {
        Find = find; this.alive = alive; this.same = same; this.what = what;
    }

    public bool Look()
    {
        if (Find == null) return false;
        Asked++;
        T v;
        try { v = Find(); }
        catch (Exception e)
        {
            if (Failed == null) { Failed = e; VrUi.Host?.Log($"[hotbar] a button's {what} threw ({e.GetType().Name}: {e.Message}); it keeps what it showed"); }
            v = Value;
        }
        if (v != null && !alive(v)) v = null;
        if (v == null ? Value == null : Value != null && same(v, Value)) return false;
        Value = v;
        return true;
    }
}

static class VrHotkeys
{
    // Pressed for at least this long, so a game that polls now and then sees it.
    const float MinHold = 0.2f;
    // While the trigger stays down on a wheel button, another notch this often.
    const float WheelRepeat = 0.15f;

    static readonly List<HotkeySet> sets = new();
    static Action<string, bool> setKey;
    static ConfigEntry<bool> labels, oldShown;
    static ConfigEntry<string> oldCurrent;
    static ConfigFile config;
    static readonly Dictionary<string, ConfigEntry<bool>> on = new();

    public static IReadOnlyList<HotkeySet> Sets => sets;
    public static bool Any => sets.Count > 0 && setKey != null;
    internal static bool CanPress => setKey != null;
    // Each set is its own bar, switched on and off by itself (UI tab, "Hotkey bars"),
    // so several can show at once, each placed on its own (user's decision, 2026-11-06).
    public static bool IsOn(HotkeySet set) => set != null && On(set)?.Value == true;
    public static void SetOn(HotkeySet set, bool value) { var e = On(set); if (e != null) e.Value = value; }
    public static bool Shown => Any && sets.Any(IsOn);
    public static bool Labels => labels?.Value ?? true;
    // The first set showing (for older callers that knew only one bar).
    public static HotkeySet Current => sets.FirstOrDefault(IsOn) ?? sets.FirstOrDefault();

    // A new set, or the one already called that.
    public static HotkeySet Set(string name)
    {
        var s = sets.FirstOrDefault(x => x.Name == name);
        if (s != null) return s;
        sets.Add(s = new HotkeySet { Name = name });
        AddRow(s);
        VrUiMenu.MarkDirty();
        return s;
    }

    // Whether a set's bar shows ([VrUi] HotkeysOn <set>). The first time, the set the
    // old single bar showed is on (HotkeysSet, or the first set, if HotkeysShown was on).
    static ConfigEntry<bool> On(HotkeySet set)
    {
        if (config == null || set == null) return null;
        if (!on.TryGetValue(set.Name, out var e))
        {
            bool wasShown = oldShown?.Value ?? true;
            string wasSet = string.IsNullOrEmpty(oldCurrent?.Value) ? sets.FirstOrDefault()?.Name : oldCurrent.Value;
            on[set.Name] = e = config.Bind("VrUi", "HotkeysOn " + set.Name, wasShown && set.Name == wasSet,
                $"Show the '{set.Name}' hotkey bar (buttons that press the game's keys).");
            e.SettingChanged += Redraw;
        }
        return e;
    }

    static readonly HashSet<string> rowsAdded = new();
    static void AddRow(HotkeySet s)
    {
        if (config == null || !rowsAdded.Add(s.Name)) return;
        VrSettings.Add(new ToggleSetting("Hotkey bars", s.Name, On(s)) { When = () => Any });
    }

    // How to press a key: setKey(key, true) every frame while it is down and
    // setKey(key, false) once when it comes up; a wheel key ("ScrollUp" /
    // "ScrollDown") gets one setKey(key, true) per notch. Key names are the
    // game's choice: whatever its key sender understands ("W", "Q", "Space"...).
    public static void UseKeys(Action<string, bool> sender)
    {
        setKey = sender;
        VrUiMenu.MarkDirty();
    }

    // How to find the key the game binds to an action id now (a key name UseKeys
    // understands), for buttons made with HotkeyGroup.Action. Asked at each press, so a
    // rebind made mid-session is followed. Null or "" (or a throw, logged once): the
    // button's own Key is pressed.
    public static void UseActions(Func<string, string> keyFor) { keyForAction = keyFor; actionFailed = false; }

    static Func<string, string> keyForAction;
    static bool actionFailed;

    // The key a press of b presses now: its action's key if the game gives one, else its Key.
    public static string KeyFor(HotkeyButton b)
    {
        if (b == null) return null;
        if (!string.IsNullOrEmpty(b.Action) && keyForAction != null)
        {
            try
            {
                var k = keyForAction(b.Action);
                if (!string.IsNullOrEmpty(k)) return k;
            }
            catch (Exception e)
            {
                if (!actionFailed) VrUi.Host?.Log($"[hotbar] finding the key for action '{b.Action}' threw ({e.GetType().Name}: {e.Message}); pressing the button's own keys");
                actionFailed = true;
            }
        }
        return b.Key;
    }

    internal static bool IsWheel(string key) => key != null && key.StartsWith("Scroll");

    // Look at every button's game art and live label now (they're looked at a few times a
    // second anyway): call it right after the game changes one, a skill swapped into a slot.
    public static void Refresh() => VrHotbar.LookNow();

    // Called by VrUi.Init: the bar's settings ([VrUi] in the game's config) and its rows on the UI tab.
    internal static void Init(ConfigFile file)
    {
        config = file;
        labels = file.Bind("VrUi", "HotkeysLabels", true, "Show the group names on the hotkey bars.");
        // The single bar's old settings, read to decide which sets start on.
        oldShown = file.Bind("VrUi", "HotkeysShown", true, "Old (one bar at a time): replaced by HotkeysOn <set>.");
        oldCurrent = file.Bind("VrUi", "HotkeysSet", "", "Old (one bar at a time): replaced by HotkeysOn <set>.");
        foreach (var s in sets) AddRow(s); // sets made before Init
        VrSettings.Add(new ToggleSetting("Hotkey bars", "Group names", labels) { When = () => Any });
        VrSettings.PutOnTab("Hotkey bars", "UI");
        labels.SettingChanged += Redraw;
    }

    static void Redraw(object sender, EventArgs e) => VrHotbar.MarkDirty();

    // Where a set's bar was left, kept in the config: "free" or "wrist", then a pose.
    internal static string LoadPlacement(HotkeySet set) => Placement(set)?.Value ?? "";
    internal static void SavePlacement(HotkeySet set, string text) { var e = Placement(set); if (e != null) e.Value = text; }

    static readonly Dictionary<string, ConfigEntry<string>> placements = new();
    static ConfigEntry<string> Placement(HotkeySet set)
    {
        if (config == null || set == null) return null;
        if (!placements.TryGetValue(set.Name, out var e))
            placements[set.Name] = e = config.Bind("VrUi", "HotkeysPlace " + set.Name, "", "Where this hotkey set's bar is (moved in VR while the VR menu is open).");
        return e;
    }

    // ---- pressing ----

    static HotkeyButton pressed;
    static string pressedKey; // resolved when pressed (KeyFor), held and let go as that key
    static float pressedAt, nextNotch;
    static readonly Dictionary<string, float> releaseAt = new();
    static readonly List<string> done = new();

    public static bool IsDown(HotkeyButton b) => b == pressed || (b?.PressedKey != null && releaseAt.ContainsKey(b.PressedKey));

    internal static void Press(HotkeyButton b) => Press(b, Time.unscaledTime);

    // (now: Time.unscaledTime; CoreTests pass their own.)
    internal static void Press(HotkeyButton b, float now)
    {
        if (setKey == null || b == null) return;
        var key = KeyFor(b);
        if (string.IsNullOrEmpty(key))
        {
            VrUi.Host?.Log($"[hotbar] '{b.Label}' has no key: action '{b.Action}' isn't bound and the button names no fallback");
            return;
        }
        pressed = b;
        pressedKey = b.PressedKey = key;
        pressedAt = now;
        if (IsWheel(key)) { setKey(key, true); nextNotch = now + WheelRepeat * 2; }
        VrHotbar.MarkDirty();
    }

    // Every frame: the button under the laser stays down while the trigger is held on it.
    internal static void Update(HotkeyButton hovered) => Update(hovered, VrInput.PointerTriggerHeld, Time.unscaledTime);

    internal static void Update(HotkeyButton hovered, bool triggerHeld, float now)
    {
        if (setKey == null) return;
        if (pressed != null)
        {
            bool wheel = IsWheel(pressedKey);
            if (triggerHeld && hovered == pressed)
            {
                if (!wheel) setKey(pressedKey, true);
                else if (now >= nextNotch) { setKey(pressedKey, true); nextNotch = now + WheelRepeat; }
            }
            else
            {
                if (!wheel) releaseAt[pressedKey] = Math.Max(now, pressedAt + MinHold);
                pressed = null;
                pressedKey = null;
                VrHotbar.MarkDirty();
            }
        }
        if (releaseAt.Count == 0) return;
        done.Clear();
        foreach (var kv in releaseAt)
        {
            if (now < kv.Value) setKey(kv.Key, true);
            else { setKey(kv.Key, false); done.Add(kv.Key); }
        }
        foreach (var k in done) releaseAt.Remove(k);
        if (done.Count > 0) VrHotbar.MarkDirty();
    }

    // For CoreTests: forget sets, senders and presses.
    internal static void ResetForTests()
    {
        sets.Clear();
        setKey = null;
        keyForAction = null;
        actionFailed = false;
        pressed = null;
        pressedKey = null;
        releaseAt.Clear();
    }

    internal static string Format(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
}
