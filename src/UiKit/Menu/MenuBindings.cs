using System;
using System.Collections.Generic;
using System.Linq;

namespace VrUiKit;

// Controller bindings as the menu shows them: key names, clashes, and Home's
// cheat sheet for the current view mode.
static class MenuBindings
{
    // The side a binding is on, after handedness and Swap sticks: 'R' or 'L'.
    // Swap sticks moves the stick directions (not the clicks) to the other hand
    // (VrInput.Update), so a pointer-stick binding is pushed on the off hand's stick.
    public static char Side(VrBinding b, bool leftHanded) => Side(b, leftHanded, VrInput.SwapSticks);

    public static char Side(VrBinding b, bool leftHanded, bool swapSticks)
    {
        bool pointer = b.Role == HandRole.Pointer;
        if (swapSticks && b.Control >= VrControl.StickUp) pointer = !pointer;
        return pointer != leftHanded ? 'R' : 'L';
    }

    // The key on its own, as on the cheat sheet: "Trigger", "A", "Stick ←", "Stick click".
    public static string KeyName(VrBinding b, bool leftHanded)
    {
        bool left = Side(b, leftHanded) == 'L';
        return b.Control switch
        {
            VrControl.Trigger => "Trigger",
            VrControl.Grip => "Grip",
            VrControl.Primary => left ? "X" : "A",
            VrControl.Secondary => left ? "Y" : "B",
            VrControl.StickClick => "Stick click",
            VrControl.StickUp => "Stick ↑",
            VrControl.StickDown => "Stick ↓",
            VrControl.StickLeft => "Stick ←",
            VrControl.StickRight => "Stick →",
            _ => b.Control.ToString(),
        };
    }

    // On a key button, with the side: "R trigger", "L stick ←", "R A".
    public static string KeyText(VrBinding b, bool leftHanded)
    {
        var k = KeyName(b, leftHanded);
        if (k.Length > 1) k = char.ToLowerInvariant(k[0]) + k.Substring(1);
        return Side(b, leftHanded) + " " + k;
    }

    public static string KeysText(VrAction a) => string.Join(", ", a.Bindings.Select(b => KeyText(b, VrInput.LeftHanded)));

    // The first other action that uses one of this action's keys at the same
    // time (VrInput.Conflicts' rule: same group, or either has none), or null.
    public static VrAction ClashOf(VrAction a)
    {
        foreach (var b in a.Bindings)
            foreach (var o in VrInput.Actions)
                if (o != a && o.Bindings.Contains(b) && (o.Group == null || a.Group == null || o.Group == a.Group))
                    return o;
        return null;
    }

    // The actions the Controls page shows for the current mode.
    public static IEnumerable<VrAction> Shown() => VrInput.Actions.Where(a => VrModes.Uses(a) && VrMenu.Shows(a));

    // ---- the cheat sheet ----

    // The words that end a direction action's name: "Turn table left" and "Turn
    // table right" are one line, "Turn table".
    static readonly string[] Directions = { " left", " right", " in", " out", " away", " towards you", " up", " down", " clockwise", " anticlockwise" };
    // Names a merged line gets when its stem alone is too bare.
    static readonly Dictionary<string, string> StemNames = new() { ["Pan"] = "Pan table", ["Move"] = "Move" };

    // The action's name without a trailing direction or a note in brackets.
    public static string Stem(string name)
    {
        var s = MenuWording.Label(name ?? "");
        int paren = s.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) s = s.Substring(0, paren);
        foreach (var d in Directions)
            if (s.EndsWith(d, StringComparison.OrdinalIgnoreCase)) return s.Substring(0, s.Length - d.Length);
        return s;
    }

    static bool IsStick(VrControl c) => c >= VrControl.StickUp;

    // One line per action and side, the pointer hand's first ("R" when right-handed).
    // Direction actions on one stick merge: left + right -> "Stick ←→", up + down
    // -> "Stick ↑↓", all four -> "Stick".
    public static List<CheatRow> CheatSheet(bool leftHanded)
    {
        var lines = new List<(char hand, string section, string stem, string name, List<VrControl> keys, int order)>();
        int order = 0;
        foreach (var a in Shown())
        {
            foreach (var b in a.Bindings)
            {
                char hand = Side(b, leftHanded);
                string stem = Stem(a.Name);
                if (IsStick(b.Control))
                {
                    var same = lines.FindIndex(l => l.hand == hand && l.section == a.Section && l.stem == stem && l.keys.All(IsStick));
                    if (same >= 0) { if (!lines[same].keys.Contains(b.Control)) lines[same].keys.Add(b.Control); continue; }
                }
                lines.Add((hand, a.Section, stem, MenuWording.Label(a.Name), new List<VrControl> { b.Control }, order++));
            }
        }
        char first = leftHanded ? 'L' : 'R'; // the pointer hand's lines first
        return lines
            .OrderBy(l => l.hand == first ? 0 : 1).ThenBy(l => l.order)
            .Select(l => new CheatRow
            {
                Hand = l.hand,
                Action = l.keys.Count > 1 ? (StemNames.TryGetValue(l.stem, out var n) ? n : l.stem) : ShortName(l.name),
                Keys = Keys(l.keys, l.hand, leftHanded),
            }).ToList();
    }

    // The cheat sheet's own short names: no note in brackets.
    static string ShortName(string name)
    {
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name.Substring(0, paren) : name;
    }

    static string Keys(List<VrControl> keys, char hand, bool leftHanded)
    {
        if (keys.Count == 1)
            return KeyName(new VrBinding(hand == 'R' != leftHanded ? HandRole.Pointer : HandRole.Off, keys[0]), leftHanded);
        bool lr = keys.Contains(VrControl.StickLeft) || keys.Contains(VrControl.StickRight);
        bool ud = keys.Contains(VrControl.StickUp) || keys.Contains(VrControl.StickDown);
        if (lr && ud) return "Stick";
        return lr ? "Stick ←→" : "Stick ↑↓";
    }
}
