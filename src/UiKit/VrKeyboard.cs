using System.Runtime.InteropServices;
using System.Text;
using TMPro;
using UnityEngine.EventSystems;
using Valve.VR;

namespace VrUiKit;

// A game text field, whatever its UI system: TextMesh Pro (TmpTextFields, in
// the core) or UI Toolkit (the mod supplies one, as timberborn-vr does).
interface ITextFields
{
    // The field that has focus now, or null.
    object Focused();
    bool Same(object a, object b);
    string Name(object field);
    int MaxLength(object field);
    string Get(object field);
    // Put text in the field with the cursor at its end and nothing selected:
    // a field that selects all when its text is set would otherwise have the
    // next key press replace everything (timberborn-vr, 2026-11-06).
    void Set(object field, string text);
    // Done on the keyboard: enter the text (as Enter would) and leave the field.
    void Submit(object field);
    void Leave(object field);
}

// Text entry in VR, the same in every game: when a text field gets focus,
// SteamVR's own keyboard opens with the field's text; what's typed is copied
// into the field as it changes (cursor at the end), Done enters it and leaves
// the field, and focus going elsewhere closes the keyboard. Once per focus:
// closed by the player, it stays closed until they click into a field again.
// Call Update every frame (after VrSession.BeginFrame); this reads SteamVR's
// event queue, so nothing else in the mod should.
static class VrKeyboard
{
    // TextMesh Pro fields unless the mod sets its own (UI Toolkit).
    public static ITextFields Fields = new TmpTextFields();

    static object field, lastOpened;
    static bool open;
    static readonly StringBuilder buffer = new(1024);

    public static bool Open => open;

    public static void Update(VrSession vr)
    {
        if (vr?.System == null || Fields == null) return; // simulated: no SteamVR
        var overlay = OpenVR.Overlay;
        if (overlay == null) return;
        PollEvents(vr, overlay);

        var focused = Fields.Focused();
        if (open && (focused == null || !Fields.Same(focused, field)))
        {
            // Focus went elsewhere (clicked away, a dialog closed): drop the keyboard.
            overlay.HideKeyboard();
            open = false;
            field = null;
        }
        if (!open && focused != null && (lastOpened == null || !Fields.Same(focused, lastOpened)))
        {
            field = lastOpened = focused;
            int max = Fields.MaxLength(field);
            var err = overlay.ShowKeyboard((int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
                (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine,
                0, "", (uint)(max > 0 ? max : 256), Fields.Get(field) ?? "", 0);
            open = err == EVROverlayError.None;
            VrCore.Info($"[keyboard] field '{Fields.Name(field)}' focused; SteamVR keyboard {(open ? "opened" : $"failed: {err}")}");
        }
        if (focused == null) lastOpened = null;
    }

    static void PollEvents(VrSession vr, CVROverlay overlay)
    {
        var ev = new VREvent_t();
        uint size = (uint)Marshal.SizeOf<VREvent_t>();
        while (vr.System.PollNextEvent(ref ev, size))
        {
            switch ((EVREventType)ev.eventType)
            {
                case EVREventType.VREvent_KeyboardCharInput:
                    if (open && field != null) Fields.Set(field, KeyboardText(overlay));
                    break;
                case EVREventType.VREvent_KeyboardDone:
                    if (open && field != null)
                    {
                        Fields.Set(field, KeyboardText(overlay));
                        Fields.Submit(field);
                        VrCore.Info($"[keyboard] entered '{Fields.Get(field)}'");
                    }
                    open = false;
                    break;
                case EVREventType.VREvent_KeyboardClosed:
                    if (open && field != null) Fields.Leave(field);
                    open = false;
                    break;
            }
        }
    }

    static string KeyboardText(CVROverlay overlay)
    {
        buffer.Clear();
        overlay.GetKeyboardText(buffer, (uint)buffer.Capacity);
        return buffer.ToString();
    }
}

// TextMesh Pro input fields (uGUI): the selected object's TMP_InputField.
sealed class TmpTextFields : ITextFields
{
    public object Focused()
    {
        var es = EventSystem.current;
        var selected = es != null ? es.currentSelectedGameObject : null;
        var f = selected != null ? selected.GetComponent<TMP_InputField>() : null;
        return f != null && f.isFocused && f.interactable ? f : null;
    }

    public bool Same(object a, object b) => a is TMP_InputField x && b is TMP_InputField y && VrCore.Same(x, y);
    public string Name(object f) => ((TMP_InputField)f).name;
    public int MaxLength(object f) => ((TMP_InputField)f).characterLimit;
    public string Get(object f) => ((TMP_InputField)f).text;

    public void Set(object f, string text)
    {
        var field = (TMP_InputField)f;
        field.text = text;
        int end = text.Length;
        field.caretPosition = end;
        field.selectionAnchorPosition = end;
        field.selectionFocusPosition = end;
    }

    public void Submit(object f)
    {
        var field = (TMP_InputField)f;
        field.onSubmit?.Invoke(field.text);
        field.DeactivateInputField(false);
    }

    public void Leave(object f) => ((TMP_InputField)f).DeactivateInputField(false);
}
