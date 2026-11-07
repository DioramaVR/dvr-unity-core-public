#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VrUiKit;

// What a window is attached to. Positions are metres in that anchor's space.
enum UiAnchor
{
    Room,       // the play space (stays put)
    Table,      // the table centre (follows table height changes)
    Head,       // the headset
    OffHand,    // the hand without the laser
    PointerHand // the hand with the laser
}

// The point of a window that stays put when it changes size (a list gets longer, a
// part opens): TopLeft for a list that grows down, Bottom for a bar that grows up.
enum UiPin { TopLeft, Top, TopRight, Left, Centre, Right, BottomLeft, Bottom, BottomRight }

static class UiPins
{
    // As a pivot: 0..1 across, 0..1 up.
    public static (float x, float y) Point(this UiPin pin)
    {
        int i = (int)pin;
        return (i % 3 * 0.5f, 1 - i / 3 * 0.5f);
    }

    // The pin nearest a pivot (a layout's pivot=x,y need not be one of the nine).
    public static UiPin Nearest(float x, float y)
    {
        int col = x < 0.25f ? 0 : x > 0.75f ? 2 : 1;
        int row = y > 0.75f ? 0 : y < 0.25f ? 2 : 1;
        return (UiPin)(row * 3 + col);
    }

    public static string Name(this UiPin pin) => pin switch
    {
        UiPin.TopLeft => "top left", UiPin.TopRight => "top right",
        UiPin.BottomLeft => "bottom left", UiPin.BottomRight => "bottom right",
        _ => pin.ToString().ToLowerInvariant(),
    };
}

// Where one window goes. Yaw / pitch are degrees in the anchor's space; a window
// faces away from the user along its +Z. Pivot is the point of the window
// (0..1 across, 0..1 up) that sits at Position, so menus that grow keep that
// corner still. Size is metres per 1000 UI pixels.
sealed class UiPlacement
{
    public string Window;
    public UiAnchor Anchor = UiAnchor.Room;
    public System.Numerics.Vector3 Position;
    public float Yaw, Pitch, Roll;
    public float Size = 0.8f;
    // The window's pin (UiPin) as a point. Setting it says so (PivotSet), and the layout
    // file keeps it; a placement that doesn't say uses its window's UiWindowDef.Pin.
    public float PivotX { get => pivotX; set { pivotX = value; PivotSet = true; } }
    public float PivotY { get => pivotY; set { pivotY = value; PivotSet = true; } }
    float pivotX = 0.5f, pivotY = 0.5f;
    public bool PivotSet { get; private set; }
    public bool Hidden;

    // The window's own pin, for a placement that doesn't say one (it stays unsaid).
    internal void DefaultPivot(UiPin pin)
    {
        if (PivotSet) return;
        (pivotX, pivotY) = pin.Point();
    }

    public UiPin Pin => UiPins.Nearest(PivotX, PivotY);

    // The centre of a width x height (metres) window placed by this, its anchor at anchorPose.
    public Pose CentreIn(Pose anchorPose, float width, float height) =>
        anchorPose.Compose(new Pose(Position, Rotation))
            .Compose(new Pose(new System.Numerics.Vector3((0.5f - PivotX) * width, (0.5f - PivotY) * height, 0), System.Numerics.Quaternion.Identity));

    // Pin the window at another point without moving it: Position moves to where that
    // point of the window is now. width / height: the window's size in metres as shown.
    public void MovePivot(float x, float y, float width, float height)
    {
        var shift = new System.Numerics.Vector3((x - PivotX) * width, (y - PivotY) * height, 0);
        Position += System.Numerics.Vector3.Transform(shift, Rotation);
        PivotX = x;
        PivotY = y;
    }
    public void MovePivot(UiPin pin, float width, float height)
    {
        var (x, y) = pin.Point();
        MovePivot(x, y, width, height);
    }

    public bool OnHand => Anchor is UiAnchor.OffHand or UiAnchor.PointerHand;

    public UiPlacement Clone() => (UiPlacement)MemberwiseClone();

    // The window's rotation in its anchor's space: yaw, then pitch, then roll about its facing.
    public System.Numerics.Quaternion Rotation => System.Numerics.Quaternion.Normalize(
        Pose.Yaw(Yaw) * Pose.Pitch(Pitch) *
        System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, Roll * MathF.PI / 180f));

    public void SetRotation(System.Numerics.Quaternion rotation)
    {
        (Yaw, Pitch) = UiGeometry.YawPitch(rotation);
        var rest = System.Numerics.Quaternion.Inverse(Pose.Yaw(Yaw) * Pose.Pitch(Pitch)) * rotation;
        var x = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, rest);
        Roll = MathF.Abs(x.X) + MathF.Abs(x.Y) < 1e-4f ? 0 : MathF.Atan2(x.Y, x.X) * 180f / MathF.PI;
        if (MathF.Abs(Roll) < 0.05f) Roll = 0;
    }
}

// A named arrangement of windows. Saved as a small text file, one window per
// line, so layouts can be shared by copying the file.
sealed class UiLayout
{
    public string Name;
    public bool BuiltIn;
    public string SourcePath; // the file it was loaded from, if any
    public readonly List<UiPlacement> Placements = new();

    public UiPlacement For(string window) => Placements.FirstOrDefault(p => p.Window == window);

    public UiLayout Clone(string name)
    {
        var copy = new UiLayout { Name = name };
        copy.Placements.AddRange(Placements.Select(p => p.Clone()));
        return copy;
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F(float v) => v.ToString("0.###", Inv);

    // window <id> anchor=<a> pos=<x>,<y>,<z> yaw=<deg> pitch=<deg> [roll=<deg>] size=<m per 1000px> [pivot=<x>,<y>] [hidden]
    public string Format()
    {
        var lines = new List<string> { "# VR UI layout", $"name = {Name}" };
        foreach (var p in Placements)
            lines.Add($"window {p.Window} anchor={p.Anchor} pos={F(p.Position.X)},{F(p.Position.Y)},{F(p.Position.Z)} " +
                      $"yaw={F(p.Yaw)} pitch={F(p.Pitch)}" + (p.Roll != 0 ? $" roll={F(p.Roll)}" : "") +
                      $" size={F(p.Size)}" + (p.PivotSet ? $" pivot={F(p.PivotX)},{F(p.PivotY)}" : "") + (p.Hidden ? " hidden" : ""));
        return string.Join("\n", lines) + "\n";
    }

    public static UiLayout Parse(string text, string fallbackName)
    {
        var layout = new UiLayout { Name = fallbackName };
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (line.StartsWith("name"))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) layout.Name = line[(eq + 1)..].Trim();
                continue;
            }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "window") continue;
            var p = new UiPlacement { Window = parts[1] };
            foreach (var part in parts.Skip(2))
            {
                if (part == "hidden") { p.Hidden = true; continue; }
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string key = part[..eq], value = part[(eq + 1)..];
                var nums = value.Split(',').Select(s => float.TryParse(s, NumberStyles.Float, Inv, out var f) ? f : 0f).ToArray();
                switch (key)
                {
                    case "anchor": if (Enum.TryParse<UiAnchor>(value, true, out var a)) p.Anchor = a; break;
                    case "pos" when nums.Length == 3: p.Position = new(nums[0], nums[1], nums[2]); break;
                    case "yaw": p.Yaw = nums[0]; break;
                    case "pitch": p.Pitch = nums[0]; break;
                    case "roll": p.Roll = nums[0]; break;
                    case "size": p.Size = nums[0]; break;
                    case "pivot" when nums.Length == 2: p.PivotX = nums[0]; p.PivotY = nums[1]; break;
                }
            }
            layout.Placements.Add(p);
        }
        return layout;
    }

    // Layout files live in <config>/<folder>/*.layout.txt.
    public static List<UiLayout> LoadAll(string dir)
    {
        var list = new List<UiLayout>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.GetFiles(dir, "*.layout.txt").OrderBy(f => f))
        {
            try
            {
                var layout = Parse(File.ReadAllText(file), Path.GetFileName(file).Replace(".layout.txt", ""));
                layout.SourcePath = file;
                list.Add(layout);
            }
            catch (Exception) { /* a broken file shouldn't stop the rest loading */ }
        }
        return list;
    }

    public string FilePath(string dir) =>
        Path.Combine(dir, new string(Name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray()) + ".layout.txt");

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(SourcePath ?? FilePath(dir), Format());
    }
}
