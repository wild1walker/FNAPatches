using MonoMod.InlineRT;
using MonoMod;
using System.Collections.Generic;
using System.Threading;
using System;
using Terraria;

#pragma warning disable 1591, 0649, 0169, 0414

// Chest lids at rest (wild1walker, portmaster-terraria, MIT: tools/trpatch/Rests.cs and runtime/Rests.cs): sixty times a
// second the game walks the 8000 places of its chest list and moves each chest's lid counters; the walk now passes the places
// TrPatches.Rests has marked as at rest (nobody has the chest open and its lid is shut), and every write of a lid's field or
// to the list outside that walk clears the mark. The pass applies only to the routines of 1.4.5.8 it was written for (sums of
// their code as shipped). TR_RESTS=0: the walk as before; TR_RESTS_AUDIT=1 checks. TERRARIA_RESTS=0 leaves the pass out.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_SPEED") == "0" || Environment.GetEnvironmentVariable("TERRARIA_RESTS") == "0")
				return;
			// the sums are taken of the game's code as shipped: here, before MonoMod weaves anything (the weave rewrites the
			// bodies' encoding, and the sums take each instruction's offset)
			TerrariaPatches.TileConvert.RestsPass.Look(MonoModRule.Modder.Module);
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Rests, "rests", m => TerrariaPatches.TileConvert.RestsPass.Apply(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
    // Chest lids. Sixty times a second the game walks the 8000 places of its chest list and moves
    // each chest's lid counters; for a chest nobody has open and whose lid is shut that leaves
    // them as they were. Such a place (an empty one too) is marked here once the game's walk has
    // been over it, and the walk passes the marked ones; with every place marked it is not begun.
    // A mark stands only while nothing but the walk writes a lid's fields or the list: the
    // patcher has every such write call Dirty, which takes all marks away.
    // TR_RESTS=0 leaves it out; TR_RESTS_AUDIT=1 passes nothing and checks each mark instead.
    public static class Rests
    {
        const int Places = 8000;
        static readonly byte[] rest = new byte[Places];
        static readonly bool on = Environment.GetEnvironmentVariable("TR_RESTS") != "0";
        static readonly bool audit = Environment.GetEnvironmentVariable("TR_RESTS_AUDIT") == "1";
        static int awake = Places, dirty = 1, wrong;
        static long walks, passed;

        public static void Dirty() => Volatile.Write(ref dirty, 1);

        static bool Shut(Chest c) => c == null || (c.frameCounter == 0 && c.eatingAnimationTime == 0 && c.frame == 0);

        // where the walk begins: at the list's start, or past its end with nothing to do
        public static int Begin(HashSet<int> used)
        {
            if (!on)
                return 0;
            if (Interlocked.Exchange(ref dirty, 0) != 0)
            {
                Array.Clear(rest, 0, Places);
                awake = Places;
            }
            foreach (int k in used)
                if ((uint)k < Places && rest[k] != 0)
                {
                    rest[k] = 0;
                    awake++;
                }
            if (awake == 0 && !audit)
            {
                passed++;
                return int.MaxValue;
            }
            walks++;
            return 0;
        }

        // the walk at place j: true to pass it
        public static bool Rest(int j)
        {
            if ((uint)j >= Places || rest[j] == 0)
                return false;
            if (!audit)
                return true;
            if (!Shut(Main.chest[j]))
                Wrong(j, "before");
            return false;
        }

        // the walk's work at place j is done
        public static void Done(Chest c, int j)
        {
            if (!on || (uint)j >= Places)
                return;
            if (rest[j] != 0)
            {
                if (!Shut(c))
                    Wrong(j, "after");
            }
            else if (Shut(c))
            {
                rest[j] = 1;
                awake--;
            }
        }

        static void Wrong(int j, string when)
        {
            if (wrong++ < 20)
                Console.Error.WriteLine($"[trpatch] rests: chest {j} was taken to be at rest and is not, {when} the game's own work on it");
        }

        public static string Line() => $"chests {walks} walked {passed} passed {awake} awake" + (audit ? $" wrong {wrong}" : "");
    }
}
