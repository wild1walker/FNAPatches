using Microsoft.Xna.Framework;
using MonoMod.InlineRT;
using MonoMod;
using System.Runtime.InteropServices;
using System;
using Terraria.ID;
using Terraria;

#pragma warning disable 1591, 0649, 0169, 0414

// Items lying still (wild1walker, portmaster-terraria, MIT: tools/trpatch/Settles.cs and runtime/Settled.cs): every update the
// game asks five questions of the blocks around each item on the ground (a conveyor under it, lava, liquid, the blocks in its
// way, the slopes); an item that lies still gets the same answers while the blocks around it, the tables the questions read and
// the item itself are as they were, so TrPatches.Settled keeps the answers and the blocks they came from, and compares the
// blocks before it gives one back. The pass applies only to the routines of 1.4.5.8 it was written for (sums of their code as
// shipped). On the RK3326 up to 4 % of the game thread in a fight with many drops. TR_SETTLED=0: as before; TR_SETTLED_AUDIT=1
// checks. TERRARIA_SETTLED=0 leaves the pass out when patching.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_SPEED") == "0" || Environment.GetEnvironmentVariable("TERRARIA_SETTLED") == "0")
				return;
			// the sums are taken of the game's code as shipped: here, before MonoMod weaves anything
			TerrariaPatches.TileConvert.SettlesPass.Look(MonoModRule.Modder.Module);
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Settles, "settles", m => TerrariaPatches.TileConvert.SettlesPass.Apply(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
    // Items lying still. Every update the game asks five questions of the blocks around each item
    // on the ground (a conveyor under it, lava, liquid, the blocks in its way, the slopes); each
    // answer comes from the item's place and size, the speed handed in, the blocks next to it and
    // five of the game's tables, and from nothing else (the patcher checks that on the game's
    // code). For an item at the place it had an update before, the blocks around it are kept
    // here with each answer; while the place, the blocks and the tables are bit for bit what they
    // were, and the speed handed in and the flags a routine finds are too, the kept answer is given
    // with the flags the game's routine left. Anything else goes to the game's routine.
    // TR_SETTLED=0 leaves it out; TR_SETTLED_AUDIT=1 gives no kept answer and compares each with
    // the game's instead.
    public static unsafe class Settled
    {
        const int MaxSide = 10, TileSize = 14, Stride = MaxSide * MaxSide * TileSize;
        const byte HaveBelt = 1, HaveLava = 2, HaveWet = 4, HaveTile = 8, HaveSlope = 16;
        // the flags each routine can leave changed, as Flags counts them
        const byte WetFlags = 3, TileFlags = 12, SlopeFlags = 124;

        [StructLayout(LayoutKind.Sequential)]
        struct Rec
        {
            public Vector2 pos;
            public int w, h;
            // the blocks kept, and the world and the tables they were taken under
            public int x0, y0, nx, ny;
            public IntPtr data;
            public int fastW, fastH, maxX, maxY;
            public int version;
            // 0: the place is noted; 1: the blocks are kept too
            public byte stage, have;
            public bool belt, lava, wet;
            // the flags as a routine found them and as it left them
            public byte wetWas, wetLeft, tileWas, tileLeft, slopeWas, slopeLeft;
            public Vector2 beltIn, beltOut, tileIn, tileOut, slopeIn;
            public float gravity;
            public Vector4 slopeOut;
        }

        static readonly bool on = Environment.GetEnvironmentVariable("TR_SETTLED") != "0";
        static readonly bool audit = Environment.GetEnvironmentVariable("TR_SETTLED_AUDIT") == "1";
        static Rec* recs;
        static byte* kept;
        static int slots;
        // the item whose blocks stand for the routine now running, or -1
        static int cur = -1;
        static uint checkedAt = uint.MaxValue;
        static byte[] copy;
        static int la, lb, lc, ld, le;
        static int version, touched = 1;
        static long put, worked, retaken, compared, wrong;

        // a write to one of the tables, or what may be one: the patcher has each call this
        public static void Touched() => System.Threading.Volatile.Write(ref touched, 1);

        static bool Same(Vector2 a, Vector2 b) => *(ulong*)&a == *(ulong*)&b;

        static void Start()
        {
            slots = Main.item.Length;
            int bytes = slots * sizeof(Rec);
            recs = (Rec*)Marshal.AllocHGlobal(bytes);
            for (int i = 0; i < bytes; i++)
                ((byte*)recs)[i] = 0;
            kept = (byte*)Marshal.AllocHGlobal(slots * Stride);
        }

        static bool Eq(byte* a, byte* b, int n)
        {
            int i = 0;
            for (; i + 8 <= n; i += 8)
                if (*(ulong*)(a + i) != *(ulong*)(b + i))
                    return false;
            for (; i < n; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        // the tables the five routines read against the copy kept of them: once an update, and
        // after any write to one. Most writes put back what was there (the game switches a kind
        // of block off for one routine and on again), and the answers kept stand through those.
        static void Check()
        {
            System.Threading.Volatile.Write(ref touched, 0);
            bool[] solid = Main.tileSolid, top = Main.tileSolidTop, platforms = TileID.Sets.Platforms, doors = TileID.Sets.ForAdvancedCollision.ClosedDoors;
            int[] belts = TileID.Sets.ConveyorDirection;
            int a = solid.Length, b = top.Length, c = platforms.Length, d = doors.Length, e = belts.Length * 4;
            bool same = copy != null && a == la && b == lb && c == lc && d == ld && e == le;
            if (!same)
            {
                copy = new byte[a + b + c + d + e + 8];
                la = a; lb = b; lc = c; ld = d; le = e;
            }
            fixed (byte* s = copy)
            fixed (bool* p0 = solid, p1 = top, p2 = platforms, p3 = doors)
            fixed (int* p4 = belts)
            {
                if (same && Eq(s, (byte*)p0, a) && Eq(s + a, (byte*)p1, b) && Eq(s + a + b, (byte*)p2, c)
                    && Eq(s + a + b + c, (byte*)p3, d) && Eq(s + a + b + c + d, (byte*)p4, e))
                    return;
                Buffer.MemoryCopy(p0, s, a, a);
                Buffer.MemoryCopy(p1, s + a, b, b);
                Buffer.MemoryCopy(p2, s + a + b, c, c);
                Buffer.MemoryCopy(p3, s + a + b + c, d, d);
                Buffer.MemoryCopy(p4, s + a + b + c + d, e, e);
            }
            version++;
        }

        // keeps the blocks any of the five routines can read from the item's place: two around
        // the columns and rows the item is in, as the routines count them
        static bool Take(Rec* r, int k)
        {
            int w = Tiles.fastW, h = Tiles.fastH;
            float x = r->pos.X, y = r->pos.Y;
            if (h == 0 || !(x >= 64f && y >= 64f && x < 1e7f && y < 1e7f) || r->w < 0 || r->h < 0 || r->w > 64 || r->h > 64)
                return false;
            int x0 = (int)((x - 1f) / 16f) - 2, x1 = (int)((x + (float)r->w + 1f) / 16f) + 2;
            int y0 = (int)((y - 1f) / 16f) - 2, y1 = (int)((y + (float)r->h + 3f) / 16f) + 2;
            int nx = x1 - x0 + 1, ny = y1 - y0 + 1;
            if (nx > MaxSide || ny > MaxSide || x0 < 0 || y0 < 0 || x1 >= w || y1 >= h)
                return false;
            r->x0 = x0;
            r->y0 = y0;
            r->nx = nx;
            r->ny = ny;
            r->data = (IntPtr)Tiles.data;
            r->fastW = w;
            r->fastH = h;
            r->maxX = Main.maxTilesX;
            r->maxY = Main.maxTilesY;
            r->version = version;
            byte* to = kept + k * Stride;
            int n = ny * TileSize;
            for (int c = 0; c < nx; c++, to += n)
                Buffer.MemoryCopy(Tiles.data + (long)((x0 + c) * h + y0) * TileSize, to, n, n);     // (FNAPatches) the records' bytes
            return true;
        }

        // the world, the tables and the kept blocks are what they were
        static bool Stands(Rec* r, int k)
        {
            int h = Tiles.fastH;
            if (r->data != (IntPtr)Tiles.data || r->fastW != Tiles.fastW || r->fastH != h || r->maxX != Main.maxTilesX
                || r->maxY != Main.maxTilesY || r->version != version)
                return false;
            byte* was = kept + k * Stride;
            int n = r->ny * TileSize;
            for (int c = 0; c < r->nx; c++, was += n)
            {
                byte* now = Tiles.data + (long)((r->x0 + c) * h + r->y0) * TileSize;     // (FNAPatches) the records' bytes
                int i = 0;
                for (; i + 8 <= n; i += 8)
                    if (*(ulong*)(now + i) != *(ulong*)(was + i))
                        return false;
                for (; i < n; i++)
                    if (now[i] != was[i])
                        return false;
            }
            return true;
        }

        static void Wrong(string what)
        {
            if (wrong++ < 20)
                Console.Error.WriteLine($"[trpatch] settled: the kept answer for {what} of item {cur} is not the game's");
        }

        // the first of the five in an item's update: finds out whether the item's blocks stand
        public static bool Belt(WorldItem item, ref Vector2 velocity)
        {
            cur = -1;
            if (!on)
                return Collision.ApplyConveyorBeltMovementToVelocity(item, ref velocity);
            if (recs == null)
                Start();
            int k = item.whoAmI;
            if ((uint)k >= (uint)slots)
                return Collision.ApplyConveyorBeltMovementToVelocity(item, ref velocity);
            Rec* r = recs + k;
            if (!Same(r->pos, item.position) || r->w != item.width || r->h != item.height)
            {
                r->pos = item.position;
                r->w = item.width;
                r->h = item.height;
                r->stage = r->have = 0;
                return Collision.ApplyConveyorBeltMovementToVelocity(item, ref velocity);
            }
            if (Main.GameUpdateCount != checkedAt || System.Threading.Volatile.Read(ref touched) != 0)
            {
                checkedAt = Main.GameUpdateCount;
                Check();
            }
            if (r->stage == 0 || !Stands(r, k))
            {
                if (r->stage != 0)
                    retaken++;
                r->stage = r->have = 0;
                if (!Take(r, k))
                    return Collision.ApplyConveyorBeltMovementToVelocity(item, ref velocity);
                r->stage = 1;
            }
            cur = k;
            Vector2 handed = velocity;
            bool known = (r->have & HaveBelt) != 0 && Same(r->beltIn, handed);
            if (known && !audit)
            {
                put++;
                velocity = r->beltOut;
                return r->belt;
            }
            bool b = Collision.ApplyConveyorBeltMovementToVelocity(item, ref velocity);
            bool stayed = Same(item.position, r->pos) && item.width == r->w && item.height == r->h;
            if (known)
            {
                compared++;
                if (!stayed || b != r->belt || !Same(velocity, r->beltOut))
                    Wrong("the conveyor");
            }
            if (!stayed)
            {
                // a conveyor moved the item
                r->stage = r->have = 0;
                cur = -1;
                return b;
            }
            worked++;
            r->beltIn = handed;
            r->beltOut = velocity;
            r->belt = b;
            r->have |= HaveBelt;
            return b;
        }

        static Rec* Here(Vector2 position, int width, int height)
        {
            int k = cur;
            if (k < 0)
                return null;
            if (System.Threading.Volatile.Read(ref touched) != 0)
                Check();
            Rec* r = recs + k;
            return r->version == version && Same(position, r->pos) && width == r->w && height == r->h ? r : null;
        }

        public static bool Lava(Vector2 position, int width, int height)
        {
            Rec* r = Here(position, width, height);
            if (r == null)
                return Collision.LavaCollision(position, width, height);
            bool known = (r->have & HaveLava) != 0;
            if (known && !audit)
            {
                put++;
                return r->lava;
            }
            bool b = Collision.LavaCollision(position, width, height);
            if (known)
            {
                compared++;
                if (b != r->lava)
                    Wrong("lava");
            }
            worked++;
            r->lava = b;
            r->have |= HaveLava;
            return b;
        }

        // the flags the routines leave behind
        static byte Flags() =>
            (byte)((Collision.honey ? 1 : 0) | (Collision.shimmer ? 2 : 0) | (Collision.up ? 4 : 0) | (Collision.down ? 8 : 0)
                | (Collision.stair ? 16 : 0) | (Collision.stairFall ? 32 : 0) | (Collision.sloping ? 64 : 0));

        static void Leave(byte flags, byte which)
        {
            if ((which & 1) != 0)
                Collision.honey = (flags & 1) != 0;
            if ((which & 2) != 0)
                Collision.shimmer = (flags & 2) != 0;
            if ((which & 4) != 0)
                Collision.up = (flags & 4) != 0;
            if ((which & 8) != 0)
                Collision.down = (flags & 8) != 0;
            if ((which & 16) != 0)
                Collision.stair = (flags & 16) != 0;
            if ((which & 32) != 0)
                Collision.stairFall = (flags & 32) != 0;
            if ((which & 64) != 0)
                Collision.sloping = (flags & 64) != 0;
        }

        public static bool Wet(Vector2 position, int width, int height)
        {
            Rec* r = Here(position, width, height);
            if (r == null)
                return Collision.WetCollision(position, width, height);
            byte was = (byte)(Flags() & WetFlags);
            bool known = (r->have & HaveWet) != 0 && r->wetWas == was;
            if (known && !audit)
            {
                put++;
                Leave(r->wetLeft, WetFlags);
                return r->wet;
            }
            bool b = Collision.WetCollision(position, width, height);
            byte left = (byte)(Flags() & WetFlags);
            if (known)
            {
                compared++;
                if (b != r->wet || left != r->wetLeft)
                    Wrong("liquid");
            }
            worked++;
            r->wet = b;
            r->wetWas = was;
            r->wetLeft = left;
            r->have |= HaveWet;
            return b;
        }

        public static Vector2 Tile(Vector2 position, Vector2 velocity, int width, int height, bool fallThrough, bool fall2, int gravDir,
            bool ignoreDoors, bool ignoreAetheriumPlatforms, bool hoik)
        {
            Rec* r = Here(position, width, height);
            // an item's update asks it one way only
            if (r == null || fallThrough || fall2 || gravDir != 1 || ignoreDoors || !ignoreAetheriumPlatforms || !hoik)
                return Collision.TileCollision(position, velocity, width, height, fallThrough, fall2, gravDir, ignoreDoors, ignoreAetheriumPlatforms, hoik);
            byte was = (byte)(Flags() & TileFlags);
            bool known = (r->have & HaveTile) != 0 && r->tileWas == was && Same(r->tileIn, velocity);
            if (known && !audit)
            {
                put++;
                Leave(r->tileLeft, TileFlags);
                return r->tileOut;
            }
            Vector2 v = Collision.TileCollision(position, velocity, width, height, fallThrough, fall2, gravDir, ignoreDoors, ignoreAetheriumPlatforms, hoik);
            byte left = (byte)(Flags() & TileFlags);
            if (known)
            {
                compared++;
                if (!Same(v, r->tileOut) || left != r->tileLeft)
                    Wrong("the blocks in its way");
            }
            worked++;
            r->tileIn = velocity;
            r->tileOut = v;
            r->tileWas = was;
            r->tileLeft = left;
            r->have |= HaveTile;
            return v;
        }

        // the last of the five
        public static Vector4 Slope(Vector2 position, Vector2 velocity, int width, int height, float gravity, bool fall, bool ignoreAetheriumPlatforms)
        {
            Rec* r = Here(position, width, height);
            cur = -1;
            if (r == null || fall || !ignoreAetheriumPlatforms)
                return Collision.SlopeCollision(position, velocity, width, height, gravity, fall, ignoreAetheriumPlatforms);
            byte was = (byte)(Flags() & SlopeFlags);
            bool known = (r->have & HaveSlope) != 0 && r->slopeWas == was && Same(r->slopeIn, velocity) && *(uint*)&gravity == *(uint*)&r->gravity;
            if (known && !audit)
            {
                put++;
                Leave(r->slopeLeft, SlopeFlags);
                return r->slopeOut;
            }
            Vector4 v = Collision.SlopeCollision(position, velocity, width, height, gravity, fall, ignoreAetheriumPlatforms);
            byte left = (byte)(Flags() & SlopeFlags);
            if (known)
            {
                compared++;
                Vector4 kept = r->slopeOut;
                if (((ulong*)&v)[0] != ((ulong*)&kept)[0] || ((ulong*)&v)[1] != ((ulong*)&kept)[1] || left != r->slopeLeft)
                    Wrong("the slopes");
            }
            worked++;
            r->slopeIn = velocity;
            r->gravity = gravity;
            r->slopeOut = v;
            r->slopeWas = was;
            r->slopeLeft = left;
            r->have |= HaveSlope;
            return v;
        }

        public static string Line() =>
            $"items {put} put back {worked} worked out {retaken} retaken {version} table changes" + (audit ? $" {compared} compared {wrong} wrong" : "");
    }
}
