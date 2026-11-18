using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework;
using MonoMod.InlineRT;
using MonoMod;
using ReLogic.Content;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System;
using Terraria.GameContent.Drawing;
using Terraria.GameContent;
using Terraria.Graphics;
using Terraria;

#pragma warning disable 1591, 0649, 0169, 0414

// The tile cache (wild1walker, portmaster-terraria, MIT: tools/trpatch/TileCachePatch.cs and runtime/TileCache.cs,
// TileCacheParts.cs, TileCacheSplit.cs): what the game's routine for one tile puts in the tile batch is kept per tile with what
// it was worked out from (the tile and the eight around it, the light, the animation frame), and put in the batch again the
// next frame if none of that changed; the solid pass's questions are asked on a second core. A kind of tile is used only after
// its answers agreed 64 times over 30 frames, one tile in 64 is still worked out by the game and compared for ever, and the
// particles a tile gives off are made by the game's own routine with the game's random numbers. The pass applies only to the
// tile routines of 1.4.5.8 (a sum over their code as shipped). (FNAPatches) the tiles are read as TileRecords' 14-byte records.
// TR_TILECACHE=0: as before; TR_TILECACHE_AUDIT=1 checks. TERRARIA_TILECACHE=0 leaves the pass out when patching.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_SPEED") == "0" || Environment.GetEnvironmentVariable("TERRARIA_TILECACHE") == "0")
				return;
			// the sum is taken of the game's code as shipped: here, before MonoMod weaves anything
			TerrariaPatches.TileConvert.TileCachePass.Look(MonoModRule.Modder.Module);
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.TileCache, "tilecache", m => TerrariaPatches.TileConvert.TileCachePass.Apply(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
    // The game works out every visible tile's sprites again each frame: which frame of its sheet,
    // where, what colour, which layer. For a plain block all of that follows from the block, the
    // blocks around it and the light on it, and those seldom change. What the game's routine for
    // one tile put in the tile batch is kept here, per tile, with what it was worked out from; a
    // frame later, with the same blocks and light, the same is put in the batch again without
    // the routine. (trpatch's TileCachePatch has the hooks.)
    //
    // Nothing is assumed about a kind of block. A kind is first watched: its tiles are worked out
    // by the game every frame and compared with what was kept, and only after enough agreement,
    // over enough frames, is the kept answer used. After that one tile in 64 is still worked out
    // by the game each frame and compared, and a kind that ever differs goes back to the game for
    // good ("differed" in the statistics: it has to stay 0). A tile whose routine did anything
    // the cache does not follow is always left to the game.
    //
    // The particles a tile gives off are not kept: the game's routine for them is called as the
    // game calls it, by the same rule and with the same values, so the game's random numbers are
    // drawn exactly as before.
    //
    // "The same blocks" is known without looking at the nine of them for each tile. As a pass
    // starts, the blocks it can draw from are compared, a column at a time (the world is kept by
    // columns), with a copy of them kept here; where one differs the copy takes it, and the nine
    // tiles around it are stamped with the frame's number. A kept answer holds as long as its
    // tile has no stamp later than the answer: the copy was the world when the answer was worked
    // out (that is checked as the routine returns), nothing in it was rewritten since, and it has
    // just been found to be the world again. What a tile's turn then reads is a 16-byte key and
    // a stamp, in the order the game walks the tiles.
    //
    // The game's walk does a little for each tile before its routine: it sets a layer, looks for
    // liquid behind the tile, picks the tile's case. Early is asked first, and where the kept
    // answer is "whole" it puts the answer back and the walk goes on to the next tile. Whole
    // means that all the walk did before the routine came to nothing: the tile and the four
    // beside it held no liquid (the liquid routine then draws nothing), the tile's case noted
    // nothing (the two routines a case can call tell), and the routine began by setting its
    // layer (so the walk's own layers were never used). Which case a tile comes to follows from
    // its kind, its frame and one setting, and those are part of what an answer is kept by.
    //
    // TR_TILECACHE=0: off. TR_TILECACHE_AUDIT=1 (tests): nothing is put back; every kept answer
    // is compared with the game's at every frame, and at each tile's turn the copy of the nine
    // blocks with the blocks ("late": a block changed after the pass began; it has to stay 0).
    public static unsafe partial class TileCache
    {
        // (FNAPatches) the game's routines that take a tile take a pointer to its record once TileRecords has converted them;
        // this runtime is compiled against the game as shipped (Tile a class), so these two pass a placeholder that
        // TileCachePass (after the conversion) replaces with the pointer they are given
        static void GameParticles(TileDrawing td, int j, int i, byte* tile, ushort type, short fx, short fy, Color light)
        {
            td.DrawTiles_EmitParticles(j, i, (Tile)null, type, fx, fy, light);
        }

        static Color GameLight(TileDrawing td, int j, int i, byte* tile, ushort type, short fx, short fy, Color light)
        {
            return td.DrawTiles_GetLightOverride(j, i, (Tile)null, type, fx, fy, light);
        }

        public static bool Recording;
        static readonly bool on = Environment.GetEnvironmentVariable("TR_TILECACHE") != "0";
        static readonly bool audit = Environment.GetEnvironmentVariable("TR_TILECACHE_AUDIT") == "1";
        const int Mask = 63, Slots = (Mask + 1) * (Mask + 1), MaxOps = 12, MaxSprites = 10, SpriteSize = 64, TileSize = 14;
        const byte Unknown = 0, Watched = 1, Proven = 2, Differs = 3, Never = 4;
        const int Agree = 64, Frames = 30;
        const byte Kept = 1, Game = 2;                  // an answer is kept, or the tile is the game's
        const byte Whole = 1;                           // flags: the answer is all the walk does for the tile

        // what an answer was worked out from, but for the blocks; rec: the frame it was worked out in
        [StructLayout(LayoutKind.Sequential)]
        struct Key
        {
            public uint tag, light, rec;
            public ushort type;
            public byte kind, flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Cell
        {
            public int frame;
            public short fx, fy;
            public byte ops, sprites;
            public fixed uint op[MaxOps];               // top byte: 0 a layer, 1 a sprite, 2 the dark strip
            public int spare;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Shadow
        {
            public uint tag;
            public fixed byte tile[TileSize];
        }

        // by tile, rows of 64: keys, cells, stamps, a tile's first sprite and its others; one more
        // of each at the end to work an answer out in for comparing. The copy of the blocks goes
        // by columns.
        static Key* keys;
        static Cell* cells;
        static uint* near;
        static byte* first, rest;
        static Shadow* shadow;
        static byte[] state;
        static byte[] agreed;
        static int[] since;
        static readonly int[] index = new int[MaxSprites];
        static bool fits, fresh, ok, area, passSolid, passTargets, everyFrame, active;
        static long lastKey = -1;
        static uint floor = 1;                          // answers from before this frame are not used
        static int frame;
        static int rx0, rx1, ry0, ry1;                  // the blocks compared as this pass began

        // the tile Early was last asked about, what it found, and whether its case noted anything
        public static bool Noted;
        static int eX = -1, eY = -1, eR4 = -1;
        static bool eKnown, eSame;
        static uint eLight;

        // what is being recorded
        static Cell* rec;
        static int recX, recY, recType;
        static uint recLight;
        static byte* recTile;
        static Texture2D recTexture;
        static bool bad, emitted, lit;
        static ulong random;

        public static long Replays, Wholes, Records, Checks, Differed, Passed, Late, Moved;
        // TR_TILECACHE_TIME=1 (tests): the time in here, by what was done with the tile
        static readonly bool timed = Environment.GetEnvironmentVariable("TR_TILECACHE_TIME") == "1";
        static long tReplay, tRecord, tGame, nGame, tKey, nKey, tEmit, nEmit, tOps, nOps, sOps, sSprites;
        static bool sample;
        static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();
        static int proven, differing;

        // TileDrawing.Draw starts
        public static void Pass(bool solid, bool targets)
        {
            fresh = true;
            ok = area = eKnown = false;
            eX = eY = -1;
            passSolid = solid;
            passTargets = targets;
            if (solid)
            {
                frame++;
                if (split)
                    SolidPass();
            }
        }

        static byte* Room(int bytes)
        {
            byte* p = (byte*)Marshal.AllocHGlobal(bytes + 64);
            p += (64 - (int)((ulong)p & 63)) & 63;
            for (int i = 0; i < bytes; i++)
                p[i] = 0;
            return p;
        }

        // TileDrawing.Draw has the tiles it is going to walk: rows y0 to y1 + 3 of columns x0 - 2 to x1 + 1
        public static void Area(int x0, int x1, int y0, int y1)
        {
            if (!passSolid)
            {
                if (ping != 0 || split)
                    Ahead(x0, x1, y0, y1);
                return;
            }
            if (ping != 0)
                Ping();
            if (!on)
                return;
            if (split)
                SolidArea(x0, x1, y0, y1);
            if (keys == null)
            {
                fits = sizeof(TileBatch.SpriteData) == SpriteSize && Tiles.RecordSize == TileSize /* (FNAPatches) the record block's */ && sizeof(Key) == 16 && sizeof(Cell) == 64 && sizeof(Shadow) == 20;
                keys = (Key*)Room(sizeof(Key) * (Slots + 1));
                cells = (Cell*)Room(sizeof(Cell) * (Slots + 1));
                near = (uint*)Room(sizeof(uint) * Slots);
                first = Room(SpriteSize * (Slots + 1));
                rest = Room(SpriteSize * (MaxSprites - 1) * (Slots + 1));
                shadow = (Shadow*)Room(sizeof(Shadow) * Slots);
                state = new byte[TextureAssets.Tile.Length];
                agreed = new byte[state.Length];
                since = new int[state.Length];
            }
            int w = Tiles.fastW, h = Tiles.fastH;
            if (!fits || w == 0 || w > 65536 || h > 65536)
                return;
            // a tile further around, for the eight about each tile walked
            rx0 = Math.Max(x0 - 3, 0);
            rx1 = Math.Min(x1 + 2, w - 1);
            ry0 = Math.Max(y0 - 1, 0);
            ry1 = Math.Min(y1 + 4, h - 1);
            if (rx1 < rx0 || ry1 < ry0 || rx1 - rx0 > Mask || ry1 - ry0 > Mask)
                return;
            area = true;
            uint now = (uint)frame;
            for (int x = rx0; x <= rx1; x++)
            {
                byte* t = (Tiles.data + (long)(x * h + ry0) * TileSize);
                Shadow* column = shadow + ((x & Mask) << 6);
                for (int y = ry0; y <= ry1; y++, t += TileSize)
                {
                    Shadow* s = column + (y & Mask);
                    uint tag = (uint)x | (uint)y << 16;
                    if (s->tag == tag && Same14(s->tile, t))
                        continue;
                    Moved++;
                    if (split)
                        SolidChanged((y & Mask) << 6 | (x & Mask));
                    s->tag = tag;
                    *(ulong*)s->tile = *(ulong*)t;
                    *(uint*)(s->tile + 8) = *(uint*)(t + 8);
                    *(ushort*)(s->tile + 12) = *(ushort*)(t + 12);
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        uint* row = near + (((y + dy) & Mask) << 6);
                        row[(x - 1) & Mask] = now;
                        row[x & Mask] = now;
                        row[(x + 1) & Mask] = now;
                    }
                }
            }
            if (split)
                SolidCompared();
        }

        static void Start(TileDrawing td, Vector2 offset)
        {
            fresh = false;
            if (!area)
                return;
            everyFrame = Lighting.UpdateEveryFrame;
            active = td._isActiveAndNotPaused;
            Player p = td._perspectivePlayer;
            ok = !Lighting.NotRetro && Main.shimmerAlpha == 0f && Main.tileBatch != null
                && !p.dangerSense && !p.findTreasure && !p.biomeSight
                && offset.X == (float)(int)offset.X && offset.Y == (float)(int)offset.Y;
            float quality = Main.gfxQuality;
            long key = (passTargets ? 1 : 0) | (everyFrame ? 2 : 0) | (td._shouldShowInvisibleBlocks ? 4 : 0) | (TileDrawingBase.DrawOwnBlacks ? 8 : 0)
                | (Main.SettingsEnabled_TilesSwayInWind ? 16 : 0)
                | (long)(*(uint*)&quality) << 8;
            if (key != lastKey)
            {
                lastKey = key;
                floor = (uint)frame;
            }
        }

        // the game's rule for a tile's particles this frame
        static bool Emits(int x, int y) =>
            active && (!everyFrame || new Terraria.Utilities.FastRandom(Main.TileFrameSeed).WithModifier(x, y).Next(4) == 0);

        static bool Dry(byte* t)
        {
            int h = Tiles.fastH;
            return t[4] == 0 && (t - TileSize)[4] == 0 && (t + TileSize)[4] == 0 && (t - h * TileSize)[4] == 0 && (t + h * TileSize)[4] == 0;
        }

        // TileDrawing.Draw, for each tile of the pass's layer, before anything is done for it;
        // true: the tile is drawn, go on to the next
        public static bool Early(TileDrawing td, Vector2 screen, Vector2 offset, int x, int y)
        {
            Noted = false;
            eX = x;
            eY = y;
            eKnown = false;
            if (fresh)
                Start(td, offset);
            if (!ok || x <= rx0 || x >= rx1 || y <= ry0 || y >= ry1)
                return false;
            byte* t = Tiles.data + (long)(x * Tiles.fastH + y) * TileSize;
            int type = (*(ushort*)t);
            if ((uint)type >= (uint)state.Length)
                return false;
            byte s = state[type];
            if (s != Watched && s != Proven)
                return false;
            int at = (y & Mask) << 6 | (x & Mask);
            uint light;
            bool same, keptWhole;
            int r4 = -1;
            // the answers the tiles' thread read ahead (TileCacheSplit.cs), where it did
            byte f = split ? Answer(x, y, at, type) : (byte)0;
            if ((f & Done) != 0)
            {
                light = vLight[at];
                same = (f & Pre) != 0 && near[at] <= vRec[at];
                keptWhole = (f & KeptWhole) != 0;
                r4 = (f & R4) != 0 ? 1 : 0;
                SplitUsed++;
                if (splitAudit)
                    Compare(f, at, x, y, type);
            }
            else
            {
                Key* k = keys + at;
                light = Lighting.GetColor(x, y).PackedValue;
                same = k->tag == ((uint)x | (uint)y << 16) && k->type == type && k->light == light && k->rec >= floor && near[at] <= k->rec
                    && cells[at].frame == Main.tileFrame[type];
                keptWhole = k->kind == Kept && (k->flags & Whole) != 0;
            }
            eLight = light;
            eSame = same;
            eR4 = r4;
            eKnown = true;
            if (!same || s != Proven || audit || !keptWhole || ((x * 7 + y * 13 + frame) & 63) == 0
                || !TextureAssets.Tile[type].IsLoaded)
                return false;
            Replays++;
            Wholes++;
            Replay(td, at, t, screen, offset, x, y, light, type, r4);
            return true;
        }

        // TileDrawing.Draw, for each tile it has DrawSingleTile draw
        public static void Draw(TileDrawing td, Vector2 screen, Vector2 offset, int x, int y)
        {
            if (!timed)
            {
                One(td, screen, offset, x, y);
                return;
            }
            long replays = Replays, records = Records, t = System.Diagnostics.Stopwatch.GetTimestamp();
            One(td, screen, offset, x, y);
            t = System.Diagnostics.Stopwatch.GetTimestamp() - t;
            if (Replays != replays)
                tReplay += t;
            else if (Records != records)
                tRecord += t;
            else
            {
                tGame += t;
                nGame++;
            }
        }

        static void One(TileDrawing td, Vector2 screen, Vector2 offset, int x, int y)
        {
            if (fresh)
                Start(td, offset);
            // the tile and the eight around it have to be among the blocks compared
            if (!ok || x <= rx0 || x >= rx1 || y <= ry0 || y >= ry1)
            {
                td.DrawSingleTile(screen, offset, x, y);
                return;
            }
            byte* t = Tiles.data + (long)(x * Tiles.fastH + y) * TileSize;
            int type = (*(ushort*)t);
            if ((uint)type >= (uint)state.Length)
            {
                td.DrawSingleTile(screen, offset, x, y);
                return;
            }
            byte s = state[type];
            if (s == Unknown)
            {
                s = state[type] = Listed(type) ? Watched : Never;
                since[type] = frame;
            }
            if (s >= Differs)
            {
                td.DrawSingleTile(screen, offset, x, y);
                return;
            }
            int at = (y & Mask) << 6 | (x & Mask);
            Key* k = keys + at;
            sample = timed && ((x + y * 3) & 7) == 0;
            long started = sample ? Now() : 0;
            uint light;
            bool same;
            int r4 = -1;
            if (eKnown && eX == x && eY == y)
            {
                light = eLight;
                same = eSame;
                r4 = eR4;
            }
            else
            {
                light = Lighting.GetColor(x, y).PackedValue;
                same = k->tag == ((uint)x | (uint)y << 16) && k->type == type && k->light == light && k->rec >= floor && near[at] <= k->rec
                    && cells[at].frame == Main.tileFrame[type];
            }
            if (sample)
            {
                tKey += Now() - started;
                nKey++;
            }
            if (audit && !Still(x, y))
                Late++;
            if (same && k->kind == Game)
            {
                Passed++;
                td.DrawSingleTile(screen, offset, x, y);
                return;
            }
            if (same && s == Proven && !audit && ((x * 7 + y * 13 + frame) & 63) != 0)
            {
                Replays++;
                Replay(td, at, t, screen, offset, x, y, light, type, r4);
                return;
            }
            int r = same ? Slots : at;
            // the walk came from Early to here for this tile with nothing noted and no liquid by
            bool whole = eX == x && eY == y && !Noted && Dry(t);
            if (!Record(td, r, t, screen, offset, x, y, light, type, whole))
            {
                // a block changed while the routine ran: neither answer is one to keep
                k->rec = 0;
                return;
            }
            if (!same)
                return;
            Checks++;
            if (Equal(r, at))
            {
                if (s == Watched && (agreed[type] >= Agree || ++agreed[type] >= Agree) && frame - since[type] >= Frames)
                {
                    state[type] = Proven;
                    proven++;
                }
                return;
            }
            Differed++;
            if (s == Proven)
                proven--;
            state[type] = Differs;
            if (++differing <= 8)
                Console.Error.WriteLine($"[trpatch] tile cache: tile kind {type} at {x},{y} was drawn differently from the same blocks and light ({Describe(at)} then {Describe(r)}); left to the game");
            Copy(r, at);
        }
    }
}

namespace TrPatches
{
    // TileCache: recording a tile, putting a kept one back, and the calls from the game's code
    public static unsafe partial class TileCache
    {
        // kinds the routine draws from more than the blocks and the light (animation, time, the
        // pointer), by its own special cases
        static readonly int[] left = { 19, 51, 61, 72, 80, 83, 84, 114, 129, 136, 160, 171, 272, 314, 323, 442, 481, 482, 483, 518, 541, 631, 692, 697,
            703, 723, 724, 726, 751, 752 };

        static bool Listed(int type) =>
            Main.tileSolid[type] && !Main.tileFrameImportant[type] && Main.tileGlowMask[type] == -1 && !Main.tileFlame[type]
            && !Terraria.ID.TileID.Sets.HasOutlines[type] && Array.IndexOf(left, type) < 0;

        static bool Same14(byte* a, byte* b) =>
            *(ulong*)a == *(ulong*)b && *(uint*)(a + 8) == *(uint*)(b + 8) && *(ushort*)(a + 12) == *(ushort*)(b + 12);

        // the copy of the tile and of the eight around it is still what the world has
        static bool Still(int x, int y)
        {
            int h = Tiles.fastH;
            for (int i = x - 1; i <= x + 1; i++)
            {
                byte* t = (Tiles.data + (long)(i * h + y - 1) * TileSize);
                Shadow* column = shadow + ((i & Mask) << 6);
                for (int j = y - 1; j <= y + 1; j++, t += TileSize)
                {
                    Shadow* e = column + (j & Mask);
                    if (e->tag != ((uint)i | (uint)j << 16) || !Same14(e->tile, t))
                        return false;
                }
            }
            return true;
        }

        static byte* SpriteAt(int at, int i) => i == 0 ? first + at * SpriteSize : rest + (at * (MaxSprites - 1) + i - 1) * SpriteSize;

        static void Copy(int from, int to)
        {
            keys[to] = keys[from];
            cells[to] = cells[from];
            for (int i = 0; i < cells[from].sprites; i++)
                Buffer.MemoryCopy(SpriteAt(from, i), SpriteAt(to, i), SpriteSize, SpriteSize);
        }

        // where the game's two generators of random numbers are
        static ulong Rand(TileDrawing td)
        {
            var a = td._rand;
            var b = Main.rand;
            ulong r = 0;
            if (a != null)
                r = (ulong)a.inext << 32 | (uint)a.SeedArray[a.inext % 56];
            if (b != null)
                r = r * 1099511628211 + ((ulong)b.inext << 32 | (uint)b.SeedArray[b.inext % 56]);
            return r;
        }

        // has the game's routine draw the tile and keeps what it did; false: a block changed meanwhile
        static bool Record(TileDrawing td, int at, byte* t, Vector2 screen, Vector2 offset, int x, int y, uint light, int type, bool whole)
        {
            Records++;
            Key* k = keys + at;
            Cell* r = cells + at;
            k->tag = (uint)x | (uint)y << 16;
            k->light = light;
            k->rec = (uint)frame;
            k->type = (ushort)type;
            k->flags = 0;
            r->frame = Main.tileFrame[type];
            r->fx = r->fy = 0;
            r->ops = r->sprites = 0;
            rec = r;
            recX = x;
            recY = y;
            recType = type;
            recLight = light;
            recTile = t;
            recTexture = TextureAssets.Tile[type].Value;
            bad = emitted = lit = false;
            random = Rand(td);
            Recording = true;
            try { td.DrawSingleTile(screen, offset, x, y); }
            finally { Recording = false; }
            // the routine has to have come to the tile's light, to have drawn random numbers for
            // the particles only, and to have made those by the rule Emits repeats
            if (!lit || Rand(td) != random || emitted != Emits(x, y))
                bad = true;
            if (!bad)
            {
                // the sprites, with their places kept from the tile's own: whole and half pixels
                // only, so that adding the place back gives the game's number exactly
                TileBatch.SpriteData[] queue = Main.tileBatch._spriteDataQueue;
                float bx = (float)(x * 16 - (int)screen.X) + offset.X, by = (float)(y * 16 - (int)screen.Y) + offset.Y;
                for (int i = 0; i < r->sprites; i++)
                {
                    TileBatch.SpriteData* d = (TileBatch.SpriteData*)SpriteAt(at, i);
                    *d = queue[index[i]];
                    float ox = d->Destination.X - bx, oy = d->Destination.Y - by;
                    if (!(Math.Abs(ox) <= 2048f && Math.Abs(oy) <= 2048f) || ox * 2f != (float)(int)(ox * 2f) || oy * 2f != (float)(int)(oy * 2f))
                    {
                        bad = true;
                        break;
                    }
                    d->Destination.X = ox;
                    d->Destination.Y = oy;
                }
            }
            k->kind = bad ? Game : Kept;
            if (whole && !bad && r->ops > 0 && r->op[0] >> 24 == 0)
                k->flags = Whole;
            if (bad)
            {
                r->fx = r->fy = 0;
                r->ops = r->sprites = 0;
            }
            return Still(x, y);
        }

        static bool Equal(int one, int other)
        {
            Cell* a = cells + one, b = cells + other;
            if (keys[one].kind != keys[other].kind || keys[one].flags != keys[other].flags
                || a->ops != b->ops || a->sprites != b->sprites || a->fx != b->fx || a->fy != b->fy)
                return false;
            for (int i = 0; i < a->ops; i++)
                if (a->op[i] != b->op[i])
                    return false;
            for (int i = 0; i < a->sprites; i++)
            {
                ulong* p = (ulong*)SpriteAt(one, i), q = (ulong*)SpriteAt(other, i);
                for (int j = 0; j < SpriteSize / 8; j++)
                    if (p[j] != q[j])
                        return false;
            }
            return true;
        }

        static string Describe(int at) =>
            keys[at].kind == Game ? "not followed" : $"{cells[at].ops} steps with {cells[at].sprites} sprites, frame {cells[at].fx},{cells[at].fy}";

        // r4: whether the particles' draw of the tile came out 0 (as the tiles' thread read it), -1 unknown
        static void Replay(TileDrawing td, int at, byte* t, Vector2 screen, Vector2 offset, int x, int y, uint light, int type, int r4)
        {
            long started = sample ? Now() : 0;
            Cell* c = cells + at;
            Texture2D texture = TextureAssets.Tile[type].Value;
            if (r4 < 0 ? Emits(x, y) : active && (!everyFrame || r4 == 1))
                GameParticles(td, y, x, t, (ushort)type, c->fx, c->fy, new Color { PackedValue = light });
            if (sample)
            {
                long now = Now();
                tEmit += now - started;
                nEmit++;
                started = now;
            }
            TileBatch batch = Main.tileBatch;
            float bx = (float)(x * 16 - (int)screen.X) + offset.X, by = (float)(y * 16 - (int)screen.Y) + offset.Y;
            for (int i = 0; i < c->ops; i++)
            {
                uint op = c->op[i];
                switch (op >> 24)
                {
                    case 0:
                        batch.SetLayerStack(op);
                        break;
                    case 1:
                        int to = batch.GetNextSpriteIndex(texture);
                        fixed (TileBatch.SpriteData* d = &batch._spriteDataQueue[to])
                        {
                            *d = *(TileBatch.SpriteData*)SpriteAt(at, (int)(op & 0xff));
                            d->Destination.X += bx;
                            d->Destination.Y += by;
                        }
                        break;
                    default:
                        td.drawBlackHelper.DrawBlack(x, y);
                        break;
                }
            }
            if (sample)
            {
                tOps += Now() - started;
                nOps++;
                sOps += c->ops;
                sSprites += c->sprites;
            }
        }

        // TileBatch.SetLayerStack, while recording
        public static void Layer(TileBatch batch, uint layer)
        {
            if (batch != Main.tileBatch || layer >= 1u << 24 || rec->ops >= MaxOps)
                bad = true;
            else
                rec->op[rec->ops++] = layer;
        }

        // TileBatch.GetNextSpriteIndex, while recording: the place it gave
        public static void Sprite(int at, TileBatch batch, Texture2D texture)
        {
            if (batch != Main.tileBatch || texture != recTexture || rec->ops >= MaxOps || rec->sprites >= MaxSprites)
            {
                bad = true;
                return;
            }
            index[rec->sprites] = at;
            rec->op[rec->ops++] = 1u << 24 | rec->sprites++;
        }

        // DrawSingleTile's call for a dark tile; the strip's own sprite is the helper's business
        public static void Black(ref DrawBlackHelper helper, int x, int y)
        {
            if (!Recording)
            {
                helper.DrawBlack(x, y);
                return;
            }
            if (x != recX || y != recY || rec->ops >= MaxOps)
                bad = true;
            else
                rec->op[rec->ops++] = 2u << 24;
            Recording = false;
            helper.DrawBlack(x, y);
            Recording = true;
        }

        // DrawSingleTile's call for a tile's particles
        public static void Emit(TileDrawing td, int j, int i, byte* tile, ushort type, short fx, short fy, Color light)
        {
            if (!Recording)
            {
                GameParticles(td, j, i, tile, type, fx, fy, light);
                return;
            }
            if (emitted || lit || i != recX || j != recY || tile != recTile || type != recType || light.PackedValue != recLight || Rand(td) != random)
                bad = true;
            emitted = true;
            int ops = rec->ops;
            GameParticles(td, j, i, tile, type, fx, fy, light);
            if (rec->ops != ops)
                bad = true;
            rec->fx = fx;
            rec->fy = fy;
            random = Rand(td);
        }

        // DrawSingleTile's call for a tile's light, which follows the one for its particles
        public static Color Light(TileDrawing td, int j, int i, byte* tile, ushort type, short fx, short fy, Color light)
        {
            if (Recording)
            {
                if (lit || i != recX || j != recY || tile != recTile || type != recType || light.PackedValue != recLight || Rand(td) != random
                    || (emitted && (fx != rec->fx || fy != rec->fy)))
                    bad = true;
                lit = true;
                rec->fx = fx;
                rec->fy = fy;
            }
            return GameLight(td, j, i, tile, type, fx, fy, light);
        }

        public static string Line()
        {
            string times = "";
            if (timed)
            {
                double ns = 1e9 / System.Diagnostics.Stopwatch.Frequency;
                long pair = Now();
                for (int i = 0; i < 1500; i++)
                    sOps += (Now() - Now()) >> 62;
                pair = Now() - pair;
                times = $", {tReplay * ns / Math.Max(1, Replays):F0} ns put back, {tRecord * ns / Math.Max(1, Records):F0} ns worked out, {tGame * ns / Math.Max(1, nGame):F0} ns each of {nGame} the game's"
                    + $", of {nOps} sampled {tKey * ns / Math.Max(1, nKey):F0} ns the key, {tEmit * ns / Math.Max(1, nEmit):F0} ns particles, {tOps * ns / Math.Max(1, nOps):F0} ns {sOps * 100 / Math.Max(1, nOps)}% steps {sSprites * 100 / Math.Max(1, nOps)}% sprites, 3000 clock readings {pair * ns:F0} ns";
            }
            return $"tile cache: {Replays} put back ({Wholes} early), {Records} worked out, {Checks} compared, {Differed} differed, {Passed} left, {Moved} blocks taken{(audit ? $", {Late} late" : "")}{times}; kinds {proven} proven, {differing} differing{SplitLine()}";
        }
    }
}

namespace TrPatches
{
    // TileCache, the solid pass's questions answered on a second core.
    //
    // For each tile of the solid pass, before anything is drawn, Early asks the tile's light and
    // whether the kept answer still holds: about 0.5 ms a frame on the RK3326, most of it reading the
    // light map (an object per tile, in columns) and the cache's records. None of what it reads
    // changes between the frame's first tile pass (the non-solid one) and the solid one: the light
    // map is worked out before both, the world is not written while a frame is drawn, the cache's
    // records are written only by the solid pass. So as the non-solid pass starts (Ahead, from Area),
    // a thread of its own reads all of that for the solid pass's tiles, row by row from the bottom,
    // and the solid pass takes a row's answers where the thread has done it, and asks as before
    // where not (the two meet; neither waits for the other). What only the solid pass can know is
    // still asked there: the blocks changed since the frame before (Area's stamps), the kind's
    // state, the sampled tile.
    //
    // Used only when the solid pass walks the same tiles, with the same rule for old answers
    // (floor), the same seed for the tiles' particles and the same brightness as the thread saw.
    //
    // With TR_TILESPLIT=2 the walk also steps over what the thread found it would pass or put back
    // (Step, where the walk went to its next column): an empty tile, and a tile Early would put back
    // whole, which Step puts back there, in the walk's order. That needs the blocks to be those the
    // thread read: the thread compares the blocks with the cache's copy as it starts, Area compares
    // them again as the solid pass starts, and only when both found the same blocks changed (and
    // the thread left those to the walk) does the walk step over anything.
    //
    // TR_TILESPLIT=1: the answers; 2 (the default with two cores or more): and the steps; 0: off.
    // On the RK3326 the pass's draw came 0.5-0.8 ms shorter (docs/notes.md). TR_TILESPLIT_AUDIT=1 (tests): each answer used
    // and each tile stepped over is also asked as Early and the walk ask it, and compared ("wrong"
    // in the statistics: it has to stay 0).
    //
    // TR_TILESPLIT_PING=<mode> (tests, alone; arm64): what handing work to another core costs: as the
    // solid pass starts, a thread is woken and answers at once while the game thread spins for the
    // answer. 1: the thread sleeps on a futex; 2: on a .NET semaphore (as the split's thread does);
    // 3: it is woken as the non-solid pass starts and spins (5 ms at most) until the solid pass hands
    // it the job.
    public static unsafe partial class TileCache
    {
        static readonly int splitMode = Environment.GetEnvironmentVariable("TR_TILECACHE") == "0"
            || Environment.GetEnvironmentVariable("TR_TILESPLIT_PING") != null ? 0
            : int.TryParse(Environment.GetEnvironmentVariable("TR_TILESPLIT"), out int sm) ? sm
            : Environment.ProcessorCount >= 2 ? 2 : 0;
        static readonly bool split = splitMode > 0;
        static readonly bool splitAudit = split && Environment.GetEnvironmentVariable("TR_TILESPLIT_AUDIT") == "1";
        static readonly bool stepping = splitMode >= 2;
        static readonly int ping = int.TryParse(Environment.GetEnvironmentVariable("TR_TILESPLIT_PING"), out int pm) ? pm : 0;

        [DllImport("libc", SetLastError = true)]
        static extern long syscall(long number, int* addr, int op, int value, IntPtr timeout, IntPtr addr2, int value3);
        const long SysFutex = 98;                       // arm64
        const int FutexWait = 128, FutexWake = 129;     // the private forms

        // what the thread found for a tile: Done (it read the tile: an active one of the solid
        // layer), Pre (the kept answer holds but for the stamps), KeptWhole, R4 (the particles'
        // draw came out 0), Empty (no active tile, and the block unchanged: the walk passes it),
        // Ready (Early would put it back whole, but for what only the solid pass knows)
        const byte Done = 1, Pre = 2, KeptWhole = 4, R4 = 8, Empty = 16, Ready = 32;

        static int* word;                               // the futex: the number of the last job given
        static Thread worker;
        static readonly SemaphoreSlim semaphore = new SemaphoreSlim(0);
        static volatile int busy, spinning, done;

        // the job: the walk's columns [wx0, wx1) and rows [wy0, wy1) (within Early's bounds), the
        // blocks compared [ax0, ax1] x [ay0, ay1], what the answers were read with
        static int wx0, wx1, wy0, wy1, ax0, ax1, ay0, ay1, jArea0, jArea1, jArea2, jArea3, jWidth;
        static uint jFloor, jGen;
        static ulong jSeed;
        static float jBright;
        static bool[] solidLayer;
        static uint* vLight, vRec, vMark;
        static ushort* vType;
        static byte* vFlags;
        // the blocks the thread found changed against the cache's copy, and those Area found
        static int* changedT, changedA;
        static int nChangedT, nChangedA;
        static volatile int compared;                   // the thread's comparison is complete
        static volatile int low, high;                  // rows >= low done by the thread; rows <= high taken by the game
        static int sArea0 = int.MinValue, sArea1, sArea2, sArea3;   // the solid pass's area this frame
        static int curRow = int.MinValue;
        static bool curOk, jobChecked, jobOk, comparedAtArea, sameChanges, stepOn;
        static Exception failed;
        static bool broken;            // the thread failed once: the solid pass asks everything itself from then on

        public static long SplitJobs, SplitRows, SplitTaken, SplitUsed, SplitWrong, SplitSkipped, SplitStale, SplitStepped, SplitStepFrames;
        // the thread's start after it was woken, its job from start to end, the rows it had done when
        // the solid pass first asked, of how many
        static long workTicks, startTicks, givenAt, rowsReady, rowsAll, windowTicks, windows;

        // the split's thread sleeps on a semaphore; the tests' futex (arm64's system call) only for ping
        static void Sleep(int seen)
        {
            if (ping == 1 || ping == 3)
                syscall(SysFutex, word, FutexWait, seen, IntPtr.Zero, IntPtr.Zero, 0);
            else
                semaphore.Wait();
        }

        static void Wake()
        {
            if (ping == 1 || ping == 3)
                syscall(SysFutex, word, FutexWake, 1, IntPtr.Zero, IntPtr.Zero, 0);
            else
                semaphore.Release();
        }

        static void StartWorker()
        {
            word = (int*)Marshal.AllocHGlobal(64);
            *word = 0;
            worker = new Thread(Run) { IsBackground = true, Name = "TR tiles" };
            worker.Start();
        }

        static void Run()
        {
            int last = 0;
            while (true)
            {
                int now = Volatile.Read(ref *word);
                if (now == last)
                {
                    Sleep(now);
                    continue;
                }
                last = now;
                if (ping != 0)
                {
                    last = Pong(now);
                    continue;
                }
                long t = Stopwatch.GetTimestamp();
                startTicks += t - givenAt;
                try { Job(); }
                catch (Exception e) { failed = e; }
                workTicks += Stopwatch.GetTimestamp() - t;
                Volatile.Write(ref busy, 0);
            }
        }

        static void Tables()
        {
            solidLayer = new bool[state.Length];
            vLight = (uint*)Room(sizeof(uint) * Slots);
            vRec = (uint*)Room(sizeof(uint) * Slots);
            vType = (ushort*)Room(sizeof(ushort) * Slots);
            vFlags = Room(Slots);
            vMark = (uint*)Room(sizeof(uint) * Slots);
            changedT = (int*)Room(sizeof(int) * Slots);
            changedA = (int*)Room(sizeof(int) * Slots);
        }

        // TileCache.Area for the non-solid pass: the solid pass's questions are given to the thread
        static void Ahead(int x0, int x1, int y0, int y1)
        {
            if (ping != 0)
            {
                PingAhead();
                return;
            }
            stepOn = false;
            if (!split || broken || keys == null || !fits || Main.gameMenu)
                return;
            if (worker == null)
            {
                // its thread and tables, or (memory, the system's limit on threads) none: then the solid
                // pass asks everything itself, as with TR_TILESPLIT=0
                try
                {
                    Tables();
                    StartWorker();
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[trpatch] tile split: not used ({e.GetType().Name}: {e.Message})");
                    broken = true;
                    return;
                }
                Console.Error.WriteLine($"[trpatch] tile split: the solid tile pass's questions asked on a thread of its own{(stepping ? ", and the walk steps over what it found" : "")}");
            }
            if (failed != null)
            {
                var e = failed;
                failed = null;
                Console.Error.WriteLine($"[trpatch] tile split: the thread failed ({e.GetType().Name}: {e.Message}); its answers were not used, and it is not used again");
                broken = true;
                return;
            }
            if (Volatile.Read(ref busy) != 0)
            {
                SplitSkipped++;
                return;
            }
            int w = Tiles.fastW, h = Tiles.fastH;
            if (w == 0 || w > 65536 || h > 65536)
                return;
            // as Area works them out for the solid pass, and Early's bounds
            ax0 = Math.Max(x0 - 3, 0); ax1 = Math.Min(x1 + 2, w - 1); ay0 = Math.Max(y0 - 1, 0); ay1 = Math.Min(y1 + 4, h - 1);
            if (ax1 < ax0 || ay1 < ay0 || ax1 - ax0 > Mask || ay1 - ay0 > Mask)
                return;
            // TileDrawing.IsTileDrawLayerSolid, as a table for the thread
            for (int i = 0; i < solidLayer.Length; i++)
                solidLayer[i] = SolidLayer(i);
            wx0 = Math.Max(x0 - 2, ax0 + 1);
            wx1 = Math.Min(x1 + 2, ax1);
            wy0 = Math.Max(y0, ay0 + 1);
            wy1 = Math.Min(y1 + 4, ay1);
            jArea0 = x0; jArea1 = x1; jArea2 = y0; jArea3 = y1; jWidth = w;
            jGen = (uint)frame + 1;
            jFloor = floor;
            jSeed = Main.TileFrameSeed;
            jBright = Lighting.GlobalBrightness;
            low = wy1;
            high = wy0 - 1;
            compared = 0;
            busy = 1;
            SplitJobs++;
            givenAt = Stopwatch.GetTimestamp();
            Volatile.Write(ref *word, *word + 1);
            Wake();
        }

        // TileDrawing.IsTileDrawLayerSolid
        static bool SolidLayer(int type) => Terraria.ID.TileID.Sets.DrawTileInSolidLayer[type] ?? Main.tileSolid[type];

        // on the thread
        static void Job()
        {
            int h = Tiles.fastH;
            byte* data = Tiles.data;
            // the blocks against the cache's copy, as Area will compare them (read only: Area keeps the copy)
            int n = 0;
            if (stepping)
            {
                for (int x = ax0; x <= ax1; x++)
                {
                    byte* t = (byte*)(data + (x * h + ay0));
                    Shadow* column = shadow + ((x & Mask) << 6);
                    for (int y = ay0; y <= ay1; y++, t += TileSize)
                    {
                        Shadow* s = column + (y & Mask);
                        if (s->tag == ((uint)x | (uint)y << 16) && Same14(s->tile, t))
                            continue;
                        int at = (y & Mask) << 6 | (x & Mask);
                        if (n < Slots)
                            changedT[n] = at;
                        vMark[at] = jGen;
                        n++;
                    }
                }
                nChangedT = n;
                Volatile.Write(ref compared, 1);
            }
            int types = state.Length;
            int[] tileFrame = Main.tileFrame;
            Asset<Microsoft.Xna.Framework.Graphics.Texture2D>[] textures = TextureAssets.Tile;
            for (int y = wy1 - 1; y >= wy0; y--)
            {
                if (y <= Volatile.Read(ref high))
                    break;
                int row = (y & Mask) << 6;
                uint tagY = (uint)y << 16;
                for (int x = wx0; x < wx1; x++)
                {
                    int at = row | (x & Mask);
                    byte* t = data + (long)(x * h + y) * TileSize;
                    byte f = 0;
                    int type = (*(ushort*)t);
                    if (((*(ushort*)(t + 5)) & 0x20) == 0)
                        f = Empty;
                    else if (type < types && solidLayer[type])
                    {
                        Key* k = keys + at;
                        uint light = Lighting.GetColor(x, y).PackedValue;
                        uint rec = k->rec;
                        f = Done;
                        if (k->tag == ((uint)x | tagY) && k->type == type && k->light == light && rec >= jFloor && cells[at].frame == tileFrame[type])
                            f |= Pre;
                        if (k->kind == Kept && (k->flags & Whole) != 0)
                        {
                            f |= KeptWhole;
                            if ((f & Pre) != 0 && textures[type].State == AssetState.Loaded)
                                f |= Ready;
                        }
                        if (new Terraria.Utilities.FastRandom(jSeed).WithModifier(x, y).Next(4) == 0)
                            f |= R4;
                        vLight[at] = light;
                        vRec[at] = rec;
                    }
                    // a changed block is the walk's own to visit
                    if (vMark[at] == jGen)
                        f &= unchecked((byte)~(Empty | Ready));
                    vType[at] = (ushort)type;
                    vFlags[at] = f;
                }
                Volatile.Write(ref low, y);
                SplitRows++;
            }
        }

        // TileCache.Area for the solid pass, before it compares the blocks
        static void SolidArea(int x0, int x1, int y0, int y1)
        {
            sArea0 = x0; sArea1 = x1; sArea2 = y0; sArea3 = y1;
            nChangedA = 0;
            if (jGen == (uint)frame)
            {
                windowTicks += Stopwatch.GetTimestamp() - givenAt;
                windows++;
            }
            comparedAtArea = stepping && jGen == (uint)frame && Volatile.Read(ref compared) != 0;
        }

        // Area found a block changed (at: its place in the cache)
        static void SolidChanged(int at)
        {
            if (!comparedAtArea)
                return;
            if (nChangedA < Slots)
                changedA[nChangedA] = at;
            nChangedA++;
        }

        // Area has compared the blocks: did it find the changes the thread found?
        static void SolidCompared()
        {
            sameChanges = false;
            if (!comparedAtArea || nChangedA != nChangedT || nChangedA > Slots)
                return;
            for (int i = 0; i < nChangedA; i++)
                if (changedA[i] != changedT[i])
                    return;
            sameChanges = true;
        }

        // TileCache.Pass, as the solid pass starts
        static void SolidPass()
        {
            sArea0 = int.MinValue;
            curRow = int.MinValue;
            curOk = false;
            jobChecked = false;
            sameChanges = false;
            stepOn = false;
        }

        // the first question of the pass that uses the thread's answers (after Start)
        static void CheckJob()
        {
            jobChecked = true;
            jobOk = split && !broken && failed == null && jGen == (uint)frame && sArea0 == jArea0 && sArea1 == jArea1 && sArea2 == jArea2 && sArea3 == jArea3
                && floor == jFloor && Main.TileFrameSeed == jSeed && Lighting.GlobalBrightness == jBright && Tiles.fastW == jWidth;
            if (split && jGen == (uint)frame && !jobOk)
                SplitStale++;
            if (jobOk)
            {
                rowsReady += wy1 - Volatile.Read(ref low);
                rowsAll += wy1 - wy0;
            }
            stepOn = jobOk && stepping && sameChanges && ok && !audit && !Terraria.Testing.DebugOptions.ShowUnbreakableWall;
            if (stepOn)
                SplitStepFrames++;
        }

        // at a row's first question: are the thread's answers for the row usable?
        static bool RowUsable(int y)
        {
            curRow = y;
            if (!jobChecked)
                CheckJob();
            if (!jobOk || y < wy0 || y >= wy1)
                return false;
            if (y >= Volatile.Read(ref low))
                return true;
            Volatile.Write(ref high, y);
            SplitTaken++;
            return false;
        }

        // Early: the thread's answer for the tile, or 0 where there is none
        static byte Answer(int x, int y, int at, int type)
        {
            if (y != curRow)
                curOk = RowUsable(y);
            if (!curOk || x < wx0 || x >= wx1)
                return 0;
            byte f = vFlags[at];
            // (a tile is not written while the pass draws; were it, the type the walk sees decides)
            return (f & Done) != 0 && vType[at] == type ? f : (byte)0;
        }

        // TileDrawing.Draw's walk, for its next column (trpatch has it call here for "column + 1")
        public static int Step(int column, TileDrawing td, Vector2 screen, Vector2 offset, int row)
        {
            if (!stepOn)
                return column + 1;
            return Steps(column, td, screen, offset, row);
        }

        // the tiles the walk would pass, and those Early would put back whole, are done here; the
        // next tile the walk has to visit is where it goes on
        static int Steps(int column, TileDrawing td, Vector2 screen, Vector2 offset, int row)
        {
            int x = column + 1;
            if (fresh)
                Start(td, offset);
            if (!passSolid || !ok)
                return x;
            if (row != curRow)
                curOk = RowUsable(row);
            if (!curOk || x < wx0)
                return x;
            int line = (row & Mask) << 6, h = Tiles.fastH;
            byte* data = Tiles.data;
            for (; x < wx1; x++)
            {
                int at = line | (x & Mask);
                byte f = vFlags[at];
                if ((f & Empty) != 0)
                {
                    if (splitAudit)
                        CheckStep(x, row, at, f);
                    continue;
                }
                if ((f & Ready) == 0)
                    return x;
                int type = vType[at];
                // what only the pass knows: as Early asks it (and the walk's own question of the layer)
                if (state[type] != Proven || near[at] > vRec[at] || ((x * 7 + row * 13 + frame) & 63) == 0 || !SolidLayer(type))
                    return x;
                if (splitAudit)
                    CheckStep(x, row, at, f);
                Replays++;
                Wholes++;
                SplitUsed++;
                SplitStepped++;
                Replay(td, at, data + (x * h + row), screen, offset, x, row, vLight[at], type, (f & R4) != 0 ? 1 : 0);
            }
            return x;
        }

        // TR_TILESPLIT_AUDIT with TR_TILESPLIT=2: what the walk and Early would have done with a tile
        // Step passes (an empty one) or puts back, asked as they ask it
        static void CheckStep(int x, int y, int at, byte f)
        {
            byte* t = Tiles.data + (long)(x * Tiles.fastH + y) * TileSize;
            bool active = ((*(ushort*)(t + 5)) & 0x20) != 0;
            if ((f & Empty) != 0)
            {
                if (active)
                    Wrong($"the walk would have visited {x},{y} (flags {f}, type {(*(ushort*)t)})");
                return;
            }
            int type = (*(ushort*)t);
            Key* k = keys + at;
            uint light = Lighting.GetColor(x, y).PackedValue;
            bool whole = active && type < state.Length && SolidLayer(type) && state[type] == Proven
                && k->tag == ((uint)x | (uint)y << 16) && k->type == type && k->light == light && k->rec >= floor && near[at] <= k->rec
                && cells[at].frame == Main.tileFrame[type] && k->kind == Kept && (k->flags & Whole) != 0 && ((x * 7 + y * 13 + frame) & 63) != 0
                && TextureAssets.Tile[type].State == AssetState.Loaded;
            bool r4 = new Terraria.Utilities.FastRandom(Main.TileFrameSeed).WithModifier(x, y).Next(4) == 0;
            if (!whole || light != vLight[at] || r4 != ((f & R4) != 0) || type != vType[at])
                Wrong($"Early would not have put {x},{y} back so (flags {f}, whole {whole}, light {light:x8} {vLight[at]:x8}, r4 {r4}, type {type} {vType[at]})");
        }

        // TR_TILESPLIT_AUDIT: the thread's answer against Early's own
        static void Compare(byte f, int at, int x, int y, int type)
        {
            Key* k = keys + at;
            uint light = Lighting.GetColor(x, y).PackedValue;
            bool pre = k->tag == ((uint)x | (uint)y << 16) && k->type == type && k->light == light && k->rec >= floor
                && cells[at].frame == Main.tileFrame[type];
            bool r4 = new Terraria.Utilities.FastRandom(Main.TileFrameSeed).WithModifier(x, y).Next(4) == 0;
            bool keptWhole = k->kind == Kept && (k->flags & Whole) != 0;
            bool ready = pre && keptWhole && TextureAssets.Tile[type].State == AssetState.Loaded && vMark[at] != jGen;
            if (vLight[at] != light || vRec[at] != k->rec || ((f & Pre) != 0) != pre || ((f & KeptWhole) != 0) != keptWhole || ((f & R4) != 0) != r4
                || (stepping && sameChanges && ((f & Ready) != 0) != ready))
                Wrong($"the thread's answer for {x},{y} differs (light {vLight[at]:x8} {light:x8}, frame {vRec[at]} {k->rec}, flags {f} pre {pre} kept {keptWhole} r4 {r4} ready {ready})");
        }

        static void Wrong(string what)
        {
            if (++SplitWrong <= 8)
                Console.Error.WriteLine("[trpatch] tile split: " + what);
        }

        static string SplitLine()
        {
            if (ping != 0)
                return PingLine();
            if (!split)
                return "";
            double ms = 1000.0 / Stopwatch.Frequency / Math.Max(1, SplitJobs);
            string s = $"; split: {SplitJobs} jobs (started {startTicks * ms:F3} ms after the wake, {workTicks * ms:F2} ms each, the solid pass {windowTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, windows):F2} ms after the wake; "
                + $"{rowsReady * 100 / Math.Max(1, rowsAll)}% of rows done as the pass began), "
                + $"{SplitRows} rows by the thread, {SplitTaken} by the game, {SplitUsed} answers used"
                + $", {SplitStepped} stepped over in {SplitStepFrames} frames, {SplitSkipped} busy, {SplitStale} stale" + (splitAudit ? $", {SplitWrong} wrong" : "");
            SplitJobs = SplitRows = SplitTaken = SplitUsed = SplitSkipped = SplitStale = SplitStepped = SplitStepFrames = 0;
            workTicks = startTicks = rowsReady = rowsAll = windowTicks = windows = 0;
            return s;
        }

        // TR_TILESPLIT_PING
        static long jobAt, startedAt;
        static readonly int[] starts = new int[4096], answers = new int[4096], wakes = new int[4096];
        static int pings, wokeAsleep, ranOut;

        // the job's number answered (mode 3: the number it was woken with, when no job came)
        static int Pong(int now)
        {
            if ((now & 1) == 1)
            {
                // mode 3: woken ahead; spin until the job (an even number) comes
                long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 200;
                spinning = 1;
                while (Volatile.Read(ref *word) == now && Stopwatch.GetTimestamp() < until)
                    Thread.SpinWait(1);
                spinning = 0;
                if (Volatile.Read(ref *word) == now)
                {
                    ranOut++;
                    return now;
                }
                now = Volatile.Read(ref *word);
            }
            startedAt = Stopwatch.GetTimestamp();
            done = now;
            return now;
        }

        static void PingAhead()
        {
            if (worker == null)
            {
                StartWorker();
                Console.Error.WriteLine($"[trpatch] tile split: ping mode {ping}");
                return;
            }
            if (ping == 3 && (*word & 1) == 0)
            {
                Volatile.Write(ref *word, *word + 1);
                Wake();
            }
        }

        static void Ping()
        {
            if (worker == null)
                return;
            int next = (*word | 1) + 1;
            bool wake = ping != 3 || spinning == 0;
            jobAt = Stopwatch.GetTimestamp();
            Volatile.Write(ref *word, next);
            if (wake)
                Wake();
            long woken = Stopwatch.GetTimestamp();
            while (done != next)
                Thread.SpinWait(1);
            long back = Stopwatch.GetTimestamp();
            double us = 1e6 / Stopwatch.Frequency;
            if (pings < starts.Length)
            {
                starts[pings] = (int)((startedAt - jobAt) * us);
                answers[pings] = (int)((back - jobAt) * us);
                wakes[pings] = (int)((woken - jobAt) * us);
                pings++;
            }
            if (wake && ping == 3)
                wokeAsleep++;
        }

        static string Spread(int[] a, int count)
        {
            Array.Sort(a, 0, count);
            long sum = 0;
            for (int i = 0; i < count; i++)
                sum += a[i];
            return $"mean {sum / Math.Max(1, count)} p50 {a[count / 2]} p90 {a[count * 9 / 10]} p99 {a[count * 99 / 100]} max {a[count - 1]}";
        }

        static string PingLine()
        {
            if (pings == 0)
                return "";
            int count = pings;
            string s = $"; split ping {ping}: {count} frames, start {Spread(starts, count)} us, answer {Spread(answers, count)} us, wake call {Spread(wakes, count)} us"
                + (ping == 3 ? $", woken asleep {wokeAsleep}, spins ran out {ranOut}" : "");
            pings = wokeAsleep = ranOut = 0;
            return s;
        }
    }
}
