using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// The monitor's view, switched off while VR shows the world (vr-urz.1; it was
    /// the same UpdateDesktopView / RestoreDesktopView in all five pivot rigs).
    ///
    /// In the world the game's own camera only has to exist: the game reads its
    /// transform, builds rays from it and culls by it. It doesn't have to draw.
    /// Blanking its culling mask saves a whole scene render a frame. The real
    /// mask is kept so the eyes can draw what the game camera would have
    /// (<see cref="GameMask"/>), and it is put back when VR leaves the world, the
    /// game swaps cameras, or the "Desktop view" setting is turned on.
    ///
    /// Each frame, before the eyes are set up:
    /// <code>
    /// desktop.Update(main, blank: inWorld &amp;&amp; !cfg.DesktopView.Value);
    /// int mask = desktop.GameMask(main);   // for the eyes' culling mask
    /// </code>
    /// and <see cref="Restore"/> when VR stops (headset lost, VR off).
    /// </summary>
    internal sealed class VrDesktop
    {
        private Camera blanked;
        private int savedMask;

        /// <summary>The camera blanked now, or null.</summary>
        public Camera Blanked => Alive(blanked) ? blanked : null;

        /// <summary>
        /// Blank <paramref name="main"/> (or keep it blanked), or put it back.
        /// A null camera restores whatever was blanked.
        /// </summary>
        public void Update(Camera main, bool blank)
        {
            if (main == null) { Restore(); return; }
            if (Alive(blanked) && (!blank || !VrCore.Same(main, blanked))) Restore();
            else if (blanked != null && !Alive(blanked)) blanked = null;   // the game destroyed it
            if (!blank) return;
            if (blanked == null)
            {
                blanked = main;
                savedMask = main.cullingMask;
                VrCore.Info($"[rig] desktop view off: '{main.name}' blanked (its mask 0x{savedMask:X} kept for the eyes)");
            }
            else if (main.cullingMask != 0)
            {
                // The game changed the mask itself (a view mode, a game state): follow it.
                savedMask = main.cullingMask;
            }
            if (main.cullingMask != 0) main.cullingMask = 0;
        }

        /// <summary>Put the blanked camera's mask back. Safe to call any time.</summary>
        public void Restore()
        {
            if (blanked == null) return;
            if (Alive(blanked))
            {
                blanked.cullingMask = savedMask;
                VrCore.Info($"[rig] desktop view back: '{blanked.name}' mask 0x{savedMask:X}");
            }
            blanked = null;
        }

        /// <summary>The game camera's real culling mask: the kept one while it is blanked.</summary>
        public int GameMask(Camera main)
        {
            if (main == null) return 0;
            return Alive(blanked) && VrCore.Same(main, blanked) ? savedMask : main.cullingMask;
        }

        private static bool Alive(Camera c)
        {
#if DVR_IL2CPP
            return c != null && !c.WasCollected;
#else
            return c != null;
#endif
        }
    }
}
