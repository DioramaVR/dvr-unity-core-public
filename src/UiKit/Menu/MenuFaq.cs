using System;
using System.Collections.Generic;
using System.Linq;

namespace VrUiKit;

// The Help page: the game's own questions first (MenuFaq.Add), under its name,
// then the core's under "Every game".
// Written from what the core does (docs/STANDARDS.md, HOOKS.md, RIG.md).
// Short, plain sentences; the first item starts open.
static class MenuFaq
{
    static readonly (string id, string q, string a)[] Core =
    {
        ("open-menu", "How do I open and close this menu?",
            "Hold both grips and pull both triggers. That works in every game, at any time. Some games also open it with a button; the Controls page lists it."),
        ("move-table", "How do I move, scale and turn the table?",
            "Hold {bind:table.move} and move your hand to drag the world. Lift or lower your hand to raise or lower the table. Hold it with both hands and pull them apart or together to scale, and twist them to turn."),
        ("sticks", "What do the thumbsticks do?",
            "{bind?:turn.left,turn.right} turns the table, and {bind?:zoom.in,zoom.out} zooms. {bind?:pan.up,pan.down,pan.left,pan.right} pans, or moves the circle in Turntable. In Orbit, one grip circles round your character. Home shows the controls for the mode you're in, and the Controls page lists them all."),
        ("modes", "What's the difference between Diorama, Turntable and Orbit?",
            "Diorama puts the whole world on the table as a model. Turntable cuts it to a circle on a table that spins, and the rest fades out. Orbit puts your character on the table, and you circle round them. Pick one on the View page; each keeps its own settings."),
        ("table-height", "The table is too high or too low.",
            "Hold {bind:table.move} and lift or lower your hand, or set Table height on the View page. 0.75 m suits seated play. Height and distance are shared by every mode."),
        ("move-things", "How do I move the menu, the game's panel or a button bar?",
            "Point the laser at it and hold the {laser side} grip: it rides on the laser. While you hold it, that hand's stick pushes it away or pulls it in, and makes it bigger or smaller. You can also touch it with your {other hand} and grip. Everything you can move has a pink outline while this menu is open, and stays put once it's closed."),
        ("wrist", "Can I wear a window or a button bar on my wrist?",
            "Yes. Let go of it next to your {other side} wrist and it rides there. That works for button bars, the game's panel and windows, but not this menu."),
        ("ui-modes", "Where did the game's menus go?",
            "On the Game UI page. Panel shows the whole UI on one floating screen. Arranged splits it into windows around the table. While this menu is open, grab a window to put it anywhere, then save that as a layout."),
        ("big-screen", "Why is the game on a big flat screen?",
            "There is no world to put on the table right now: the main menu, a map or a loading screen. The table comes back when you're in the world."),
        ("left-handed", "I'm left-handed.",
            "Turn on Left-handed on the Controls page. The laser moves to your left hand, and every binding swaps sides with it."),
        ("rebind", "Can I change the buttons?",
            "Yes. On the Controls page, click an action's key and press the button you want; \"+\" adds a second one. A button used twice turns amber. Presets save a whole set."),
        ("button-bars", "What are the button bars?",
            "Floating keys for the game's buttons the controllers don't have. Point the laser at a key and pull the {laser side} trigger. Switch them on on Home or the Game UI page."),
        ("stutter", "The headset view stutters.",
            "Lower Resolution scale on the Graphics page, or turn off Shadows or Monitor shows the game. Each costs frame rate."),
        ("photos", "Where do photos and videos go?",
            "Photos go to your Pictures folder and videos to your Videos folder, each in a \"Diorama VR\" folder. Videos need ffmpeg installed. The spectator view on the Capture page shows a smoothed picture on the monitor for recording."),
        ("no-vr", "VR didn't start.",
            "Start SteamVR before the game. If SteamVR isn't running, the game plays flat on the monitor as usual."),
    };

    static readonly List<(string q, string a)> game = new();

    // A game's own question: shown first, under the game's name (bead vr-1wc).
    public static void Add(string question, string answer) => game.Add((question, answer));
    internal static void ClearGame() => game.Clear();

    // The heading over the core's questions when the game has its own.
    public const string GeneralHeading = "Every game";

    // gameTitle: the heading over the game's questions. The game's come first,
    // then its extra tips (VrMenu.Welcome's after the first, which Home shows),
    // then the core's. The first item starts open.
    public static IEnumerable<FaqItem> Items(string gameTitle = "This game")
    {
        var tips = VrMenu.Tips.Skip(1).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        bool hasGame = game.Count > 0 || tips.Count > 0;
        bool first = true;
        string heading = hasGame ? gameTitle : null;
        int n = 0;
        foreach (var (q, a) in game)
        {
            yield return new FaqItem { Id = "game-" + (n++), Question = HelpText.Resolve(q), Answer = HelpText.Resolve(a), DefaultOpen = first, Heading = heading };
            first = false;
            heading = null;
        }
        if (tips.Count > 0)
        {
            yield return new FaqItem { Id = "game-tips", Question = "More tips for this game", Answer = HelpText.Resolve(string.Join(" ", tips)), DefaultOpen = first, Heading = heading };
            first = false;
        }
        heading = hasGame ? GeneralHeading : null;
        foreach (var (id, q, a) in Core)
        {
            yield return new FaqItem { Id = id, Question = HelpText.Resolve(q), Answer = HelpText.Resolve(a), DefaultOpen = first, Heading = heading };
            first = false;
            heading = null;
        }
    }
}
