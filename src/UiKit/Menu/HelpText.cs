using System;
using System.Collections.Generic;
using System.Text;

namespace VrUiKit;

// Help text that names controls as they are bound now. Help answers, tips and
// row help are written with placeholders, resolved each time the menu builds
// them, so Left-handed, Swap sticks, a preset or a rebind on the Controls page
// changes the words too. Plain text passes through untouched.
//
//   {press:id}          what to do, from VrInput's bindings for action id:
//                       "press A", "pull the right trigger", "click the left stick",
//                       "push the left stick left or right", "squeeze either grip"
//   {bind:id}           the control alone: "A", "the right trigger", "either grip",
//                       "the left stick click", "the left stick (left and right)"
//   {press:id1,id2}     several actions as one (turn.left,turn.right: one stick)
//   {press?:id} {bind?:id}  optional: if none of the ids is registered and bound
//                       in this game, the whole sentence holding it is left out
//   {laser hand} {other hand}  "right hand" / "left hand"
//   {laser side} {other side}  "right" / "left"
//   {key:action|F8}     a game key: the key the game binds to its action now
//                       (VrHotkeys.UseActions), else the fallback after the |
//   {{ }}               literal braces
//
// A placeholder that starts a sentence gets a capital. A registered action with
// no binding reads "(not bound: see Controls)"; an unknown placeholder "(?)",
// logged once. Raw braces are never shown.
static class HelpText
{
    public const string NotBound = "(not bound: see Controls)";
    public const string Unknown = "(?)";

    static readonly HashSet<string> warned = new();

    // The pointer (laser) hand is on the left.
    static bool LeftHanded => VrUi.Host != null ? VrUi.Host.LeftHanded : VrInput.LeftHanded;

    public static string Resolve(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0 && text.IndexOf('}') < 0) return text;
        var sb = new StringBuilder();
        foreach (var sentence in Sentences(text))
        {
            var s = ResolveSentence(sentence);
            if (s != null) sb.Append(s);
        }
        return sb.ToString().Trim();
    }

    // Splits after ". ", "! ", "? " (and the end), keeping the spaces; never inside braces.
    static IEnumerable<string> Sentences(string text)
    {
        int start = 0, depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && (c == '.' || c == '!' || c == '?') && (i + 1 == text.Length || text[i + 1] == ' '))
            {
                int end = i + 1;
                while (end < text.Length && text[end] == ' ') end++;
                yield return text.Substring(start, end - start);
                start = end;
                i = end - 1;
            }
        }
        if (start < text.Length) yield return text.Substring(start);
    }

    // Null: an optional placeholder's actions don't exist here, so the sentence goes.
    static string ResolveSentence(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '{' && i + 1 < s.Length && s[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < s.Length && s[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (c == '}') continue; // a stray brace is dropped, never shown
            if (c != '{') { sb.Append(c); continue; }
            int close = s.IndexOf('}', i + 1);
            if (close < 0) { i = s.Length; break; } // unclosed: drop the rest of the token
            string token = s.Substring(i + 1, close - i - 1).Trim();
            i = close;
            var word = Token(token, out bool drop);
            if (drop) return null;
            if (StartsSentence(sb)) word = Capital(word);
            sb.Append(word);
        }
        return sb.ToString();
    }

    static bool StartsSentence(StringBuilder sb)
    {
        for (int i = sb.Length - 1; i >= 0; i--)
        {
            char c = sb[i];
            if (c == ' ' || c == '"' || c == '\'') continue;
            return false;
        }
        return true; // only spaces or quotes before it in this sentence
    }

    static string Capital(string w) => string.IsNullOrEmpty(w) || !char.IsLower(w[0]) ? w : char.ToUpperInvariant(w[0]) + w.Substring(1);

    static string Token(string token, out bool drop)
    {
        drop = false;
        bool left = LeftHanded;
        switch (token)
        {
            case "laser hand": return (left ? "left" : "right") + " hand";
            case "other hand": return (left ? "right" : "left") + " hand";
            case "laser side": return left ? "left" : "right";
            case "other side": return left ? "right" : "left";
        }
        int colon = token.IndexOf(':');
        string kind = colon > 0 ? token.Substring(0, colon).Trim() : token;
        string arg = colon > 0 ? token.Substring(colon + 1).Trim() : "";
        bool optional = kind.EndsWith("?");
        if (optional) kind = kind.Substring(0, kind.Length - 1);
        switch (kind)
        {
            case "press":
            case "bind":
            {
                // Plain loops: lambdas here need NullableAttribute, which IL2CPP's class library lacks.
                var actions = new List<VrAction>();
                var bindings = new List<VrBinding>();
                foreach (var part in arg.Split(','))
                {
                    string id = part.Trim();
                    foreach (var a in VrInput.Actions)
                    {
                        if (a.Id != id) continue;
                        actions.Add(a);
                        foreach (var b in a.Bindings) if (!bindings.Contains(b)) bindings.Add(b);
                        break;
                    }
                }
                if (optional && bindings.Count == 0) { drop = true; return ""; }
                if (actions.Count == 0)
                {
                    Warn($"no action '{arg}' registered");
                    return NotBound;
                }
                return Controls(bindings, kind == "press", left);
            }
            case "key":
            {
                int bar = arg.IndexOf('|');
                string action = (bar >= 0 ? arg.Substring(0, bar) : arg).Trim();
                string fallback = bar >= 0 ? arg.Substring(bar + 1).Trim() : null;
                var key = VrHotkeys.KeyFor(new HotkeyButton { Action = action.Length > 0 ? action : null, Key = fallback });
                if (string.IsNullOrEmpty(key)) { Warn($"no key for game action '{action}'"); return Unknown; }
                return KeyWords(key);
            }
        }
        Warn($"unknown placeholder {{{token}}}");
        return Unknown;
    }

    static void Warn(string what)
    {
        if (warned.Add(what)) VrUi.Host?.Log("[menu] help text: " + what);
    }

    internal static void ResetWarnings() => warned.Clear();

    // ---- controls in words ----

    static bool IsStick(VrControl c) => c >= VrControl.StickUp;

    static string SideWord(VrBinding b, bool left) => MenuBindings.Side(b, left) == 'L' ? "left" : "right";

    // The bindings in words: one phrase per control (a stick's directions merge,
    // the same button on both hands is "either"), joined with "or".
    public static string Controls(IList<VrBinding> bindings, bool verb, bool leftHanded)
    {
        if (bindings.Count == 0) return NotBound;
        var parts = new List<string>();
        var done = new HashSet<int>();
        for (int i = 0; i < bindings.Count; i++)
        {
            if (done.Contains(i)) continue;
            var b = bindings[i];
            string side = SideWord(b, leftHanded);
            if (IsStick(b.Control))
            {
                var dirs = new List<VrControl>();
                for (int j = i; j < bindings.Count; j++)
                    if (IsStick(bindings[j].Control) && SideWord(bindings[j], leftHanded) == side) { dirs.Add(bindings[j].Control); done.Add(j); }
                parts.Add(Stick(side, dirs, verb));
                continue;
            }
            done.Add(i);
            int twin = -1;
            for (int j = i + 1; j < bindings.Count; j++)
                if (!done.Contains(j) && bindings[j].Control == b.Control && SideWord(bindings[j], leftHanded) != side) { twin = j; break; }
            if (twin >= 0) done.Add(twin);
            parts.Add(Button(b, side, twin >= 0, verb, leftHanded));
        }
        return string.Join(" or ", parts);
    }

    static string Button(VrBinding b, string side, bool both, bool verb, bool leftHanded)
    {
        string where = both ? "either" : "the " + side;
        switch (b.Control)
        {
            case VrControl.Trigger: return (verb ? "pull " : "") + where + " trigger";
            case VrControl.Grip: return (verb ? "squeeze " : "") + where + " grip";
            case VrControl.StickClick: return verb ? "click " + where + " stick" : where + " stick click";
            default:
                // Face buttons: their letters name the side (A / B right, X / Y left).
                string letter = both
                    ? (b.Control == VrControl.Primary ? "A or X" : "B or Y")
                    : MenuBindings.KeyName(b, leftHanded);
                return (verb ? "press " : "") + letter;
        }
    }

    static string Stick(string side, List<VrControl> dirs, bool verb)
    {
        var words = new List<string>();
        if (dirs.Contains(VrControl.StickUp)) words.Add("up");
        if (dirs.Contains(VrControl.StickDown)) words.Add("down");
        if (dirs.Contains(VrControl.StickLeft)) words.Add("left");
        if (dirs.Contains(VrControl.StickRight)) words.Add("right");
        string stick = "the " + side + " stick";
        if (words.Count == 4) return verb ? "push " + stick : stick;
        return verb ? "push " + stick + " " + string.Join(" or ", words) : stick + " (" + string.Join(" and ", words) + ")";
    }

    // A key name as UseKeys takes it, for reading: "PageUp" -> "Page Up", "Alpha1" -> "1".
    public static string KeyWords(string key)
    {
        if (key.StartsWith("Alpha") && key.Length == 6 && char.IsDigit(key[5])) return key.Substring(5);
        var sb = new StringBuilder();
        for (int i = 0; i < key.Length; i++)
        {
            if (i > 0 && char.IsUpper(key[i]) && char.IsLower(key[i - 1])) sb.Append(' ');
            sb.Append(key[i]);
        }
        return sb.ToString();
    }
}
