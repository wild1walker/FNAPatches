using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using MonoMod;
using MonoMod.InlineRT;
using Terraria;
using Terraria.DataStructures;
using Terraria.Graphics.Shaders;

#pragma warning disable 1591, 0649, 0169, 0414

// The PortMaster port's speed passes (wild1walker, portmaster-terraria, MIT), over the woven game (TileConverter: Split.cs,
// Inits.cs, Scans.cs, DrawCuts.cs; their order in TileConverter/Passes.cs):
//   - the longest routines split into parts and their locals no longer cleared at every call (the creatures' and
//     projectiles' AI cleared up to 85 KB of stack per call: on the H700 the Destroyer went from about 49 to 60 fps);
//   - draw cuts: one TileDrawInfo instead of one per tile, the zoom asked once a pass, only the queued sprites uploaded, the
//     pylon layer's search left out with no pylon, the stars drawn texture by texture (TrPatches.Stars, TR_STARS=0: off),
//     the clouds under the stock sprite shader where the cloud shader's edge light adds nothing, and in runs
//     (TrPatches.Clouds, TR_CLOUD_RUNS=0: one call a cloud), the dust out of sight left out of the batch (TrPatches.Offscreen,
//     TR_OFFSCREEN=0: off), the water's first question per tile from a copy of the screen's liquid (TrPatches.Water,
//     TR_WATER=0: off);
//   - the fixed lists' "inactive" remembered (TrPatches.Scan, TR_SCAN=0: off, TR_SCAN_AUDIT=1: checked), loops passing the
//     slots known inactive, the dust list's inactive end, the search for a free dust, the lying items' merge (TR_SAME=0: off).
//   - the interface's draw: work whose answer is already known (TrPatches.Ui: the minimap's boss bags and icons, the
//     projectile type searches, the players seen, the lock-on marks; TR_UI=0: off; TR_SCAN_AUDIT=1 checks its marks).
// TERRARIA_SPEED=0 leaves all of it out; TERRARIA_UI=0 the interface's cuts; TERRARIA_SPLIT=0, TERRARIA_INITS=0, TERRARIA_SCANS=0, TERRARIA_DRAWCUTS=0 one pass.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_SPEED") == "0")
			{
				return;
			}
			bool hook = false;
			// the interface's cuts: their sums taken of the game's code as shipped (here, before MonoMod weaves anything; the
			// weave rewrites the bodies' encoding), applied before the scans (the loops are matched as the game wrote them)
			if (Environment.GetEnvironmentVariable("TERRARIA_UI") != "0")
			{
				TerrariaPatches.TileConvert.UiPass.Look(MonoModRule.Modder.Module);
				hook |= TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Ui, "ui", m => TerrariaPatches.TileConvert.UiPass.Apply(m));
			}
			if (Environment.GetEnvironmentVariable("TERRARIA_DRAWCUTS") != "0")
				hook |= TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.DrawCuts, "drawcuts", m => TerrariaPatches.TileConvert.Speed.DrawCuts(m));
			if (Environment.GetEnvironmentVariable("TERRARIA_SCANS") != "0")
				hook |= TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Scans, "scans", m => TerrariaPatches.TileConvert.Speed.Scans(m));
			if (Environment.GetEnvironmentVariable("TERRARIA_SPLIT") != "0")
				hook |= TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Split, "split", m => TerrariaPatches.TileConvert.Speed.Split(m));
			if (Environment.GetEnvironmentVariable("TERRARIA_INITS") != "0")
				hook |= TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Inits, "inits", m => TerrariaPatches.TileConvert.Speed.Inits(m));
			if (hook)
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace Terraria
{
	// the index + 1 of the world item a marked inner item belongs to (TrPatches.Scan)
	class patch_Item : Item
	{
		public int trSlot;
	}
}

// From the PortMaster port by wild1walker (tools/trpatch/runtime/Scan.cs and Stars.cs, MIT), except where marked: his
// interface cuts (Ui) are not taken, so their marks are left out here (FNAPatches).
namespace TrPatches
{
    using System.Linq;
    using Terraria.ID;

    // ---- the interface (wild1walker, portmaster-terraria runtime/Ui.cs, MIT): TileConverter/UiCuts.cs

    // Work of the interface's draw whose answer is known without it (trpatch, UiCuts):
    //
    // The minimap's boss bag layer asks every one of the 400 world items, every frame, whether it is a
    // boss bag lying in the world: two reads from memory a slot (the world item, its inner item), about
    // 0.13 ms of a frame on the RK3326 with the fights' dropped coins. A world item found active and not
    // a bag is marked at its index ("plain"), as Scan marks an inactive one: the mark holds while the slot
    // keeps the object, the object its inner item, and the inner item its type. Every write of an item's
    // type goes through Scan.Type, a new inner item through Scan.Inner and a new index through Scan.Who,
    // and each takes the mark away (Scan); the bag table is the game's constant set. NextBag hands the
    // layer's loop the next slot that is not marked air or plain, so the loop's rounds for the others,
    // which would only read and go on, are not made.
    //
    // The golf club's power bar and the Digtoise map layer look through all 1000 projectiles every frame
    // for one of their types (722; 1098 and 1123). Every write of a projectile's type goes through
    // ProjType, which notes the type as made; while none of a loop's types was ever made, no projectile
    // can have one, and the loop is not begun.
    //
    // The minimap's creature icons ask every one of the 200 NPCs, every frame, whether it is a town NPC
    // and whether it has a boss head icon (NPC.GetBossHeadTextureIndex): the 87 parts of the Destroyer,
    // pirates, every creature. An NPC that is not a town NPC and whose type has no head icon whatever its
    // state (the table's -1, and none of the types the routine treats apart) is marked at its index the
    // same way; every write of an NPC's type or town flag goes through NpcType and NpcTown, a new index
    // through Scan.Who, and each takes the mark away. NextIcon hands the three map icon loops the next
    // NPC that is not marked so or inactive.
    //
    // TR_UI=0: none of it. TR_SCAN_AUDIT=1 checks the plain marks with Scan's.
    public static class Ui
    {
        static readonly bool on = Environment.GetEnvironmentVariable("TR_UI") != "0";
        public static long Bags, Passed, Skipped, Icons, IconsPassed;

        // ---- boss bags on the minimap: the plain marks (cleared by Scan)
        internal static readonly object[] seenB = new object[401];
        internal static readonly byte[] plainB = new byte[401];

        // from slot i of Main.item, the first slot below n whose round could do anything (a null slot, an
        // item that could be a bag, or one the game's own code should fail on), or -1 with none left
        public static int NextBag(int i, int n)
        {
            var list = Main.item;
            if (!on || Scan.Stop || i < 0 || i >= n || n > list.Length || n > plainB.Length)
                return i;
            bool[] bag = ItemID.Sets.BossBag;
            Bags++;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (o == null)
                    return Found(i, j);
                if (plainB[j] != 0 && seenB[j] == o || Scan.Air(o, j))
                    continue;
                var it = o.inner;
                if (it == null)
                    return Found(i, j);
                int type = it.type;
                if (type == 0)
                {
                    Scan.LookItem(o, j);
                    continue;
                }
                if ((uint)type >= (uint)bag.Length || bag[type])
                    return Found(i, j);
                Plain(o, it, j);
            }
            Passed += n - i;
            return -1;
        }

        static int Found(int i, int j)
        {
            Passed += j - i;
            return j;
        }

        // o, at index k, holds it, whose type is neither 0 nor a bag: marked, unless something moved
        static void Plain(WorldItem o, Item it, int k)
        {
            if (o.whoAmI != k)
                return;
            int type = it.type;
            plainB[k] = 0;
            seenB[k] = o;
            ((Terraria.patch_Item)(object)it).trSlot = k + 1;     // (FNAPatches) the field patch_Item adds
            plainB[k] = 1;
            Thread.MemoryBarrier();
            if (it.type != type || o.inner != it || o.whoAmI != k)
                plainB[k] = 0;
        }

        // ---- the map's creature icons: the plain marks (cleared here and by Scan.Who)
        internal static readonly object[] seenM = new object[201];
        internal static readonly byte[] plainM = new byte[201];
        // the types NPC.GetBossHeadTextureIndex works out from more than the table (1.4.5.8's, checked by
        // the patcher on the routine's code)
        static readonly bool[] apart = Apart(4, 125, 126, 262, 266, 439, 440);

        static bool[] Apart(params int[] types)
        {
            var a = new bool[types.Max() + 1];
            foreach (int t in types) a[t] = true;
            return a;
        }

        // from slot i of Main.npc, the first slot below n whose round could draw (a town NPC, a type with a
        // head icon, a null slot), or -1 with none left
        public static int NextIcon(int i, int n)
        {
            var list = Main.npc;
            if (!on || Scan.Stop || i < 0 || i >= n || n > list.Length || n > plainM.Length)
                return i;
            int[] heads = NPCID.Sets.BossHeadTextures;
            Icons++;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (o == null)
                {
                    IconsPassed += j - i;
                    return j;
                }
                if (plainM[j] != 0 && seenM[j] == o || !Scan.Active(o, j))
                    continue;
                int type = o.type;
                if (o.townNPC || (uint)type >= (uint)heads.Length || heads[type] != -1 || type < apart.Length && apart[type])
                {
                    IconsPassed += j - i;
                    return j;
                }
                PlainNpc(o, type, j);
            }
            IconsPassed += n - i;
            return -1;
        }

        static void PlainNpc(NPC o, int type, int k)
        {
            if (o.whoAmI != k)
                return;
            plainM[k] = 0;
            seenM[k] = o;
            plainM[k] = 1;
            Thread.MemoryBarrier();
            if (o.type != type || o.townNPC || o.whoAmI != k)
                plainM[k] = 0;
        }

        // every "npc.type = value" and "npc.townNPC = value" of the game
        public static void NpcType(NPC o, int value)
        {
            o.type = value;
            Unmark(o);
        }

        public static void NpcTown(NPC o, bool value)
        {
            o.townNPC = value;
            Unmark(o);
        }

        static void Unmark(NPC o)
        {
            Thread.MemoryBarrier();
            int k = o.whoAmI;
            if ((uint)k < (uint)plainM.Length && seenM[k] == o)
                plainM[k] = 0;
        }

        // Scan.Audit: every plain mark against its object
        internal static int Audit()
        {
            int wrong = 0;
            int[] heads = NPCID.Sets.BossHeadTextures;
            for (int k = 0; k < plainM.Length && k < Main.npc.Length; k++)
                if (plainM[k] != 0 && Main.npc[k] == seenM[k])
                {
                    var o = (NPC)seenM[k];
                    if (o.townNPC || (uint)o.type >= (uint)heads.Length || heads[o.type] != -1 || o.type < apart.Length && apart[o.type]
                        || o.whoAmI != k || o.active && o.GetBossHeadTextureIndex() != -1)
                        wrong++;
                }
            bool[] bag = ItemID.Sets.BossBag;
            for (int k = 0; k < plainB.Length && k < Main.item.Length; k++)
                if (plainB[k] != 0 && Main.item[k] == seenB[k])
                {
                    var o = (WorldItem)seenB[k];
                    int type = o.inner.type;
                    if (type == 0 || (uint)type >= (uint)bag.Length || bag[type] || o.whoAmI != k || ((Terraria.patch_Item)(object)o.inner).trSlot != k + 1)
                        wrong++;
                }
            return wrong;
        }

        // ---- the map's players: from slot i of Main.player, the first slot below n that is the local
        // player or not marked inactive by Scan (DrawPlayerMapIcon_CanBeSeen is false for the others, from
        // their "active" alone), or -1 with none left
        public static long Seen;

        public static int NextSeen(int i, int n)
        {
            var list = Main.player;
            if (!on || Scan.Stop || i < 0 || i >= n || n > list.Length)
                return i;
            int me = Main.myPlayer;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (j != me && o != null && Scan.IdlePlayer(o, j))
                    continue;
                Seen += j - i;
                return j;
            }
            Seen += n - i;
            return -1;
        }

        // ---- LockOnHelper.Draw's loop (1.4.5.8, summed by the patcher): for each NPC i, mark = 2 when
        // _pickedTarget != -1 && _targets.Count > 0 && i == _targets[_pickedTarget]; else 1 when (flag &&
        // _targets.Contains(i)) || (UseMode == 2 && _threeDSTarget == i) || (UseMode == 1 &&
        // _targetClosestTarget == i); else 0. Then p[i,0] = Clamp(p[i,0] + (mark == 1 ? 0.1 : -0.1), 0, 1),
        // p[i,1] likewise for mark 2, and a draw only where one of them is above 0. With no mark anywhere
        // and every value +0 (bits), each round stores +0 over +0 and draws nothing.
        public static long LockOn;

        public static unsafe bool LockOnIdle(float[,] progress, int picked, System.Collections.Generic.List<int> targets, int mode, int threeDS, int closest, bool flag)
        {
            if (!on || progress == null || targets == null || flag || progress.Length == 0 || progress.GetLength(1) < 2)
                return false;
            int n = progress.GetLength(0);
            if (picked != -1 && targets.Count > 0 || mode == 2 && (uint)threeDS < (uint)n || mode == 1 && (uint)closest < (uint)n)
                return false;
            fixed (float* p = &progress[0, 0])
            {
                int* bits = (int*)p;
                for (int k = 0, length = progress.Length; k < length; k++)
                    if (bits[k] != 0)
                        return false;
            }
            LockOn++;
            return true;
        }

        // PingMapLayer.Draw's count of pings: none, and the cut is on (the layer returns at once)
        public static bool NoPings(int count) => on && count == 0;

        // StateSnapshot.Restore (the game's testing code) puts objects back past every hook: the marks go,
        // and every projectile type counts as made from then on
        public static void Restored()
        {
            Array.Clear(plainB, 0, plainB.Length);
            Array.Clear(plainM, 0, plainM.Length);
            odd = true;
        }

        // ---- projectile types ever made
        static readonly byte[] made = new byte[1 << 12];
        static volatile bool odd;           // a type outside the table: every type counts as made

        // every "projectile.type = value" of the game: noted when the projectile is one of Main.projectile's
        // (the list is filled with new projectiles of type 0 and never takes another object: the patcher
        // checks), not for the game's samples of every type
        public static void ProjType(Projectile p, int value)
        {
            p.type = value;
            var list = Main.projectile;
            int k = p.whoAmI;
            if (list != null && ((uint)k < (uint)list.Length && list[k] == p || Array.IndexOf(list, p) >= 0))
            {
                if ((uint)value < (uint)made.Length)
                    made[value] = 1;
                else
                    odd = true;
            }
        }

        // was a projectile of this type ever made? (true when the cut is off). TR_SCAN_AUDIT=1: a "no"
        // is checked against every projectile
        static readonly bool audit = Environment.GetEnvironmentVariable("TR_SCAN_AUDIT") == "1";
        public static long Wrong;

        public static bool Made(int type)
        {
            if (!on || odd || (uint)type >= (uint)made.Length || made[type] != 0)
                return true;
            Skipped++;
            if (audit)
                foreach (var p in Main.projectile)
                    if (p != null && p.type == type)
                        Wrong++;
            return false;
        }

        public static string Line()
        {
            string s = $"ui bags {Bags} passing {Passed}, icons {Icons} passing {IconsPassed}, players passing {Seen}, lock-on {LockOn} idle, searches {Skipped} not begun{(audit ? $" wrong {Wrong}" : "")}";   // (FNAPatches) portmaster-terraria adds its FNA prep's counts here
            Bags = Passed = Skipped = Icons = IconsPassed = Seen = LockOn = 0;
            return s;
        }
    }


    // The game keeps its projectiles, NPCs, players and dust in fixed lists (1000, 200, 255 and 6000
    // slots) and asks every slot "active?" in dozens of loops a frame. An object is hundreds of bytes
    // and the lists are more than the H700's cache holds, so every question is a read from memory:
    // 85 to 240 ns a slot, 1 ms for one pass over the dust, about 3 ms of a 16.7 ms frame with next
    // to nothing in the lists (2026-11-11).
    // Here a slot seen inactive is remembered in two small arrays: idle[k] is set and seen[k] is the
    // object only while that object is inactive and k is its own index (whoAmI, dustIndex). Every write
    // of an "active" field and of an index field in the game goes through here (trpatch, Scans), so a
    // write of true, or of another index, takes the object's mark away at its index, where alone it can
    // be. A question about a marked object is answered false without touching it; any other question
    // goes to the object. The index that comes with a question is only where to look: the answer is
    // the object's field for any object and any index, and the game runs the same.
    // TR_SCAN=0: nothing is remembered. TR_SCAN_AUDIT=1 (tests): every mark is checked against its
    // object every update.
    public static class Scan
    {
        static readonly bool on = Environment.GetEnvironmentVariable("TR_SCAN") != "0";
        static readonly bool audit = Environment.GetEnvironmentVariable("TR_SCAN_AUDIT") == "1";
        public static bool Stop;        // tests (Parts.Sweep, "@scan"): every question goes to the object

        static readonly object[] seenP = new object[1001], seenN = new object[201], seenL = new object[256], seenD = new object[6001];
        static readonly byte[] idleP = new byte[1001], idleN = new byte[201], idleL = new byte[256], idleD = new byte[6001];
        static readonly object[] seenI = new object[401];
        static readonly byte[] idleI = new byte[401];
        static int wrong;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool Idle(object[] seen, byte[] idle, object o, int k) =>
            (uint)k < (uint)idle.Length && idle[k] != 0 && seen[k] == o && !Stop;

        // o was read inactive with k its index: marked. The caller reads it again after: another thread
        // may have woken it between (the world's loader sets entities up beside the menu).
        static void Rest(object[] seen, byte[] idle, object o, int k)
        {
            idle[k] = 0;
            seen[k] = o;
            idle[k] = 1;
            Thread.MemoryBarrier();
        }

        // o is active now, or k is no more its index
        static void Woke(object[] seen, byte[] idle, object o, int k)
        {
            Thread.MemoryBarrier();
            if ((uint)k < (uint)idle.Length && seen[k] == o)
                idle[k] = 0;
        }

        // ---- projectiles
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Active(Projectile o, int k) => !Idle(seenP, idleP, o, k) && Look(o, k);

        static bool Look(Projectile o, int k)
        {
            bool active = o.active;
            if (!active && on && !Stop && (uint)k < (uint)idleP.Length && o.whoAmI == k)
            {
                Rest(seenP, idleP, o, k);
                if (o.active || o.whoAmI != k) idleP[k] = 0;
            }
            return active;
        }

        public static void Off(Projectile o, int k)
        {
            if (Idle(seenP, idleP, o, k)) return;
            o.active = false;
            Look(o, k);
        }

        public static void Set(Projectile o, bool active)
        {
            o.active = active;
            if (active) Woke(seenP, idleP, o, o.whoAmI);
        }

        // Projectile.Update returns at once for an inactive projectile
        public static void Update(Projectile o, int i)
        {
            if (!Idle(seenP, idleP, o, i)) o.Update(i);
        }

        // ---- NPCs
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Active(NPC o, int k) => !Idle(seenN, idleN, o, k) && Look(o, k);

        static bool Look(NPC o, int k)
        {
            bool active = o.active;
            if (!active && on && !Stop && (uint)k < (uint)idleN.Length && o.whoAmI == k)
            {
                Rest(seenN, idleN, o, k);
                if (o.active || o.whoAmI != k) idleN[k] = 0;
            }
            return active;
        }

        public static void Off(NPC o, int k)
        {
            if (Idle(seenN, idleN, o, k)) return;
            o.active = false;
            Look(o, k);
        }

        public static void Set(NPC o, bool active)
        {
            o.active = active;
            if (active) Woke(seenN, idleN, o, o.whoAmI);
        }

        // NPC.UpdateNPC sets whoAmI to i and returns for an inactive NPC: a marked one has that index
        public static void Update(NPC o, int i)
        {
            if (!Idle(seenN, idleN, o, i)) o.UpdateNPC(i);
        }

        // ---- players
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Active(Player o, int k) => !Idle(seenL, idleL, o, k) && Look(o, k);

        static bool Look(Player o, int k)
        {
            bool active = o.active;
            if (!active && on && !Stop && (uint)k < (uint)idleL.Length && o.whoAmI == k)
            {
                Rest(seenL, idleL, o, k);
                if (o.active || o.whoAmI != k) idleL[k] = 0;
            }
            return active;
        }

        public static void Off(Player o, int k)
        {
            if (Idle(seenL, idleL, o, k)) return;
            o.active = false;
            Look(o, k);
        }

        public static void Set(Player o, bool active)
        {
            o.active = active;
            if (active) Woke(seenL, idleL, o, o.whoAmI);
        }

        // ---- dust
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Active(Dust o, int k) => !Idle(seenD, idleD, o, k) && Look(o, k);

        static bool Look(Dust o, int k)
        {
            bool active = o.active;
            if (!active && on && !Stop && (uint)k < (uint)idleD.Length && o.dustIndex == k)
            {
                Interlocked.Increment(ref busyD);
                if (k >= Volatile.Read(ref tailD)) Interlocked.Increment(ref versionD);
                Rest(seenD, idleD, o, k);
                if (o.active || o.dustIndex != k) idleD[k] = 0;
                Interlocked.Decrement(ref busyD);
            }
            return active;
        }

        public static void Off(Dust o, int k)
        {
            if (Idle(seenD, idleD, o, k)) return;
            bool was = o.active;
            o.active = false;
            if (was) Lower(o.dustIndex);
            Look(o, k);
        }

        public static void Set(Dust o, bool active)
        {
            if (!active)
            {
                bool was = o.active;
                o.active = false;
                if (was) Lower(o.dustIndex);
                return;
            }
            Interlocked.Increment(ref busyD);
            int k = o.dustIndex;
            if (k >= Volatile.Read(ref tailD)) Interlocked.Increment(ref versionD);
            o.active = true;
            Woke(seenD, idleD, o, k);
            Interlocked.Decrement(ref busyD);
        }

        // ---- the end of the dust list
        // Dust.UpdateDust writes "inactive" to every dust past the number the game draws (1000 to
        // 6000 of the 6000, by the quality setting) every update: thousands of questions for nothing.
        // tailD is where the marked end of the list starts: from there on every slot of Main.dust
        // holds its own marked object, so the rest of the game's loop has nothing to do and the loop
        // is left there (trpatch, Scans.Tail). What could make that untrue says so first: a mark made
        // or taken away from tailD on and an object put in the list change versionD, and busyD counts
        // the changes under way (on any thread); the end is then worked out again at the next update.
        static int tailD = int.MaxValue, lastD = int.MaxValue, knownD, versionD, busyD;
        static bool againD = true;
        public static long Tails;       // updates whose loop was left early (tests)

        public static bool Tail(int i)
        {
            if (!on || Stop) return false;
            if (i <= lastD && againD) Mend();
            lastD = i;
            if (i >= tailD && Volatile.Read(ref busyD) == 0 && Volatile.Read(ref versionD) == knownD)
            {
                Tails++;
                return true;
            }
            againD = true;
            return false;
        }

        static void Mend()
        {
            againD = false;
            Volatile.Write(ref tailD, int.MaxValue);
            int v = Volatile.Read(ref versionD);
            if (Volatile.Read(ref busyD) != 0)
            {
                againD = true;
                return;
            }
            Dust[] list = Main.dust;
            int t = Math.Min(Main.maxDust, list.Length);
            while (t > 0 && idleD[t - 1] != 0 && seenD[t - 1] == list[t - 1]) t--;
            knownD = v;
            Volatile.Write(ref tailD, t);
            Thread.MemoryBarrier();
            if (Volatile.Read(ref busyD) != 0 || Volatile.Read(ref versionD) != v)
            {
                Volatile.Write(ref tailD, int.MaxValue);
                againD = true;
            }
        }

        // an object was put in Main.dust, or the list itself changed
        public static void Moved()
        {
            Interlocked.Increment(ref versionD);
            Lower(0);
        }

        // ---- the first free dust
        // Dust.NewDust looks for the first inactive dust from slot 0, past every active one: 700 of
        // them in a fight, a hundred times a frame. Every slot below lowD holds its own active
        // object, so the search starts there (trpatch, Scans.First). A write of "inactive" and a
        // change of index lower it to the object's index, an object put in the list to 0; First
        // raises it past the active dust it reads, unless something was lowered meanwhile (stepD).
        static int lowD, stepD;
        public static long Firsts, Skipped;     // searches, and the slots they did not ask (tests)

        static void Lower(int k)
        {
            if (k < 0) k = 0;
            Interlocked.Increment(ref stepD);
            int now;
            while (k < (now = Volatile.Read(ref lowD)) && Interlocked.CompareExchange(ref lowD, k, now) != now) { }
        }

        public static int First()
        {
            if (!on || Stop) return 0;
            int step = Volatile.Read(ref stepD), from = Volatile.Read(ref lowD), k = from;
            Dust[] list = Main.dust;
            int n = Math.Min(list.Length, 6000);
            Dust o;
            while (k < n && (o = list[k]) != null && o.active && o.dustIndex == k) k++;
            if (k != from && Interlocked.CompareExchange(ref lowD, k, from) == from && Volatile.Read(ref stepD) != step)
                Lower(from);
            Firsts++;
            Skipped += from;
            if (audit)
                for (int j = 0; j < from; j++)
                    if (list[j] == null || !list[j].active)
                    {
                        wrong++;
                        Console.Error.WriteLine($"[trpatch] scan: dust {j} is free below {from}, where the search was to start");
                        return 0;
                    }
            return k;
        }

        // ---- the index fields: in place of a write of Entity.whoAmI, of Dust.dustIndex
        public static void Who(Entity o, int index)
        {
            int was = o.whoAmI;
            o.whoAmI = index;
            if (was == index) return;
            Woke(seenP, idleP, o, was);
            Woke(seenN, idleN, o, was);
            Woke(seenL, idleL, o, was);
            Woke(seenI, idleI, o, was);
        }

        // ---- world items: one is inactive (WorldItem.active) and air (IsAir) alike while its inner
        // item's type is 0. Marked so at its index, the inner item keeping the index + 1 (Item.trSlot):
        // every write of Item.type in the game comes through Type, a new inner item through Inner, the
        // item's index through Who, and a new object at the index is not the marked one.
        static bool Look(WorldItem o, int k)
        {
            var it = o.inner;
            bool air = it.type == 0;
            if (air && on && !Stop && (uint)k < (uint)idleI.Length && o.whoAmI == k)
            {
                ((Terraria.patch_Item)(object)it).trSlot = k + 1;
                Rest(seenI, idleI, o, k);
                if (it.type != 0 || o.inner != it || o.whoAmI != k) idleI[k] = 0;
            }
            return !air;
        }

        // every "item.type = value" of the game
        public static void Type(Item it, int value)
        {
            it.type = value;
            Interlocked.Increment(ref versionS);
            int s = ((Terraria.patch_Item)(object)it).trSlot;
            if (s != 0)
            {
                ((Terraria.patch_Item)(object)it).trSlot = 0;
                Thread.MemoryBarrier();
                if (s - 1 < idleI.Length)
                {
                    idleI[s - 1] = 0;
                }
            }
        }

        // WorldItem.inner's setter, after the store
        public static void Inner(WorldItem o)
        {
            Interlocked.Increment(ref versionS);
            Woke(seenI, idleI, o, o.whoAmI);
        }

        // Ui: player o is marked inactive at k
        internal static bool IdlePlayer(Player o, int k) => Idle(seenL, idleL, o, k);

        // Ui: o is marked air at k; o asked, and marked when air
        internal static bool Air(WorldItem o, int k) => Idle(seenI, idleI, o, k);
        internal static bool LookItem(WorldItem o, int k) => Look(o, k);

        public static void Index(Dust o, int index)
        {
            int was = o.dustIndex;
            if (was == index) return;
            Interlocked.Increment(ref busyD);
            if (was >= Volatile.Read(ref tailD)) Interlocked.Increment(ref versionD);
            o.dustIndex = index;
            Lower(was);
            Woke(seenD, idleD, o, was);
            Interlocked.Decrement(ref busyD);
        }

        // ---- loops (trpatch, Scans.Skip): a loop over a list whose every round begins with the
        // question and goes to the loop's step on "inactive". From slot i, the first slot below n
        // whose object does not answer "inactive" (that round is the game's to do), or -1 with none
        // left; the questions are the loop's own, in its order, the marked slots passed in a loop
        // of two small arrays and the list.
        public static long Loops, Passed;       // loops' heads, and the rounds they did not make (tests)

        public static int NextP(int i, int n)
        {
            var list = Main.projectile;
            if (!on || Stop || i < 0 || i >= n || n > list.Length || n > idleP.Length) return i;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (idleP[j] != 0 && seenP[j] == o)
                {
                    if (audit && o.active) wrong++;
                    continue;
                }
                if (o == null || Look(o, j))
                {
                    Loops++;
                    Passed += j - i;
                    return j;
                }
            }
            Loops++;
            Passed += n - i;
            return -1;
        }

        public static int NextN(int i, int n)
        {
            var list = Main.npc;
            if (!on || Stop || i < 0 || i >= n || n > list.Length || n > idleN.Length) return i;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (idleN[j] != 0 && seenN[j] == o)
                {
                    if (audit && o.active) wrong++;
                    continue;
                }
                if (o == null || Look(o, j))
                {
                    Loops++;
                    Passed += j - i;
                    return j;
                }
            }
            Loops++;
            Passed += n - i;
            return -1;
        }

        public static int NextL(int i, int n)
        {
            var list = Main.player;
            if (!on || Stop || i < 0 || i >= n || n > list.Length || n > idleL.Length) return i;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (idleL[j] != 0 && seenL[j] == o)
                {
                    if (audit && o.active) wrong++;
                    continue;
                }
                if (o == null || Look(o, j))
                {
                    Loops++;
                    Passed += j - i;
                    return j;
                }
            }
            Loops++;
            Passed += n - i;
            return -1;
        }

        public static int NextI(int i, int n)
        {
            var list = Main.item;
            if (!on || Stop || i < 0 || i >= n || n > list.Length || n > idleI.Length) return i;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (idleI[j] != 0 && seenI[j] == o)
                {
                    if (audit && o.inner.type != 0) wrong++;
                    continue;
                }
                if (o == null || Look(o, j))
                {
                    Loops++;
                    Passed += j - i;
                    return j;
                }
            }
            Loops++;
            Passed += n - i;
            return -1;
        }

        public static int NextD(int i, int n)
        {
            var list = Main.dust;
            if (!on || Stop || i < 0 || i >= n || n > list.Length || n > idleD.Length) return i;
            for (int j = i; j < n; j++)
            {
                var o = list[j];
                if (idleD[j] != 0 && seenD[j] == o)
                {
                    if (audit && o.active) wrong++;
                    continue;
                }
                if (o == null || Look(o, j))
                {
                    Loops++;
                    Passed += j - i;
                    return j;
                }
            }
            Loops++;
            Passed += n - i;
            return -1;
        }

        // ---- the merge of lying items (WorldItem.TryCombiningIntoNearbyItems): for each item, every
        // item after it, until its stack is full. Only one of the same type can be merged into it
        // (Item.CanStack: the same type first), and with 300-400 items lying each round reads two
        // objects of another item: 6 ms an update on the RK3326. So each slot's inner type is kept
        // with the count of writes it was read at (versionS: every write of an item's type, every new
        // inner item and every store into Main.item counts one, through Type, Inner and Stored), and
        // NextSame passes the slots of another type while nothing was written since. A round passed
        // is one whose first question would have been false (air, or CanStack false): it does nothing.
        static volatile int versionS = 1;
        static readonly WorldItem[] holdS = new WorldItem[401];
        static readonly int[] typeS = new int[401], stampS = new int[401];
        public static long SameRounds, SamePassed, SameRead;

        // Every slot's type is read again in one pass when the count of writes moved (Chain), and each
        // slot points to the next slot of its type, or of none known (nextS): the merge's walk from
        // the item's own slot is a walk along its type. A new object put in the list counts as a
        // write too (Stored, after the game's four stores into Main.item).
        static readonly int[] nextS = new int[401];
        static int[] lastOf = new int[0];
        static int builtS;

        public static void Stored() => Interlocked.Increment(ref versionS);

        static void Chain(WorldItem[] list, int n)
        {
            int v = versionS;
            int top = 0;
            for (int j = 0; j < n; j++)
            {
                var o = list[j];
                var inner = o?.inner;
                holdS[j] = o;
                typeS[j] = inner == null ? -1 : inner.type;
                stampS[j] = v;
                if (typeS[j] > top) top = typeS[j];
            }
            SameRead += n;
            if (lastOf.Length <= top)
                lastOf = new int[top + 64];
            for (int t = 0; t < lastOf.Length; t++)
                lastOf[t] = n;
            int unknown = n;
            for (int j = n - 1; j >= 0; j--)
            {
                int t = typeS[j];
                if (t < 0)
                {
                    nextS[j] = unknown;
                    unknown = j;
                    continue;
                }
                nextS[j] = Math.Min(lastOf[t], unknown);
                lastOf[t] = j;
            }
            builtS = v;
        }

        public static int NextSame(int i, int n, WorldItem self)
        {
            var list = Main.item;
            if (!on || Stop || !same || i < 1 || i >= n || n > list.Length || n >= holdS.Length || self.inner == null)
                return NextI(i, n);
            int t = self.inner.type;
            if (builtS != versionS)
                Chain(list, n);
            int c;
            if (typeS[i - 1] == t)
                c = nextS[i - 1];     // from the item's own slot, or from the one it last merged with
            else
            {
                // after a merge emptied the slot before (the chain read again): every slot of its type or unknown
                c = i;
                while (c < n && typeS[c] != t && typeS[c] >= 0)
                    c++;
            }
            if (audit)
                for (int j = i; j < c && j < n; j++)
                    if (list[j]?.inner != null && list[j].inner.type == t)
                        wrong++;
            SameRounds++;
            SamePassed += Math.Min(c, n) - i;
            return c < n ? c : -1;
        }

        static readonly bool same = Environment.GetEnvironmentVariable("TR_SAME") != "0";

        // ---- tests
        static int Count(byte[] idle) { int n = 0; foreach (byte b in idle) n += b; return n; }

        // marked slots: projectiles, NPCs, players, dust (and marks found wrong by the audit)
        public static string Line() =>
            $"idle {Count(idleP)} {Count(idleN)} {Count(idleL)} {Count(idleD)} {Count(idleI)} tail {(tailD == int.MaxValue ? -1 : tailD)} left {Tails} first {lowD} of {Firsts} skipping {Skipped / Math.Max(1, Firsts)} loops {Loops} passing {Passed} same {SameRounds} passing {SamePassed} read {SameRead}" + (audit ? $" wrong {wrong}" : "");

        public static void Audit()
        {
            if (!audit) return;
            for (int k = 0; k < idleP.Length; k++)
                if (idleP[k] != 0 && (((Projectile)seenP[k]).active || ((Projectile)seenP[k]).whoAmI != k)) wrong++;
            for (int k = 0; k < idleN.Length; k++)
                if (idleN[k] != 0 && (((NPC)seenN[k]).active || ((NPC)seenN[k]).whoAmI != k)) wrong++;
            for (int k = 0; k < idleL.Length; k++)
                if (idleL[k] != 0 && (((Player)seenL[k]).active || ((Player)seenL[k]).whoAmI != k)) wrong++;
            for (int k = 0; k < idleD.Length; k++)
                if (idleD[k] != 0 && (((Dust)seenD[k]).active || ((Dust)seenD[k]).dustIndex != k)) wrong++;
            for (int k = 0; k < idleI.Length; k++)
                if (idleI[k] != 0 && Main.item[k] == seenI[k] && (((WorldItem)seenI[k]).inner.type != 0 || ((WorldItem)seenI[k]).whoAmI != k)) wrong++;
        }
    }


    // The sky's stars are sprites of four textures, drawn in the order of the game's list, and FNA's
    // batch ends a draw call wherever the texture changes: about 150 calls for 150 stars, 1.5 ms of a
    // frame on the H700 from dusk to dawn. A star's colour has alpha 0 and the batch blends one,
    // 1 - alpha with the stock shader: every star is added to what is there, and a sum (with its clamp
    // at white) is the same in any order. So the stars of one Main.DrawStarsInBackground are held and
    // drawn texture by texture: four calls. The game's code runs in its order; only the draws move
    // (DrawCuts.Stars). A sprite that is not such a sum is drawn where it stood. TR_STARS=0: as the
    // game draws.
    public static class Stars
    {
        static readonly bool on = Environment.GetEnvironmentVariable("TR_STARS") != "0";
        public static bool Off;         // tests (Parts.Sweep, "@stars")
        public static int Held;

        struct Sprite
        {
            public Texture2D Texture;
            public Vector2 Position, Origin;
            public Color Color;
            public float Rotation, Scale;
            public SpriteEffects Effects;
        }

        const BindingFlags Own = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo blend = typeof(SpriteBatch).GetField("blendState", Own),
            sort = typeof(SpriteBatch).GetField("sortMode", Own), effect = typeof(SpriteBatch).GetField("customEffect", Own);
        // the held stars, each with the place of its texture in kinds; drawn texture by texture, in
        // the order a texture first came and, within one, in the order of the draws
        static Sprite[] held = new Sprite[256];
        static int[] kindOf = new int[256], order = new int[256];
        static int count;
        static readonly List<Texture2D> kinds = new List<Texture2D>();
        static int[] start = new int[8];
        static bool holding;

        // as DrawStarsInBackground starts
        public static void Begin(SpriteBatch batch)
        {
            holding = on && !Off && blend != null && sort != null && effect != null
                && blend.GetValue(batch) == BlendState.AlphaBlend && effect.GetValue(batch) == null
                && (SpriteSortMode)sort.GetValue(batch) == SpriteSortMode.Deferred;
        }

        // in place of DrawStar's SpriteBatch.Draw
        public static void Draw(SpriteBatch batch, Texture2D texture, Vector2 position, Rectangle? source, Color color,
            float rotation, Vector2 origin, float scale, SpriteEffects effects, float depth)
        {
            if (!holding || color.A != 0 || source.HasValue || depth != 0f)
            {
                End(batch, holding);
                batch.Draw(texture, position, source, color, rotation, origin, scale, effects, depth);
                return;
            }
            int kind = kinds.IndexOf(texture);
            if (kind < 0)
            {
                kind = kinds.Count;
                kinds.Add(texture);
            }
            if (count == held.Length)
            {
                Array.Resize(ref held, count * 2);
                Array.Resize(ref kindOf, count * 2);
                Array.Resize(ref order, count * 2);
            }
            held[count] = new Sprite { Texture = texture, Position = position, Origin = origin, Color = color,
                Rotation = rotation, Scale = scale, Effects = effects };
            kindOf[count++] = kind;
        }

        // as DrawStarsInBackground returns
        public static void End(SpriteBatch batch) => End(batch, false);

        static void End(SpriteBatch batch, bool more)
        {
            int n = count, k = kinds.Count;
            if (start.Length < k + 1)
                start = new int[k * 2 + 1];
            for (int i = 0; i <= k; i++)
                start[i] = 0;
            for (int i = 0; i < n; i++)
                start[kindOf[i] + 1]++;
            for (int i = 1; i <= k; i++)
                start[i] += start[i - 1];
            for (int i = 0; i < n; i++)
                order[start[kindOf[i]]++] = i;
            for (int j = 0; j < n; j++)
            {
                ref Sprite s = ref held[order[j]];
                batch.Draw(s.Texture, s.Position, null, s.Color, s.Rotation, s.Origin, s.Scale, s.Effects, 0f);
                s.Texture = null;
            }
            Held += n;
            count = 0;
            kinds.Clear();
            holding = more;
        }
    }

    // ---- the clouds, the dust out of sight and the water (wild1walker, portmaster-terraria runtime/Clouds.cs, Offscreen.cs,
    // Water.cs, MIT): TileConverter/DrawCuts.cs's Clouds, Dust and Water

    // The background's clouds are drawn one by one under the game's cloud shader. It reads the cloud's
    // texture five times for every pixel to light the edges that face the sun or the moon, and multiplies
    // that light by a strength; what it adds the light to is the texture times the colour, the stock
    // sprite shader's picture, which reads the texture once. On the H700's chip (240 MHz) the five reads
    // cost 5 to 7.5 ms of a 16.8 ms refresh: with them a sunset is 19 to 21 ms a frame, 30 frames a
    // second; without, 13 ms. So the clouds are drawn with FNA's sprite shader: the same vertices, the
    // same matrix (read back from the game's shader), the same blend. The lit edges are lost at sunrise,
    // at sunset and at night (a cut of the picture); by day the strength is 0 and nothing is.
    // NextHorizonRenderer.CloudsEnd asks here (DrawCuts.Clouds).
    // TR_CLOUDS=1: the sprite shader only while the strength is 0 (the same picture: on this chip one
    // cloud pixel in a hundred differs by 1 of 255, the two programs round differently);
    // TR_CLOUDS=0: the game's shader always.
    //
    // Runs. CloudsEnd then draws its clouds one draw call each (100 to 200 a frame by day, about 2 us of
    // the game thread each on the RK3326 with the GL thread), though under the sprite shader nothing
    // changes between two clouds but the texture: the shader, its matrix and the states are set before
    // the first. So with the sprite shader the clouds are drawn in runs (Single, in place of the
    // buffer's DrawSingle): one call for clouds that follow one another in the list with the same
    // texture, the same vertices in the same order (the chip blends the triangles of one call in their
    // order, as it does those of calls one after another). A cloud whose quad lies wholly beyond one
    // edge of the view (its four vertices through the shader's matrix, 5 % of the view's half width
    // past it: the shader's half-pixel shift and the chip's rounding cannot bring it back) puts nothing
    // on the screen whatever texture is bound: it does not end a run and is drawn with the run around
    // it, and a run neither starts nor ends with one. Under the game's shader every cloud has its own
    // values: drawn one by one, as the game does. TR_CLOUD_RUNS=0: one by one always.
    // TR_CLOUDS_TEST=<frames> with TR_CLOUDS_TEST_RUNS=1: every so many frames the clouds are drawn
    // one by one and in runs into two targets, and the pixels compared.
    public static class Clouds
    {
        static readonly string mode = Environment.GetEnvironmentVariable("TR_CLOUDS") ?? "2";
        static readonly bool on = mode != "0", always = mode != "1";
        // TR_CLOUDS_TEST=<frames>: every so many frames the clouds are drawn both ways into two targets
        // and the pixels compared (tests: the frame on the screen may be lost)
        static readonly int test = int.TryParse(Environment.GetEnvironmentVariable("TR_CLOUDS_TEST"), out int t) ? t : 0;
        static readonly bool testRuns = Environment.GetEnvironmentVariable("TR_CLOUDS_TEST_RUNS") == "1";
        static readonly bool runs = Environment.GetEnvironmentVariable("TR_CLOUD_RUNS") != "0";
        public static bool Off, Always; // tests (Parts.Sweep, "@clouds": the game's shader; "@plain": never)
        public static bool NoRuns;      // tests (Parts.Sweep, "@runs")
        public static int Plain, Full;
        public static long Calls, Unseen;       // the runs' draw calls, and the clouds out of view (tests)

        static Effect plain, gameShader;
        static EffectParameter matrix, gameMatrix;
        static bool flat, applied, zero;
        // the run being gathered: its first and last cloud in view and their texture; the shader's matrix
        static int runFirst = -1, runEnd;
        static Texture runTexture;
        static Matrix clip;
        static bool clipPlain;
        static MiscShaderData shader;
        static readonly List<DrawData> drawn = new List<DrawData>();
        static RenderTarget2D[] targets;
        static int tests;

        // the strength is the vector's z: set once, before the clouds are drawn
        public static MiscShaderData Strength(MiscShaderData s, Vector4 data)
        {
            zero = data.Z == 0f;
            flat = (zero || always || Always) && on && !Off;
            applied = false;
            runFirst = -1;
            clipPlain = false;
            drawn.Clear();
            return s.UseShaderSpecificData(data);
        }

        // before every cloud, in place of the game's shader
        public static void Apply(MiscShaderData s, DrawData? cloud)
        {
            if (test > 0 && cloud.HasValue) { shader = s; drawn.Add(cloud.Value); }
            if (!flat)
            {
                Full++;
                s.Apply(cloud);
                return;
            }
            Plain++;
            if (!applied)
                Flat(s, cloud);
            applied = true;
        }

        // The game's shader once, for the states and the matrix it sets, then the sprite shader with that
        // matrix; nothing between two clouds changes either.
        static void Flat(MiscShaderData s, DrawData? cloud)
        {
            s.Apply(cloud);
            if (plain == null)
            {
                var device = Main.graphics.GraphicsDevice;
                using (var code = typeof(Effect).Assembly.GetManifestResourceStream("Microsoft.Xna.Framework.Graphics.Effect.Resources.SpriteEffect.fxb"))
                using (var bytes = new MemoryStream())
                {
                    code.CopyTo(bytes);
                    plain = new Effect(device, bytes.ToArray());
                }
                matrix = plain.Parameters["MatrixTransform"];
            }
            // the game's shader's matrix (its parameter looked up by name once per shader)
            if (s.Shader != gameShader)
            {
                gameShader = s.Shader;
                gameMatrix = gameShader.Parameters["MatrixTransform"];
            }
            clip = gameMatrix.GetValueMatrix();
            matrix.SetValue(clip);
            plain.CurrentTechnique.Passes[0].Apply();
            // a transform that keeps w at 1: the clip test below reads x and y as they are
            clipPlain = clip.M14 == 0f && clip.M24 == 0f && clip.M34 == 0f && clip.M44 == 1f;
        }

        // in place of the buffer's DrawSingle, after Apply for the same cloud
        public static void Single(SpriteDrawBuffer buffer, int i)
        {
            if (!flat || !runs || NoRuns)
            {
                buffer.DrawSingle(i);
                return;
            }
            Run(buffer, i);
        }

        static void Run(SpriteDrawBuffer buffer, int i)
        {
            if (!InView(buffer, i))
            {
                Unseen++;
                return;
            }
            Texture texture = buffer.textures[i];
            if (runFirst >= 0 && texture == runTexture)
            {
                runEnd = i + 1;
                return;
            }
            Flush(buffer);
            runFirst = i;
            runEnd = i + 1;
            runTexture = texture;
        }

        static void Flush(SpriteDrawBuffer buffer)
        {
            if (runFirst < 0)
                return;
            buffer.DrawRange(runFirst, runEnd - runFirst);
            Calls++;
            runFirst = -1;
            runTexture = null;
        }

        // whether a cloud's quad may reach the view: not when its four vertices, through the shader's
        // matrix, lie beyond one edge of the clip square by 5 % of it
        const float Edge = 1.05f;
        static bool InView(SpriteDrawBuffer buffer, int i)
        {
            if (!clipPlain)
                return true;
            VertexPositionColorTexture[] v = buffer.vertices;
            int left = 0, right = 0, low = 0, high = 0;
            for (int k = i * 4; k < i * 4 + 4; k++)
            {
                Vector3 p = v[k].Position;
                float x = p.X * clip.M11 + p.Y * clip.M21 + p.Z * clip.M31 + clip.M41;
                float y = p.X * clip.M12 + p.Y * clip.M22 + p.Z * clip.M32 + clip.M42;
                if (x < -Edge) left++;
                else if (x > Edge) right++;
                if (y < -Edge) low++;
                else if (y > Edge) high++;
            }
            return left < 4 && right < 4 && low < 4 && high < 4;
        }

        // after the last cloud, in place of the buffer's Unbind
        public static void End(SpriteDrawBuffer buffer)
        {
            Flush(buffer);
            if (test > 0 && drawn.Count > 0 && ++tests % test == 0)
                try { Compare(buffer); }
                catch (Exception e) { Console.Error.WriteLine("[clouds] test: " + e.Message); }
            buffer.Unbind();
        }

        static void Compare(SpriteDrawBuffer buffer)
        {
            var device = Main.graphics.GraphicsDevice;
            var view = device.Viewport;
            var before = device.GetRenderTargets();
            if (targets == null || targets[0].Width != view.Width || targets[0].Height != view.Height)
            {
                targets = new RenderTarget2D[2];
                for (int k = 0; k < 2; k++)
                    targets[k] = new RenderTarget2D(device, view.Width, view.Height, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }
            var pixels = new Color[2][];
            for (int k = 0; k < 2; k++)
            {
                device.SetRenderTarget(targets[k]);
                device.Clear(Color.Transparent);
                for (int i = 0; i < drawn.Count; i++)
                {
                    if (testRuns)
                    {
                        // the sprite shader both times: one by one, then in runs
                        if (i == 0) Flat(shader, drawn[0]);
                        if (k == 0) buffer.DrawSingle(i);
                        else Run(buffer, i);
                        continue;
                    }
                    if (k == 0) shader.Apply(drawn[i]);
                    else if (i == 0) Flat(shader, drawn[0]);
                    buffer.DrawSingle(i);
                }
                Flush(buffer);
                pixels[k] = new Color[view.Width * view.Height];
            }
            device.SetRenderTargets(before);
            device.Viewport = view;
            for (int k = 0; k < 2; k++)
                targets[k].GetData(pixels[k]);
            int differ = 0, most = 0, shown = 0;
            for (int i = 0; i < pixels[0].Length; i++)
            {
                Color a = pixels[0][i], b = pixels[1][i];
                if (a.PackedValue != 0) shown++;
                if (a.PackedValue == b.PackedValue) continue;
                differ++;
                most = Math.Max(most, Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));
            }
            Console.Error.WriteLine($"[clouds] test: {drawn.Count} clouds, strength {(zero ? "0" : "not 0")}, {view.Width}x{view.Height}: "
                + $"{shown} pixels drawn, {differ} differ between " + (testRuns ? "one by one and runs" : "the game's shader and the sprite shader") + $" (most by {most})");
        }
    }

    // Main.DrawDust hands the sprite batch every dust within a rectangle 1000 pixels wider than the
    // screen on either side and 1050 taller above and below (what lies beyond it the game switches
    // off). On a 640 x 480 screen nearly all of that rectangle is out of sight. A sprite there costs
    // the batch its vertices (a sine and a cosine of the game's, sixteen products) and the chip its
    // four vertices, and the chip then clips it whole: no pixel is touched.
    //
    // Here DrawDust's own Begin, End and Draw of the batch come through (trpatch, DrawCuts.Dust), so
    // the batch's mode, effect and transform are known as DrawDust set them. While the batch is
    // deferred, with no effect of its own and a transform that only scales and moves, a sprite that
    // lies wholly beyond one edge of the view is not handed to the batch. Everything else of the
    // game's loop runs as before, in its order: the light and the colour of every dust are worked out
    // and a dust found black is switched off as the game does; only the batch's draws of sprites that
    // cannot reach the screen are left out.
    //
    // Beyond an edge: the sprite's quad lies within a circle around its position, of radius the scale
    // times the farthest corner of the source rectangle from the origin, at any rotation (the sum of
    // the two offsets bounds that distance). FNA's batch places the quad in the target's pixels as
    // position * transform, the view being [0, width] x [0, height] of the viewport (the batch's
    // projection, made from the viewport as the batch ends); the chip draws nothing outside it. With
    // the circle 4 pixels beyond an edge, the rounding of the batch's and the chip's arithmetic (well
    // under a pixel at these sizes) and the shader's half-pixel shift cannot bring a corner back.
    //
    // The light of a dust's tile (Lighting.GetColor, six places in DrawDust) is asked here as well: the
    // dust of a bullet's trail or of a hit lie on one tile, one after another in the list, and nothing
    // changes the lighting while the dust is drawn (the frame's lighting runs after the draw; with
    // TR_LIGHT_AHEAD on its own thread meanwhile: then every call goes to the game). The tile asked
    // just before gets the answer it got; the memory is dropped at every Begin and End.
    // TR_OFFSCREEN=0: every sprite drawn, every light asked. TR_OFFSCREEN_AUDIT=1 (tests): every sprite
    // drawn, and each one that would have been left out is placed corner by corner, as the batch places
    // it, and counted wrong unless all four corners lie beyond the same edge; a light given again is
    // asked of the game too and compared.
    public static class Offscreen
    {
        static readonly bool on = Environment.GetEnvironmentVariable("TR_OFFSCREEN") != "0";
        static readonly bool audit = Environment.GetEnvironmentVariable("TR_OFFSCREEN_AUDIT") == "1";
        public static bool Off;         // tests (Parts.Sweep, "@offscreen")
        public static long Drawn, Left; // sprites handed to the batch, and left out (tests)
        public static long Lit, Asked;  // lights given again, and asked of the game (tests)
        public static long Wrong;       // the audit's: corners found within the view, viewports changed

        const float Margin = 4f;
        static bool plain;
        static float m11, m22, m41, m42, width, height;
        static int viewWidth, viewHeight;

        // in place of DrawDust's SpriteBatch.Begin
        public static void Begin(SpriteBatch batch, SpriteSortMode sort, BlendState blend, SamplerState sampler,
            DepthStencilState depth, RasterizerState raster, Effect effect, Matrix transform)
        {
            batch.Begin(sort, blend, sampler, depth, raster, effect, transform);
            Viewport view = batch.GraphicsDevice.Viewport;
            viewWidth = view.Width;
            viewHeight = view.Height;
            plain = on && !Off && sort == SpriteSortMode.Deferred && effect == null && viewWidth > 0 && viewHeight > 0
                && transform.M12 == 0f && transform.M13 == 0f && transform.M14 == 0f
                && transform.M21 == 0f && transform.M23 == 0f && transform.M24 == 0f
                && transform.M31 == 0f && transform.M32 == 0f && transform.M34 == 0f && transform.M44 == 1f
                && Finite(transform.M11) && Finite(transform.M22) && Finite(transform.M41) && Finite(transform.M42);
            m11 = transform.M11;
            m22 = transform.M22;
            m41 = transform.M41;
            m42 = transform.M42;
            width = viewWidth;
            height = viewHeight;
            lightX = int.MinValue;
        }

        // in place of DrawDust's SpriteBatch.End: the batch makes its projection from the viewport now
        public static void End(SpriteBatch batch)
        {
            if (plain)
            {
                Viewport view = batch.GraphicsDevice.Viewport;
                if (view.Width != viewWidth || view.Height != viewHeight)
                {
                    Wrong++;
                    Console.Error.WriteLine($"[offscreen] the viewport changed within the dust's batch: {viewWidth}x{viewHeight} -> {view.Width}x{view.Height}");
                }
            }
            plain = false;
            lightX = int.MinValue;
            batch.End();
        }

        static int lightX = int.MinValue, lightY;
        static Color light;

        // in place of DrawDust's Lighting.GetColor(int, int)
        public static Color Light(int x, int y)
        {
            // (FNAPatches) portmaster-terraria also asks the game here while its lighting runs on a core of its own
            // (LightAhead.Holding); that thread is not in this port
            if (!on || Off)
                return Terraria.Lighting.GetColor(x, y);
            if (x == lightX && y == lightY)
            {
                Lit++;
                if (audit && Terraria.Lighting.GetColor(x, y).PackedValue != light.PackedValue)
                {
                    Wrong++;
                    if (Wrong <= 20)
                        Console.Error.WriteLine($"[offscreen] the light of tile {x},{y} changed while the dust was drawn");
                }
                return light;
            }
            Asked++;
            light = Terraria.Lighting.GetColor(x, y);
            lightX = x;
            lightY = y;
            return light;
        }

        // in place of DrawDust's SpriteBatch.Draw
        public static void Draw(SpriteBatch batch, Texture2D texture, Vector2 position, Rectangle? source, Color color,
            float rotation, Vector2 origin, float scale, SpriteEffects effects, float depth)
        {
            if (plain && Outside(texture, position, source, origin, scale))
            {
                Left++;
                if (!audit)
                    return;
                Check(texture, position, source, origin, rotation, scale);
            }
            else
                Drawn++;
            batch.Draw(texture, position, source, color, rotation, origin, scale, effects, depth);
        }

        static bool Finite(float x) => x > -1e7f && x < 1e7f;

        static bool Outside(Texture2D texture, Vector2 position, Rectangle? source, Vector2 origin, float scale)
        {
            float w, h;
            if (source.HasValue)
            {
                w = source.Value.Width;
                h = source.Value.Height;
            }
            else
            {
                w = texture.Width;
                h = texture.Height;
            }
            float reach = (Math.Max(Math.Abs(origin.X), Math.Abs(w - origin.X)) + Math.Max(Math.Abs(origin.Y), Math.Abs(h - origin.Y))) * Math.Abs(scale);
            float x = position.X * m11 + m41, y = position.Y * m22 + m42;
            float rx = reach * Math.Abs(m11) + Margin, ry = reach * Math.Abs(m22) + Margin;
            if (!(Finite(x) && Finite(y) && Finite(rx) && Finite(ry)))
                return false;
            return x + rx < 0f || x - rx > width || y + ry < 0f || y - ry > height;
        }

        // the audit: the four corners as FNA's batch places them (offsets from the origin, scaled,
        // turned by the rotation, moved to the position, then the transform), in double precision
        static void Check(Texture2D texture, Vector2 position, Rectangle? source, Vector2 origin, float rotation, float scale)
        {
            double w = source.HasValue ? source.Value.Width : texture.Width;
            double h = source.HasValue ? source.Value.Height : texture.Height;
            double s = Math.Sin(rotation), c = Math.Cos(rotation);
            int left = 0, right = 0, above = 0, below = 0;
            for (int k = 0; k < 4; k++)
            {
                double dx = ((k & 1) * w - origin.X) * scale, dy = ((k >> 1) * h - origin.Y) * scale;
                double x = (position.X + dx * c - dy * s) * m11 + m41;
                double y = (position.Y + dx * s + dy * c) * m22 + m42;
                if (x < -1) left++;
                if (x > width + 1) right++;
                if (y < -1) above++;
                if (y > height + 1) below++;
            }
            if (left == 4 || right == 4 || above == 4 || below == 4)
                return;
            Wrong++;
            if (Wrong <= 20)
                Console.Error.WriteLine($"[offscreen] a sprite left out reaches the view: at {position}, scale {scale}, rotation {rotation}, origin {origin}, {w}x{h}, view {width}x{height}");
        }

        public static string Line() => $"offscreen {Left} of {Left + Drawn}, lights {Lit} again {Asked} asked" + (audit ? $" wrong {Wrong}" : "");
    }

    // Main.oldDrawWater asks every tile of the screen and a margin, row by row, whether it holds
    // liquid; most hold none. The tiles are kept column after column (Tiles), so each question of a
    // row is a tile's height of the world away from the one before: on the RK3326 a cache miss each,
    // about 0.3 ms of a frame. Now, as the routine starts, the liquid of the tiles its loops walk is
    // copied column by column, in the order of memory, and the loop's first question (trpatch,
    // DrawCuts.Water) is answered from the copy. Nothing writes a tile's liquid while the routine
    // draws (it puts a fresh tile only where the grid holds none, which the copy is not made for);
    // a tile outside the copy is asked as before. TR_WATER=0: asked as before. TR_WATER_AUDIT=1
    // (tests): each answer from the copy is compared with the tile's.
    //
    // The copy's place and size are in Copy, a class with no static constructor (as Tiles): the
    // compiled loop reads them with no check that the class is ready. The counts are made per copy.
    public static unsafe class Water
    {
        static readonly bool on = Environment.GetEnvironmentVariable("TR_WATER") != "0";
        static readonly bool audit = Environment.GetEnvironmentVariable("TR_WATER_AUDIT") == "1";
        public static bool Off;         // tests (Parts.Sweep, "@water")
        public static long Copies, Copied, Asked, Wrong;
        static int room;

        public static class Copy
        {
            public static byte* data;
            public static int x0, y0, w, h;     // w 0: no copy (or every answer asked, TR_WATER_AUDIT)
        }

        // Main.oldDrawWater, as it starts: the loops' bounds as the routine works them out
        public static void Begin()
        {
            Copy.w = 0;
            if (!on || Off || Tiles.fastW == 0)
                return;
            float range = Main.drawToScreen ? 0f : Main.offScreenRange;
            Vector2 at = Main.screenPosition;
            int left = (int)((at.X - range) / 16f - 1f);
            int right = (int)((at.X + Main.screenWidth + range) / 16f) + 2;
            int top = (int)((at.Y - range) / 16f - 1f);
            int bottom = (int)((at.Y + Main.screenHeight + range) / 16f) + 5;
            if (left < 5) left = 5;
            if (right > Main.maxTilesX - 5) right = Main.maxTilesX - 5;
            if (top < 5) top = 5;
            if (bottom > Main.maxTilesY - 5) bottom = Main.maxTilesY - 5;
            int ax = left - 2, bx = right + 2, ay = top, by = bottom + 4;
            if (ax < 0) ax = 0;
            if (ay < 0) ay = 0;
            if (bx > Tiles.fastW) bx = Tiles.fastW;
            if (by > Tiles.fastH) by = Tiles.fastH;
            if (bx <= ax || by <= ay)
                return;
            int cw = bx - ax, ch = by - ay;
            if (room < cw * ch)
            {
                if (Copy.data != null)
                    Marshal.FreeHGlobal((IntPtr)Copy.data);
                Copy.data = null;
                room = 0;
                try
                {
                    Copy.data = (byte*)Marshal.AllocHGlobal(cw * ch * 5 / 4);
                    room = cw * ch * 5 / 4;
                }
                catch (OutOfMemoryException)
                {
                    return;     // no copy: every tile asked as before
                }
            }
            int height = Tiles.fastH;
            byte* c = Copy.data;
            for (int x = 0; x < cw; x++)
            {
                // (FNAPatches) the column's records (TileRecords: 14 bytes, the liquid at 4)
                byte* t = Tiles.data + (long)((ax + x) * height + ay) * Tiles.RecordSize + 4;
                byte* d = c + x;
                for (int y = 0; y < ch; y++, d += cw, t += Tiles.RecordSize)
                    *d = *t;
            }
            Copy.x0 = ax; Copy.y0 = ay; Copy.h = ch;
            Copies++;
            Copied += cw * ch;
            if (audit)
            {
                // every answer through Asked, compared there
                auditW = cw;
                return;
            }
            Copy.w = cw;
        }

        static int auditW;

        // in place of the loop's first Main.tile[x, y].liquid
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Liquid(int x, int y)
        {
            int dx = x - Copy.x0, dy = y - Copy.y0, w = Copy.w;
            if ((uint)dx < (uint)w && (uint)dy < (uint)Copy.h)
                return Copy.data[dy * w + dx];
            return Ask(x, y);
        }

        static int Ask(int x, int y)
        {
            byte* r = (byte*)Tiles.At(x, y);     // (FNAPatches) the record's liquid, at 4
            int v = r == null ? 0 : r[4];
            int dx = x - Copy.x0, dy = y - Copy.y0;
            if (audit && auditW > 0 && (uint)dx < (uint)auditW && (uint)dy < (uint)Copy.h && Copy.data[dy * auditW + dx] != v)
                Wrong++;
            Asked++;
            return v;
        }

        public static string Line() => $"water {Copies} copies of {Copied} tiles, {Asked} asked" + (audit ? $" wrong {Wrong}" : "");
    }
}
