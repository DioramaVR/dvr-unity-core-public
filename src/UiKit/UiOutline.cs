using UnityEngine;

namespace VrUiKit;

// The outline round things that can be moved while the VR menu is open: the
// game's windows (UiWindow), the button bars (VrHotbar) and the game's UI panel
// (VrPane). Pink is DioramaVR's interaction colour (the menu's pink-hi): faint
// on everything movable, brighter under the laser, solid while held, and yellow
// when letting go would put it on a wrist. Thin (bead vr-k7t, 2026-11-07).
sealed class UiOutline
{
    public enum State { Idle, Hover, Held, Snap }

    // Metres (tracking space): about 2 px of the VR menu at its usual distance.
    public const float Border = 0.0035f;

    static readonly Color PinkHi = new(1f, 0.247f, 0.525f, 1f); // #ff3f86, the menu's pink-hi
    public static readonly Color Idle = new(PinkHi.r, PinkHi.g, PinkHi.b, 0.4f);
    public static readonly Color Hover = new(PinkHi.r, PinkHi.g, PinkHi.b, 0.85f);
    public static readonly Color Held = PinkHi;
    public static readonly Color Snap = new(0.969f, 0.722f, 0.004f, 1f); // #f7b801, the menu's yellow (NWarn)
    public static readonly Color Fill = new(PinkHi.r, PinkHi.g, PinkHi.b, 0.1f); // an empty window's placeholder

    public static Color ColourOf(State s) => s switch
    {
        State.Held => Held,
        State.Snap => Snap,
        State.Hover => Hover,
        _ => Idle,
    };

    readonly string name;
    GameObject root;
    readonly GameObject[] edges = new GameObject[4];
    Material material;

    public UiOutline(string name) => this.name = name;

    public bool Shown => root != null && root.activeSelf;

    // centre: world pose of the outlined rectangle's centre; width / height: world units.
    public void Show(IVrUiHost host, Pose centre, float width, float height, State state)
    {
        if (root == null)
        {
            root = new GameObject("VrUi Outline " + name) { layer = host.VisualLayer };
            UiGeometry.Keep(root);
            material = host.MakeTexturedMaterial(Texture2D.whiteTexture);
            material.renderQueue = Dvr.VrDrawOrder.Menu;
            for (int i = 0; i < edges.Length; i++)
            {
                edges[i] = UiGeometry.MakeQuad("Edge", root.layer);
                edges[i].transform.SetParent(root.transform, false);
                edges[i].GetComponent<Renderer>().sharedMaterial = material;
            }
        }
        root.SetActive(true);
        var colour = ColourOf(state);
        material.color = colour;
        material.SetColor("_BaseColor", colour);
        float b = Border * host.WorldScale, hw = width * 0.5f, hh = height * 0.5f;
        root.transform.SetPositionAndRotation(centre.UnityPosition, centre.UnityRotation);
        Edges(edges, b, width, height, hw, hh);
    }

    public void Hide() { if (root != null) root.SetActive(false); }

    // The four edges just outside a w x h rectangle centred on the parent, b thick.
    internal static void Edges(GameObject[] edges, float b, float w, float h, float hw, float hh)
    {
        Set(edges[0], 0, hh + b * 0.5f, w + 2 * b, b);
        Set(edges[1], 0, -hh - b * 0.5f, w + 2 * b, b);
        Set(edges[2], -hw - b * 0.5f, 0, b, h);
        Set(edges[3], hw + b * 0.5f, 0, b, h);
    }

    static void Set(GameObject go, float x, float y, float w, float h)
    {
        go.transform.localPosition = new Vector3(x, y, 0);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = new Vector3(w, h, 1);
    }
}
