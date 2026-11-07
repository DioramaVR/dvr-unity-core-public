using System;
using UnityEngine;

namespace VrUiKit;

// One scrolling area of the menu, as a model the menu drives (docs/menu-design:
// "Scroll area"). The menu uses no uGUI events and no EventSystem (class
// injection is ruled out in some games), so nothing here listens for input:
// the menu passes the laser point, the trigger and the stick in, draws the
// thumb and arrows at the rectangles this gives, and moves its content by
// Offset (Apply does that for a RectTransform).
//
// Coordinates are canvas pixels from the top-left, as the menu's controls use.
// A rectangle "in the content" is where it is drawn at offset 0; it shows at
// y - Offset.
//
//   var s = new VrScroll { Viewport = new Rect(120, 112, 1080, 688) };
//   s.ContentHeight = bottom + 28 - s.Viewport.y;        after building the page
//   each frame:
//     bool stick = s.Update(laserPoint, triggerHeld, handStickY, dt);
//     (claim that hand's stick while stick is true; release it when false)
//     s.Apply(contentRoot);                                 or read s.Offset
//   hit test a scrolled control: s.Hits(control.Rect, laserPoint)
//
// The pieces Update uses (Press, Drag, Release, Stick, Wheel, Tick) are public
// too, for a menu that routes its own clicks.
sealed class VrScroll
{
    // ---- geometry ----

    // The visible area, canvas px. The bar sits inside its right edge.
    public Rect Viewport;
    // How tall the content is, px from the viewport's top (Offset runs 0..Max).
    public float ContentHeight { get => contentHeight; set { contentHeight = Math.Max(0, value); SetOffset(offset); } }
    public float Offset => offset;
    public float Max => Math.Max(0, contentHeight - Viewport.height);
    public bool CanScroll => Max > 0.5f;

    // ---- tuning (design: hit targets at least 48 px) ----

    public float BarWidth = 48;     // the bar's hit width: the thumb and arrows are this wide to the laser
    public float ThumbWidth = 12;   // the thumb as drawn, centred in the bar
    public float ArrowSize = 48;    // the up and down buttons, at the bar's ends; 0 for none
    public float MinThumb = 64;     // the shortest the thumb gets
    public float ArrowStep = 96;    // px one arrow click moves
    public float ArrowHoldDelay = 0.35f, ArrowHoldRate = 900; // held arrow: after the delay, px/s
    public float WheelStep = 96;    // px a mouse-wheel notch moves
    public float StickRate = 1400;  // px/s at full push
    public float StickCurve = 2;    // speed rises as push^curve: fine control near the centre
    public float StickDeadZone = 0.15f;
    public float Coast = 6;         // 1/s: how fast a released stick's scroll slows

    // ---- state ----

    float contentHeight, offset, velocity;
    bool over, lastTrigger;
    enum Grab { None, Thumb, Up, Down }
    Grab grab;
    float grabOffset, held;

    // The laser is over the viewport (or holding the thumb) and there is something to scroll.
    public bool Over => over;
    // True while the thumb or an arrow is held by the trigger.
    public bool Dragging => grab != Grab.None;
    // The stick should scroll this area (claim it from the table): the laser is
    // over it and it can scroll. The menu does the claiming.
    public bool WantsStick => over && CanScroll;
    // Still moving (coasting): the menu may want to redraw.
    public bool Moving => velocity != 0 || grab == Grab.Up || grab == Grab.Down;

    // ---- rectangles the menu draws and hit-tests (canvas px) ----

    public Rect Bar => new(Viewport.xMax - BarWidth, Viewport.y, BarWidth, Viewport.height);
    public Rect UpArrow => ArrowSize > 0 ? new Rect(Bar.x, Viewport.y, BarWidth, ArrowSize) : default;
    public Rect DownArrow => ArrowSize > 0 ? new Rect(Bar.x, Viewport.yMax - ArrowSize, BarWidth, ArrowSize) : default;
    // Where the thumb runs: the bar between the arrows.
    public Rect Track => new(Bar.x, Viewport.y + ArrowSize, BarWidth, Math.Max(0, Viewport.height - 2 * ArrowSize));
    public float ThumbLength => ScrollMath.ThumbLength(Viewport.height, contentHeight, Track.height, MinThumb);
    // The thumb's hit area (the bar's width), and as drawn (ThumbWidth, centred).
    public Rect Thumb => new(Bar.x, Track.y + ScrollMath.ThumbTop(offset, Max, Track.height, ThumbLength), BarWidth, ThumbLength);
    public Rect ThumbDrawn { get { var t = Thumb; return new Rect(t.x + (BarWidth - ThumbWidth) / 2, t.y, ThumbWidth, t.height); } }

    // ---- what is shown where ----

    public bool InViewport(Vector2 p) => Viewport.Contains(p);
    // Where a content rectangle is drawn now.
    public Rect OnCanvas(Rect content) => new(content.x, content.y - offset, content.width, content.height);
    // A canvas point, in the content.
    public Vector2 ToContent(Vector2 p) => new(p.x, p.y + offset);
    // The laser at p hits this content rectangle: inside the viewport (scrolled-out
    // controls aren't clickable), not on the bar, and on the rectangle as scrolled.
    public bool Hits(Rect content, Vector2 p) =>
        InViewport(p) && !(CanScroll && Bar.Contains(p)) && content.Contains(ToContent(p));
    // Any part of the content rectangle shows in the viewport.
    public bool Visible(Rect content) => OnCanvas(content).Overlaps(Viewport);

    // ---- moving ----

    public void SetOffset(float y) => offset = Clamp(y, 0, Max);
    // Move by px (positive: further down the content). Stops a coast.
    public void Scroll(float px) { velocity = 0; SetOffset(offset + px); }
    public void Wheel(float notches) => Scroll(-notches * WheelStep); // wheel up (positive) scrolls up
    // Bring a content rectangle fully into view (the nearest way), with a margin.
    public void ScrollIntoView(Rect content, float margin = 16)
    {
        float top = content.y - Viewport.y - margin, bottom = content.yMax - Viewport.y + margin;
        if (top < offset) Scroll(top - offset);
        else if (bottom > offset + Viewport.height) Scroll(bottom - Viewport.height - offset);
    }
    public void ResetOffset() { offset = velocity = 0; grab = Grab.None; }

    // The stick's push this frame (up positive), while WantsStick. Faster the
    // further it's pushed; let go and it coasts to a stop.
    public void Stick(float y, float dt)
    {
        float v = ScrollMath.StickSpeed(y, StickDeadZone, StickRate, StickCurve);
        if (v != 0) velocity = v;
        Move(dt);
    }

    // Per frame: coasting and held arrows (Stick calls it too; call it when not over the area).
    public void Tick(float dt)
    {
        if (grab == Grab.Up || grab == Grab.Down)
        {
            held += dt;
            if (held > ArrowHoldDelay) SetOffset(offset + (grab == Grab.Up ? -1 : 1) * ArrowHoldRate * dt);
        }
        Move(dt);
    }

    void Move(float dt)
    {
        if (velocity == 0) return;
        float before = offset;
        SetOffset(offset + velocity * dt);
        // Hit an end: stop there, no bounce.
        if (offset == before && (offset == 0 || offset == Max)) { velocity = 0; return; }
        velocity = ScrollMath.Coast(velocity, dt, Coast);
    }

    // The trigger went down at p. True if the bar took it (the thumb, an
    // arrow, or the track: the thumb's centre jumps there and is held).
    public bool Press(Vector2 p)
    {
        if (!CanScroll || !Bar.Contains(p)) return false;
        velocity = 0;
        held = 0;
        if (ArrowSize > 0 && UpArrow.Contains(p)) { grab = Grab.Up; SetOffset(offset - ArrowStep); return true; }
        if (ArrowSize > 0 && DownArrow.Contains(p)) { grab = Grab.Down; SetOffset(offset + ArrowStep); return true; }
        var t = Thumb;
        grabOffset = p.y >= t.y && p.y <= t.yMax ? p.y - t.y : t.height / 2;
        grab = Grab.Thumb;
        Drag(p);
        return true;
    }

    // The held trigger, with the laser now at p: the thumb follows.
    public void Drag(Vector2 p)
    {
        if (grab != Grab.Thumb) return;
        SetOffset(ScrollMath.OffsetForThumb(p.y - grabOffset - Track.y, Max, Track.height, ThumbLength));
    }

    public void Release() => grab = Grab.None;

    // Hover: where the laser is (null: off the menu).
    public void Hover(Vector2? p) => over = CanScroll && (Dragging || p is Vector2 q && InViewport(q));

    // All of it, once a frame: the laser point (null off the menu), whether the
    // trigger is held, that hand's stick (up positive) and dt. Returns WantsStick.
    // The trigger's press on the bar is taken here: don't also route it as a click.
    public bool Update(Vector2? laser, bool trigger, float stickY, float dt)
    {
        if (trigger && !lastTrigger && laser is Vector2 p) Press(p);
        if (!trigger) Release();
        lastTrigger = trigger;
        if (Dragging && laser is Vector2 d) Drag(d);
        Hover(laser);
        if (WantsStick && !Dragging) Stick(stickY, dt);
        else Tick(dt);
        return WantsStick;
    }

    // Moves the content root by the offset: its anchoredPosition.y (top-anchored
    // content, y down the canvas, so scrolling down moves it up).
    public void Apply(RectTransform content)
    {
        if (content == null) return;
        var a = content.anchoredPosition;
        if (a.y != offset) content.anchoredPosition = new Vector2(a.x, offset);
    }

    static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}

// VrScroll's numbers, apart (CoreTests).
static class ScrollMath
{
    // Scroll speed for a stick push y in [-1, 1] (up positive): 0 inside the
    // dead zone, then rate * push^curve over the rest, rescaled so it starts
    // at 0. Pushing up moves towards the top (a negative speed).
    public static float StickSpeed(float y, float deadZone, float rate, float curve)
    {
        float a = Math.Abs(y);
        if (a <= deadZone) return 0;
        float t = Math.Min(1, (a - deadZone) / (1 - deadZone));
        return -Math.Sign(y) * (float)Math.Pow(t, curve) * rate;
    }

    // A coasting speed after dt: exponential slowing at k per second, and 0
    // once it's under 5 px/s (so it ends rather than creeping).
    public static float Coast(float velocity, float dt, float k)
    {
        float v = velocity * (float)Math.Exp(-k * dt);
        return Math.Abs(v) < 5 ? 0 : v;
    }

    // The thumb's length: the share of the content in view, of the track, at least min.
    public static float ThumbLength(float view, float content, float track, float min)
    {
        if (content <= view || track <= 0) return track;
        return Math.Min(track, Math.Max(min, track * view / content));
    }

    // The thumb's top in the track for an offset.
    public static float ThumbTop(float offset, float max, float track, float thumb) =>
        max <= 0 ? 0 : (track - thumb) * Math.Max(0, Math.Min(1, offset / max));

    // The offset for a thumb top in the track (the inverse).
    public static float OffsetForThumb(float top, float max, float track, float thumb)
    {
        float run = track - thumb;
        if (run <= 0 || max <= 0) return 0;
        return Math.Max(0, Math.Min(1, top / run)) * max;
    }
}
