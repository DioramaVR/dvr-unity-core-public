using System;
using System.Collections.Generic;
using System.Linq;

namespace VrUiKit;

// The words the menu shows: labels cleaned up (units out of the label, the
// README's wording changes), short help lines for the core's rows, and icons.
static class MenuWording
{
    // "(m)" at the end of a label is the unit: the stepper shows it with the value.
    static readonly (string tag, string unit)[] Units =
    {
        ("(deg/s)", "°/s"), ("(m/s)", "m/s"), ("(deg)", "°"), ("(m)", "m"), ("(s)", "s"), ("(1/s)", "/s"),
    };

    // The README's wording changes, applied to every label and action name.
    static readonly (string from, string to)[] Renames =
    {
        ("Back / close", "Back"),
        ("Back / menu", "Back"),
        ("counterclockwise", "anticlockwise"),
        ("Counterclockwise", "Anticlockwise"),
        // Free mode became part of Arranged (2026-11-07); the mods still name the section so.
        ("Free mode windows", "Moving windows"),
    };

    // A label as shown: units taken off the end, wording changes applied.
    public static string Label(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? "";
        string s = raw.Trim();
        foreach (var (tag, _) in Units)
            if (s.EndsWith(" " + tag, StringComparison.Ordinal)) { s = s.Substring(0, s.Length - tag.Length - 1).TrimEnd(); break; }
        foreach (var (from, to) in Renames) s = s.Replace(from, to);
        return s;
    }

    // The unit in a label ("Table height (m)" -> "m"), or null.
    public static string UnitOf(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        foreach (var (tag, unit) in Units)
            if (raw.TrimEnd().EndsWith(" " + tag, StringComparison.Ordinal)) return unit;
        return null;
    }

    // Labels the core renames on its new pages (by the label as shown).
    static readonly Dictionary<string, string> Relabel = new()
    {
        ["Spectator view on monitor"] = "Spectator view on the monitor",
        ["Spectator field of view"] = "Field of view",
        ["Spectator smoothing"] = "Smoothing",
        ["Game UI panel"] = "Panel place",
        ["Video (needs ffmpeg)"] = "Record video",
        ["Photo"] = "Take photo",
    };

    public static string PageLabel(string shown) => Relabel.TryGetValue(shown, out var l) ? l : shown;

    // Short help lines for the core's rows, by the label as shown. A row's own
    // Help wins; then these; then the table rig's longer help (RigSettingDef.Help).
    static readonly Dictionary<string, string> CoreHelp = new()
    {
        ["Table height"] = "Also set by lifting the table",
        ["Table distance"] = "How far in front of you the table's centre is",
        ["Scale"] = "Mostly set by pinching with both grips",
        ["Background"] = "What surrounds the table",
        ["Radius"] = "Size of the circle on the table",
        ["Edge"] = "Sharp, or Soft (fades out at the rim)",
        ["Turn"] = "Smooth spins and coasts; Snap jumps a step a flick",
        ["Turn speed"] = "Top spin speed at full stick",
        ["Snap angle"] = "One step a flick",
        ["Turn ramp"] = "Time to reach full speed",
        ["Spin coast"] = "How long a spin keeps going; 0 stops at once",
        ["Zoom speed"] = "Zoom rate at full stick",
        ["Pan speed"] = "How fast the stick moves the world",
        ["Twist gain"] = "How far the table turns when you twist both grips",
        ["Stick dead zone"] = "Stick travel that is ignored",
        ["Follow the game camera"] = "The game's own panning moves the table",
        ["Fling"] = "Let go of a spinning rim to fling it",
        ["Reset to game defaults"] = "This mode's settings only; table height and distance stay",
        ["Orbit turn"] = "How strongly a sideways grip circles round",
        ["One grip"] = "Orbit circles round the character; Drag moves the table",
        ["Cut around the character"] = "Hide everything outside a circle",
        ["Cut radius"] = "Size of the cut",
        ["Monitor shows the game"] = "Costs frame rate",
        ["Resolution scale"] = "Lower it if the headset view stutters",
        ["Shadows"] = "Off saves a lot of frame time",
        ["Post-processing"] = "The game's colour grading and bloom",
        ["Laser angle"] = "How far the laser tilts down from the controller",
        ["Panel width"] = "Size of the game's UI panel",
        ["Panel height"] = "Height of the game's UI panel",
        ["Panel distance"] = "How far away the panel floats",
        ["Panel place"] = "Put the panel back where it started",
        ["Windows"] = "Popout draws each window alone; Cutout cuts it from the whole UI",
        ["Held windows"] = "How a window or button bar turns while you carry it",
        ["Window names"] = "A name tag on each window",
        ["Size in the world"] = "UI pieces at the laser or in the world",
        ["Log UI pieces"] = "Writes the game's UI pieces to a file, for mod makers",
        ["Field of view"] = "Of the spectator view on the monitor",
        ["Smoothing"] = "Evens out head movement in videos",
        ["Show VR menu in recordings"] = "Leave it off for clean videos",
        ["Capture diorama"] = "The table's circle, from 208 views, for a 3D model",
        ["Show diorama ring"] = "Marks what a capture takes, on the ground",
        ["Group names"] = "A label over each group of keys",
        ["Left-handed"] = "Laser on the left hand",
        ["Swap sticks"] = "The two thumbsticks trade jobs",
    };

    public static string HelpFor(VrSetting s, string shownLabel)
    {
        if (!string.IsNullOrEmpty(s?.Help)) return s.Help;
        if (shownLabel != null && CoreHelp.TryGetValue(shownLabel, out var h)) return h;
        var raw = s?.Label;
        var def = Dvr.RigSettings.PerMode.Concat(Dvr.RigSettings.Shared).Concat(Dvr.RigSettings.Graphics)
            .FirstOrDefault(d => d.Label == raw || Label(d.Label) == shownLabel);
        return def?.Help;
    }

    internal static bool HasCoreHelp(string shownLabel) => CoreHelp.ContainsKey(shownLabel);

    // Icons (Lucide names) for rows, sections and pages.
    static readonly Dictionary<string, string> RowIcons = new()
    {
        ["Left-handed"] = "hand",
        ["Swap sticks"] = "arrow-left-right",
        ["Monitor shows the game"] = "monitor",
        ["Shadows"] = "sun",
        ["Post-processing"] = "palette",
        ["Group names"] = "tag",
        ["Follow the game camera"] = "locate-fixed",
        ["Fling"] = "rotate-3d",
        ["Window names"] = "tag",
        ["Cut around the character"] = "circle-dot",
    };

    public static string IconFor(VrSetting s, string shownLabel)
    {
        if (!string.IsNullOrEmpty(s?.Icon)) return s.Icon;
        if (shownLabel != null && RowIcons.TryGetValue(shownLabel, out var i)) return i;
        if (s?.Section == "Hotkey bars") return "keyboard";
        return s is IToggleSetting ? "sliders-horizontal" : null;
    }

    public static string PageTitle(MenuPage p) => p switch
    {
        MenuPage.GameUi => "Game UI",
        _ => p.ToString(),
    };

    public static string PageIcon(MenuPage p) => p switch
    {
        MenuPage.Home => "house",
        MenuPage.View => "box",
        MenuPage.GameUi => "app-window",
        MenuPage.Controls => "gamepad-2",
        MenuPage.Capture => "camera",
        MenuPage.Graphics => "monitor",
        _ => "circle-help",
    };

    public static string PageSubtitle(MenuPage p) => p switch
    {
        MenuPage.View => "How the game sits on your table",
        MenuPage.GameUi => "Where the game's menus and HUD go",
        MenuPage.Controls => "What each button does",
        MenuPage.Capture => "Photos, videos and the monitor",
        MenuPage.Graphics => "Picture quality and frame rate",
        MenuPage.Help => "Questions players ask",
        _ => "",
    };
}
