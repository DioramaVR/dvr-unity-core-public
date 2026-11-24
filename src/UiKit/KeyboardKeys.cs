using System;
using System.Text;

namespace VrUiKit;

// A field's text from SteamVR's keyboard, built key by key (VrKeyboard).
//
// SteamVR's current keyboard (2026-11-23, Cataclismo, raw events logged) sends
// each KeyboardCharInput with its data all zero, so the key's own input
// (cNewInput) is empty, and GetKeyboardText then holds ONLY THE NEWEST KEY, not
// the whole text. Copying that text into the field left just the last letter
// ("each key replaces the last"). So:
// - the event's own input, when it has one (a character, or backspace), is
//   applied to the field's text;
// - otherwise SteamVR's text decides. It is the whole text if it has two or more
//   characters, or is exactly one key longer than the field and starts with it,
//   or one shorter and the field starts with it. Otherwise it is the newest key
//   only: that key is added, and an empty text is a backspace;
// - which kind SteamVR's text is, once seen, holds until Reset (a new field), so
//   a 'd' typed after "dv" isn't read as the whole text going back to "d".
// User test with this logic (in Cataclismo): 'ddv', backspace -> 'dd', then
// 'ddhhhhhhh', all right.
sealed class KeyboardKeys
{
    bool? newestOnly;

    public bool? NewestOnly => newestOnly;

    // A new field (the keyboard opened on it): what SteamVR's text means is found again.
    public void Reset() => newestOnly = null;

    // The field's text after one key event: `before` the field's text, `key` the event's own
    // input (cNewInput, "" when the event has none), `keyboardText` SteamVR's text now,
    // `max` the field's character limit (0: none).
    public string Next(string before, string key, string keyboardText, int max)
    {
        before ??= "";
        if (!string.IsNullOrEmpty(key)) return Apply(before, key, max);
        var text = keyboardText ?? "";
        if (text.Length >= 2) newestOnly = false;
        bool whole = newestOnly != true &&
            (newestOnly == false
             || (text.Length == before.Length + 1 && text.StartsWith(before, StringComparison.Ordinal))
             || (text.Length > 0 && text.Length + 1 == before.Length && before.StartsWith(text, StringComparison.Ordinal)));
        if (!whole && text.Length > 0) newestOnly = true;
        if (whole) return Limit(text, max);
        return Apply(before, text.Length == 0 ? "\b" : text, max);
    }

    // Keys onto a text: backspace removes the last character; other control characters
    // (Enter, tab) add nothing; nothing past the limit.
    public static string Apply(string text, string key, int max)
    {
        var sb = new StringBuilder(text ?? "");
        foreach (char c in key)
        {
            if (c == '\b') { if (sb.Length > 0) sb.Length--; }
            else if (c >= ' ' && (max <= 0 || sb.Length < max)) sb.Append(c);
        }
        return sb.ToString();
    }

    static string Limit(string text, int max) => max > 0 && text.Length > max ? text.Substring(0, max) : text;

    // An event's cNewInput bytes as text (UTF-8, up to the first zero).
    public static string Input(byte[] bytes)
    {
        int n = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, n < 0 ? bytes.Length : n);
    }
}
