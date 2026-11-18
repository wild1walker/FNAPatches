using MonoMod.InlineRT;
using MonoMod;
using System.Collections.Generic;
using System.Diagnostics;
using System;
using Terraria.ID;
using Terraria;

#pragma warning disable 1591, 0649, 0169, 0414

// The sections' tiles framed ahead (wild1walker, portmaster-terraria, MIT: tools/trpatch/FramesPatch.cs and runtime/Frames.cs):
// the game chooses each tile's picture (its "frame") a section of 200 x 150 tiles at a time, when the section first comes into
// view: 30,000 tiles in the middle of one frame. TrPatches.Frames does that work for the sections the view is moving towards,
// a little each update, and the game's own pass then finds them done; a tile whose frame could depend on what changes meanwhile
// is left to the game's pass, as the game's code reads it (the rules there). TR_FRAMES=0: as before. TERRARIA_FRAMES=0 leaves
// the pass out when patching.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_SPEED") == "0" || Environment.GetEnvironmentVariable("TERRARIA_FRAMES") == "0")
				return;
			// matched on the game's own code (before the tile conversion); the placeholder replaced after it
			bool hook = TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Frames, "frames", m => TerrariaPatches.TileConvert.FramesPass.Apply(m));
			hook |= TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.FramesPointers, "frames-pointers", m => TerrariaPatches.TileConvert.FramesPass.Pointers(m));
			if (hook)
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
    // The game chooses each tile's picture (its "frame") a section of 200 x 150 tiles at a time,
    // when the section first comes into view: 30,000 tiles in the middle of one frame, 38 - 46 ms
    // on an H700 (docs/notes.md). That pass is not only about pictures: for each tile it also wakes
    // its liquid, lets unsupported sand fall, and checks torches, cactus and vines, and choosing a
    // picture can ask the same of a neighbour. So the pass cannot simply be run earlier: the world
    // would change at another moment than in the game.
    //
    // It is split in two here.
    //
    // Ahead of the view, a slice of each update (Tick): a section near the view is looked at, in
    // the game's order, and the tiles that have nothing else to do in the pass are done: a solid,
    // dry block that is not an object gets its picture; an empty dry cell needs nothing; an
    // object (other than a torch and the like) is not touched by the pass at all; every wall gets
    // its picture. With each such tile what it was is kept (Class). Every other tile is left
    // alone. While this runs, a frame call the game's picture code makes for a neighbour comes to
    // Enter: the neighbour's picture is made as the game would, and where the call could do more
    // than that it is held back, with the tile it came from.
    //
    // At the game's own moment (the section comes into view; Begin, Reframe, End from the game's
    // pass): the game's pass runs as it is, in its order; a tile done ahead that is still what it
    // was is skipped, the calls held back for it are made, and every other tile goes through the
    // game's own routine. A block that can fall (sand) is skipped only if the game's own test
    // says, then, that it does not fall. So what the pass changes in the world changes then, in
    // the same order.
    //
    // A tile done ahead is given back to the game's routine when anything asks for its frame
    // again before the section is in view (Mark), or when it is no longer what it was (another
    // kind of tile, liquid in it). A section is given back whole when the view arrives before it is done,
    // when the game frames several sections at once, when a thread other than the game's frames
    // tiles (the start of hardmode), or when invisible blocks are shown otherwise than they were.
    //
    // What differs from the game: its random choice among a tile's picture variants is made at
    // another moment (the game's own order already depends on the camera's path), and a border
    // tile of a section in view can take its final picture a moment earlier.
    //
    // Single player only. TR_FRAMES=0: off. TR_FRAMES_MS: the slice, ms (0.5; three times that
    // when the view is about to reach a section).
    public static unsafe class Frames
    {
        static readonly bool slow = Environment.GetEnvironmentVariable("TR_SLOW") != null;   // (FNAPatches) tests: told what happened
        sealed class Section
        {
            public int sx, sy;
            public int x0, y0, w, h;        // the pass's tiles, in its order: columns, then rows
            public byte[] was;              // a tile done ahead: what it was then (its class + 1)
            public readonly List<Call> calls = new List<Call>();
            public int next;                // how far the pass ahead is
            public bool complete, echo;
        }

        struct Call
        {
            public int at;                  // the tile of the pass it came from
            public int i, j;
            public bool reset, noBreak;
            public bool cactus;             // the cactus routine's call, not the frame routine's
        }

        const int Idle = 0, Plain = 1, Still = 2, Falling = 3, Wet = 4, Other = 5;
        const byte Outside = 7;
        const int Most = 16, MostCalls = 20000;

        static readonly bool on = Environment.GetEnvironmentVariable("TR_FRAMES") != "0";
        static readonly long slice = Slice();
        static Section[] live = new Section[0];     // replaced, never changed: Mark reads it
        static bool busy;                   // looking, or sections wait: the game's frame calls come to Enter
        static bool looking;
        static volatile bool spoiled;
        static Section job, taking;
        static int parent, replay, nextAt, left;
        static long takeAt, mapAt;
        static readonly int[] leftBy = new int[6];
        static object manager;
        static int mainThread, idle, rest;
        static readonly HashSet<int> skip = new HashSet<int>();
        public static int Ahead, Taken, Late;

        static long Slice() =>
            (long)((double.TryParse(Environment.GetEnvironmentVariable("TR_FRAMES_MS"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double ms) && ms > 0 ? ms : 0.5) * Stopwatch.Frequency / 1000);

        public static string Line() => $"sections ahead {Ahead}, taken {Taken}, late {Late}";

        // (FNAPatches) a tile's record (TileRecords): type at 0, liquid at 4, sTileHeader at 5, bTileHeader3 at 9
        static int Class(byte* t)
        {
            int s = *(ushort*)(t + 5);
            bool dry = t[4] == 0;
            if ((s & 0x20) == 0)
                return (s & 0x741f) == 0 && (t[9] & 0xa0) == 0 && dry ? Idle : Other;
            int type = *(ushort*)t;
            if (Main.tileFrameImportant[type])
                return dry && !TileID.Sets.Torches[type] && !TileID.Sets.FrameImportantEchoCulling[type] ? Still : Other;
            if (!Main.tileSolid[type])
                return Other;
            if (!dry)
                return Wet;
            return TileID.Sets.Falling[type] ? Falling : Plain;
        }

        // the game's test of a block that can fall: nothing under it, and nothing on it that holds it
        static bool Falls(int i, int j) =>
            WorldGen.BlockBelowMakesSandFall(i, j) && GameAllowsSandfall((byte*)Tiles.At(i, j - 1));

        // (FNAPatches) WorldGen.AllowsSandfall and TileFrameCosmetic take a pointer to the tile's record once TileRecords has
        // converted them; this runtime is compiled against the game as shipped (Tile a class), so these pass a placeholder that
        // FramesPass (after the conversion) replaces with the pointer they are given
        static bool GameAllowsSandfall(byte* tile) => WorldGen.AllowsSandfall((Tile)null);
        static void GameCosmetic(int i, int j, byte* tile, bool reset) => WorldGen.TileFrameCosmetic(i, j, (Tile)null, reset);

        static bool Inside(int i, int j) => i > 5 && j > 5 && i < Main.maxTilesX - 5 && j < Main.maxTilesY - 5;

        static void Reset()
        {
            live = new Section[0];
            job = taking = null;
            busy = looking = false;
            skip.Clear();
        }

        static void Remove(Section s)
        {
            var now = new List<Section>(live);
            now.Remove(s);
            live = now.ToArray();
            if (job == s)
                job = null;
            busy = live.Length > 0;
        }

        static Section Find(int sx, int sy)
        {
            foreach (var s in live)
                if (s.sx == sx && s.sy == sy)
                    return s;
            return null;
        }

        // every update
        public static void Tick()
        {
            if (!on)
                return;
            mainThread = Environment.CurrentManagedThreadId;
            if (spoiled)
            {
                spoiled = false;
                Reset();
                rest = 600;
            }
            if (Main.gameMenu || Main.netMode != 0 || WorldGen.generatingWorld || Tiles.fastW == 0 || manager != Main.sectionManager)
            {
                if (live.Length > 0 || skip.Count > 0)
                    Reset();
                manager = Main.gameMenu ? null : Main.sectionManager;
                return;
            }
            if (rest > 0)
            {
                rest--;
                return;
            }
            if (job == null)
            {
                if (!Main.sectionManager.AnyUnfinishedSections || (idle++ & 7) != 0)
                    return;
                Pick();
                if (job == null)
                    return;
            }
            Work(job, Stopwatch.GetTimestamp() + (Soon(job) ? 3 * slice : slice));
        }

        // the view with the game's margin, tiles, moved to where the player will be in two seconds
        static void View(out int x0, out int y0, out int x1, out int y1, int updates)
        {
            var v = Main.LocalPlayer.velocity;
            float m = Main.offScreenRange;
            float x = Main.screenPosition.X + v.X * updates, y = Main.screenPosition.Y + v.Y * updates;
            x0 = (int)((x - m) / 16) - 1;
            y0 = (int)((y - m) / 16) - 1;
            x1 = (int)((x + Main.screenWidth + m) / 16) + 2;
            y1 = (int)((y + Main.screenHeight + m) / 16) + 5;
        }

        // how far a section's tiles are from a rectangle of tiles
        static int Far(int sx, int sy, int x0, int y0, int x1, int y1)
        {
            int ax = sx * 200, ay = sy * 150;
            int dx = Math.Max(0, Math.Max(ax - x1, x0 - (ax + 199)));
            int dy = Math.Max(0, Math.Max(ay - y1, y0 - (ay + 149)));
            return dx + dy;
        }

        // the view reaches the section within a second and a half as the player moves now
        static bool Soon(Section s)
        {
            View(out int x0, out int y0, out int x1, out int y1, 90);
            return Far(s.sx, s.sy, x0, y0, x1, y1) == 0;
        }

        static void Pick()
        {
            View(out int x0, out int y0, out int x1, out int y1, 120);
            var m = Main.sectionManager;
            int best = int.MaxValue, bx = 0, by = 0;
            int fromX = Math.Max(0, (x0 - 150) / 200), toX = Math.Min((Main.maxTilesX - 1) / 200, (x1 + 150) / 200);
            int fromY = Math.Max(0, (y0 - 110) / 150), toY = Math.Min((Main.maxTilesY - 1) / 150, (y1 + 110) / 150);
            for (int sx = fromX; sx <= toX; sx++)
                for (int sy = fromY; sy <= toY; sy++)
                {
                    if (!m.SectionLoaded(sx, sy) || m.SectionFramed(sx, sy) || skip.Contains(sx + (sy << 12)) || Find(sx, sy) != null)
                        continue;
                    int far = Far(sx, sy, x0, y0, x1, y1);
                    if (far < best)
                    {
                        best = far; bx = sx; by = sy;
                    }
                }
            if (best == int.MaxValue)
                return;
            if (live.Length >= Most)
            {
                // room for it: the section farthest from the view goes back to the game
                Section worst = null;
                int most = best;
                foreach (var s in live)
                {
                    int far = Far(s.sx, s.sy, x0, y0, x1, y1);
                    if (far > most)
                    {
                        most = far; worst = s;
                    }
                }
                if (worst == null)
                    return;
                Remove(worst);
            }
            var n = new Section { sx = bx, sy = by, echo = Main.ShouldShowInvisibleBlocksAndWalls() };
            WorldGen.GetSectionBounds(bx, by, bx, by, out int tx0, out int tx1, out int ty0, out int ty1);
            n.x0 = tx0 - 1; n.y0 = ty0 - 1;
            n.w = tx1 - tx0 + 2; n.h = ty1 - ty0 + 2;
            if (n.w <= 0 || n.h <= 0)
                return;
            n.was = new byte[n.w * n.h];
            var now = new List<Section>(live) { n };
            live = now.ToArray();
            job = n;
            busy = true;
        }

        static void Work(Section s, long until)
        {
            int k = s.next, n = s.w * s.h;
            int i = s.x0 + k / s.h, j = s.y0 + k % s.h;
            looking = true;
            try
            {
                while (k < n)
                {
                    parent = k;
                    byte* t = (byte*)Tiles.At(i, j);
                    if (t != null)
                    {
                        int c = Inside(i, j) ? Class(t) : -1;
                        if (c == Plain || c == Falling)
                        {
                            try { GameCosmetic(i, j, t, true); }
                            catch { }
                        }
                        if (c <= Falling)
                            s.was[k] = c < 0 ? Outside : (byte)(c + 1);
                    }
                    Framing.WallFrame(i, j, true);
                    k++;
                    if (++j == s.y0 + s.h)
                    {
                        j = s.y0;
                        i++;
                    }
                    if ((k & 31) == 0 && Stopwatch.GetTimestamp() >= until)
                        break;
                }
            }
            finally
            {
                looking = false;
            }
            s.next = k;
            if (s.calls.Count > MostCalls)
            {
                skip.Add(s.sx + (s.sy << 12));
                Remove(s);
            }
            else if (k >= n)
            {
                s.complete = true;
                job = null;
                Ahead++;
            }
        }

        // the game's frame routine, as it starts: true when the call is dealt with here
        public static bool Enter(int i, int j, bool resetFrame, bool noBreak)
        {
            if (!busy)
                return false;
            if (Environment.CurrentManagedThreadId != mainThread)
            {
                spoiled = true;
                return false;
            }
            if (!looking)
            {
                Mark(i, j);
                return false;
            }
            if (!Inside(i, j))
                return true;
            byte* t = (byte*)Tiles.At(i, j);
            if (t == null)
                return true;
            int c = Class(t);
            if (c == Idle)
                return true;
            if (c != Plain)
                job.calls.Add(new Call { at = parent, i = i, j = j, reset = resetFrame, noBreak = noBreak });
            if (c == Plain || c == Falling || c == Wet)
            {
                // its picture now: the caller reads what this leaves (how it merged)
                try { GameCosmetic(i, j, t, resetFrame); }
                catch { }
            }
            return true;
        }

        // the game's cactus routine, as it starts (the picture routine calls it for a cactus beside
        // the tile it works on; it breaks a cactus that cannot stand): held back like a frame call
        public static bool Cactus(int i, int j)
        {
            if (!looking || Environment.CurrentManagedThreadId != mainThread)
                return false;
            job.calls.Add(new Call { at = parent, i = i, j = j, cactus = true });
            return true;
        }

        // a frame was asked for outside the pass ahead: the tile is the game's again
        static void Mark(int i, int j)
        {
            foreach (var s in live)
            {
                int x = i - s.x0, y = j - s.y0;
                if ((uint)x < (uint)s.w && (uint)y < (uint)s.h)
                {
                    int k = x * s.h + y;
                    s.was[k] = 0;
                }
            }
        }

        // the game's pass over sections starts
        public static void Begin(int sx, int sy, int ex, int ey)
        {
            taking = null;
            if (!busy)
                return;
            if (spoiled || WorldGen.generatingWorld || manager != Main.sectionManager || Environment.CurrentManagedThreadId != mainThread)
            {
                spoiled = true;
                return;
            }
            for (int x = sx; x <= ex; x++)
                for (int y = sy; y <= ey; y++)
                {
                    var s = Find(x, y);
                    if (s == null)
                        continue;
                    if (sx == ex && sy == ey && s.complete && s.echo == Main.ShouldShowInvisibleBlocksAndWalls())
                    {
                        taking = s;
                        replay = left = 0;
                        nextAt = s.calls.Count > 0 ? s.calls[0].at : int.MaxValue;
                        Array.Clear(leftBy, 0, leftBy.Length);
                        mapAt = slow ? 0 : 1;
                        takeAt = Stopwatch.GetTimestamp();
                        continue;
                    }
                    Late++;
                    if (slow)
                        Console.Error.WriteLine($"[frames] section {x},{y} left to the game ({s.next * 100L / (s.w * s.h)} % done ahead)");
                    Remove(s);
                }
        }

        // the game's pass, for each tile
        public static void Reframe(int i, int j, bool resetFrame)
        {
            var s = taking;
            if (s != null)
            {
                if (mapAt == 0)
                    mapAt = Stopwatch.GetTimestamp();
                int x = i - s.x0, y = j - s.y0;
                if ((uint)x < (uint)s.w && (uint)y < (uint)s.h)
                {
                    int k = x * s.h + y;
                    if (k >= nextAt)
                        Held(s, k, false);
                    if (Ready(s.was[k], i, j))
                    {
                        if (k == nextAt)
                            Held(s, k, true);
                        return;
                    }
                }
                left++;
                if (slow)
                {
                    byte* t = (byte*)Tiles.At(i, j);
                    if (t != null)
                        leftBy[Class(t)]++;
                }
            }
            WorldGen.Reframe(i, j, resetFrame);
        }

        // a tile done ahead that is still what it was then
        static bool Ready(int was, int i, int j)
        {
            if (was == 0)
                return false;
            if (was == Outside)
                return true;
            byte* t = (byte*)Tiles.At(i, j);
            if (t == null || Class(t) != was - 1)
                return false;
            return was != Falling + 1 || !Falls(i, j);
        }

        // the calls held back for the tiles before k are dropped (the game's routine has made them
        // itself); with make, those of k are made
        static void Held(Section s, int k, bool make)
        {
            var calls = s.calls;
            while (replay < calls.Count && calls[replay].at < k)
                replay++;
            while (make && replay < calls.Count && calls[replay].at == k)
            {
                var c = calls[replay++];
                if (c.cactus)
                    WorldGen.CactusFrame(c.i, c.j);
                else if (!Settled(c.i, c.j))
                    WorldGen.TileFrame(c.i, c.j, c.reset, c.noBreak);
            }
            nextAt = replay < calls.Count ? calls[replay].at : int.MaxValue;
        }

        // a dry block that can fall and does not: the game's routine would make its picture again
        // and no more (it was made when the call was held back)
        static bool Settled(int i, int j)
        {
            byte* t = (byte*)Tiles.At(i, j);
            return t != null && Inside(i, j) && Class(t) == Falling && !Falls(i, j);
        }

        // the game's pass ends
        public static void End()
        {
            var s = taking;
            if (s == null)
                return;
            taking = null;
            Taken++;
            if (slow)
                Console.Error.WriteLine($"[frames] section {s.sx},{s.sy} taken: {(Stopwatch.GetTimestamp() - takeAt) * 1000.0 / Stopwatch.Frequency:F1} ms (the map {(mapAt - takeAt) * 1000.0 / Stopwatch.Frequency:F1}), " +
                                        $"{left} tiles left to the game (empty {leftBy[Idle]}, blocks {leftBy[Plain]}, objects {leftBy[Still]}, " +
                                        $"falling {leftBy[Falling]}, wet {leftBy[Wet]}, other {leftBy[Other]}), {s.calls.Count} held calls");
            Remove(s);
        }
    }
}
