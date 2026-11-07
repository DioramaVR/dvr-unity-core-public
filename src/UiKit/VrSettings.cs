#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;

namespace VrUiKit;

// What a setting is, for the menu's layout (Menu/RowKinds picks the control from it).
enum SettingKind { Toggle, Number, Choice, Action }

// One row on the VR menu. The shared VR settings go in first; each game adds
// its own rows (in its own sections) through VrSettings.Add. The menu model
// (Menu/MenuModel) places each row on a page and picks its control from its
// kind and the metadata below; games can add metadata with the With* / As*
// setters (VrSettingMeta), e.g.
//   VrSettings.Add(new ToggleSetting("Game", "Pause in menu", e).WithHelp("...").WithIcon("pause"));
abstract class VrSetting
{
    public string Section, Label;
    // Shown only while this returns true (null: always), e.g. a row that only
    // applies in one UI mode.
    public Func<bool> When;
    public abstract string Value { get; }
    // Arrows step through values; otherwise clicking the value toggles it.
    public virtual bool HasArrows => true;
    public abstract void Step(int direction);

    // A subclass of a game's own that doesn't say: arrows mean a choice, else an action.
    public virtual SettingKind Kind => HasArrows ? SettingKind.Choice : SettingKind.Action;
    // A click on the row's main control: a toggle flips, an action runs, a choice
    // steps forward, a number steps up. (Destructive rows: the menu model asks
    // for a second click first; see MenuRow.Activate.)
    public virtual void Activate() => Step(1);

    // ---- metadata for the menu (all optional) ----
    // A Lucide icon name ("camera", "sun"); tiles and section headers show it.
    public string Icon;
    // The line under the label. Always shown, never only on hover.
    public string Help;
    // A unit shown with the value ("m" -> "0.75 m"). Number rows only.
    public string Unit;
    // Rarely changed: behind the section's "More settings" fold.
    public bool Rarely;
    // For mod makers: in a collapsed Developer section.
    public bool Developer;
    // Needs a second click ("Sure?") before it acts.
    public bool Destructive;
    // A choice that always shows as a cycler (< value >), e.g. a list of saved layouts.
    public bool Cycler;
}

static class VrSettingMeta
{
    public static T WithIcon<T>(this T s, string icon) where T : VrSetting { s.Icon = icon; return s; }
    public static T WithHelp<T>(this T s, string help) where T : VrSetting { s.Help = help; return s; }
    public static T WithUnit<T>(this T s, string unit) where T : VrSetting { s.Unit = unit; return s; }
    public static T AsRarely<T>(this T s, bool on = true) where T : VrSetting { s.Rarely = on; return s; }
    public static T AsDeveloper<T>(this T s, bool on = true) where T : VrSetting { s.Developer = on; return s; }
    public static T AsDestructive<T>(this T s, bool on = true) where T : VrSetting { s.Destructive = on; return s; }
    public static T AsCycler<T>(this T s, bool on = true) where T : VrSetting { s.Cycler = on; return s; }
    public static T ShownWhen<T>(this T s, Func<bool> when) where T : VrSetting { s.When = when; return s; }
}

// A setting the menu shows as a switch.
interface IToggleSetting
{
    bool IsOn { get; }
    void Set(bool on);
}

// A setting with a range: the menu shows a stepper.
interface INumberSetting
{
    float Min { get; }
    float Max { get; }
    float StepSize { get; }
    string Format { get; }
    float Number { get; }
    void SetNumber(float value);
}

// A setting with a list of named options.
interface IChoiceSetting
{
    IReadOnlyList<string> Options { get; }
    void Select(string option);
}

sealed class ToggleSetting : VrSetting, IToggleSetting
{
    readonly ConfigEntry<bool> entry;
    public ToggleSetting(string section, string label, ConfigEntry<bool> entry) { Section = section; Label = label; this.entry = entry; }
    public override string Value => entry.Value ? "On" : "Off";
    public override bool HasArrows => false;
    public override void Step(int direction) => entry.Value = !entry.Value;
    public override SettingKind Kind => SettingKind.Toggle;
    public bool IsOn => entry.Value;
    public void Set(bool on) => entry.Value = on;
}

// A button: clicking it runs an action; its text can change (e.g. a countdown).
sealed class ActionSetting : VrSetting
{
    readonly Func<string> text;
    readonly Action action;
    public ActionSetting(string section, string label, Func<string> text, Action action) { Section = section; Label = label; this.text = text; this.action = action; }
    public override string Value => text();
    public override bool HasArrows => false;
    public override void Step(int direction) => action();
    public override SettingKind Kind => SettingKind.Action;
}

sealed class NumberSetting : VrSetting, INumberSetting
{
    readonly ConfigEntry<float> entry;
    readonly float min, max, step;
    readonly string format;
    public NumberSetting(string section, string label, ConfigEntry<float> entry, float min, float max, float step, string format = "0.##")
    {
        Section = section; Label = label; this.entry = entry; this.min = min; this.max = max; this.step = step; this.format = format;
    }
    public override string Value => entry.Value.ToString(format, CultureInfo.InvariantCulture);
    public override void Step(int direction)
    {
        // Snap to the step grid so repeated presses don't drift.
        float v = MathF.Round((entry.Value + direction * step) / step) * step;
        entry.Value = Math.Clamp(v, min, max);
    }
    public override SettingKind Kind => SettingKind.Number;
    public float Min => min;
    public float Max => max;
    public float StepSize => step;
    public string Format => format;
    public float Number => entry.Value;
    public void SetNumber(float value) => entry.Value = Math.Clamp(value, min, max);
}

sealed class IntSetting : VrSetting, INumberSetting
{
    readonly ConfigEntry<int> entry;
    readonly int min, max;
    public IntSetting(string section, string label, ConfigEntry<int> entry, int min, int max) { Section = section; Label = label; this.entry = entry; this.min = min; this.max = max; }
    public override string Value => entry.Value.ToString(CultureInfo.InvariantCulture);
    public override void Step(int direction) => entry.Value = Math.Clamp(entry.Value + direction, min, max);
    public override SettingKind Kind => SettingKind.Number;
    public float Min => min;
    public float Max => max;
    public float StepSize => 1;
    public string Format => "0";
    public float Number => entry.Value;
    public void SetNumber(float value) => entry.Value = Math.Clamp((int)MathF.Round(value), min, max);
}

// Any list of named options, read and written through delegates (enums, layouts, ...).
sealed class ChoiceSetting : VrSetting, IChoiceSetting
{
    readonly Func<IReadOnlyList<string>> options;
    readonly Func<string> get;
    readonly Action<string> set;
    public ChoiceSetting(string section, string label, Func<IReadOnlyList<string>> options, Func<string> get, Action<string> set)
    {
        Section = section; Label = label; this.options = options; this.get = get; this.set = set;
    }
    public override string Value => get();
    public override void Step(int direction)
    {
        var list = options();
        if (list.Count == 0) return;
        int i = 0;
        for (int k = 0; k < list.Count; k++) if (list[k] == get()) i = k;
        set(list[(i + direction + list.Count) % list.Count]);
    }
    public override SettingKind Kind => SettingKind.Choice;
    public IReadOnlyList<string> Options => options();
    public void Select(string option) { if (option != null && option != get()) set(option); }
}

static class VrSettings
{
    public static readonly List<VrSetting> All = new();
    public static void Add(VrSetting setting) => All.Add(setting);

    // Which menu tab a section's rows go on: Mode, UI, Controls, Record, Graphics
    // or Game. Without PutOnTab, the shared section names pick their tab and
    // anything else goes on Game. The old tab names still work: View is Mode,
    // Settings is Game. The new menu's pages (Menu/MenuPlacement) follow from
    // the tab: Mode -> View, UI -> Game UI, Record -> Capture.
    static readonly Dictionary<string, string> tabs = new();
    public static void PutOnTab(string section, string tab) => tabs[section] = tab;
    public static string TabOf(VrSetting s)
    {
        if (tabs.TryGetValue(s.Section, out var t)) return t switch { "View" => "Mode", "Settings" => "Game", _ => t };
        return s.Section switch
        {
            "UI" => "UI",
            "Controls" => "Controls",
            "Graphics" => "Graphics",
            "Recording" => "Record",
            "Table" => "Mode",
            _ => "Game",
        };
    }
}
