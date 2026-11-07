#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using UnityEngine;

namespace VrUiKit;

// What the VR UI framework needs from the game's VR mod. Everything here is
// game-agnostic: the framework never touches game types, so it can move into
// the shared core and be reused by the other mods.
//
// Spaces: "tracking" is the play space in metres (OpenVR's, converted to
// Unity's left-handed axes); "world" is the game world the eyes render.
interface IVrUiHost
{
    // VR is running and the UI should be drawn.
    bool Active { get; }
    // The game is somewhere the split windows make sense (e.g. in a level, not
    // the main menu). When false, the full panel is always used.
    bool CanArrange { get; }

    // The camera that renders the game's converted screen-space UI, and its target.
    Camera UiCamera { get; }
    RenderTexture UiTexture { get; }
    // Root canvases currently drawn into UiTexture.
    System.Collections.Generic.IReadOnlyList<Canvas> UiCanvases { get; }

    Pose Head { get; }                 // tracking space
    Pose LeftHand { get; }             // tracking space
    Pose RightHand { get; }
    bool LeftHanded { get; set; }      // laser on the left hand; "off hand" anchors and bindings swap
    Vector3 TableCentre { get; }       // tracking space, metres (the table surface centre)

    Pose TrackingToWorld(Pose p);
    float WorldScale { get; }          // world units per tracking metre
    int VisualLayer { get; }           // layer the eye cameras always render
    Material MakeTexturedMaterial(Texture texture);
    // Called every frame for each camera the framework owns (e.g. to keep it out of the game's GI).
    void PrepareCamera(Camera cam);

    // Show or hide the game's single full UI panel (the "Panel" mode).
    void ShowFullPanel(bool show);

    string ConfigDirectory { get; }
    void Log(string message);
}
