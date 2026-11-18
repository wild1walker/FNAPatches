using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MonoMod;
using MonoMod.InlineRT;
using Terraria;

#pragma warning disable 1591, 0649, 0169, 0414

// Full collections where a pause is not seen (wild1walker, portmaster-terraria, MIT: runtime/Quiet.cs). The oldest generation
// of the heap grows through play (3-4 MB a minute in a fight on the RK3326) and is collected only when its budget is spent or
// memory is nearly full: then as one pause of 180-390 ms. So while the player is dead, the full map is open or the game is
// paused, and the heap has grown by TR_QUIET_GC_MB (48) since the last full collection, the collection is made there (the
// runtime's own, sooner); with less than TR_QUIET_GC_FREE MB available (96) also at a calm moment of play (no boss, no
// invasion or event), and with less than TR_QUIET_GC_FLOOR (64) at once: one pause rather than running out. At most once in
// two minutes. TR_QUIET_GC=0: none. TERRARIA_QUIET=0 leaves the hook out when patching.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_QUIET") == "0")
				return;
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Quiet, "quiet", m => TerrariaPatches.TileConvert.QuietPass.Apply(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
    public static class Quiet
    {
        static readonly bool on = Environment.GetEnvironmentVariable("TR_QUIET_GC") != "0";
        static readonly long growth = (int.TryParse(Environment.GetEnvironmentVariable("TR_QUIET_GC_MB"), out int mb) && mb > 0 ? mb : 48) * 1048576L;
        static long after = -1;     // the heap after the last full collection
        static readonly int tight = Mb("TR_QUIET_GC_FREE", 96), floor = Mb("TR_QUIET_GC_FLOOR", 64);
        static int fulls = -1;
        static int lastAt = int.MinValue / 2;    // Now of the last collection made here
        public static int Collections;

        static int Mb(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v >= 0 ? v : fallback;

        // nothing going on that a pause would spoil
        static bool Calm()
        {
            if (Main.invasionType != 0 || Main.bloodMoon || Main.eclipse || Main.pumpkinMoon || Main.snowMoon)
                return false;
            var npcs = Main.npc;
            for (int i = 0; i < npcs.Length; i++)
                if (npcs[i] != null && npcs[i].active && npcs[i].boss)
                    return false;
            return true;
        }

        // tests: TR_TRIM_EVERY=<seconds> hands the C library's free memory back to the system that
        // often (malloc_trim) and logs the process's anonymous memory before and after
        static readonly int trimEvery = Mb("TR_TRIM_EVERY", 0);
        static int trimAt;
        [DllImport("libc.so.6", EntryPoint = "malloc_trim")]
        static extern int MallocTrim(UIntPtr pad);

        static long Anon()
        {
            try
            {
                foreach (string l in System.IO.File.ReadLines("/proc/self/status"))
                    if (l.StartsWith("RssAnon:", StringComparison.Ordinal))
                        return long.Parse(l.Substring(8).Replace("kB", "").Trim());
            }
            catch (Exception) { }
            return -1;
        }

        // .NET's aggressive collection (GCCollectionMode.Aggressive, 3, from .NET 7) hands the memory
        // it frees back to the system; a forced one keeps it committed for the heap's next growth:
        // over a soak the committed memory rose with each peak of the heap (197 -> 260 MB) while the
        // heap after each full collection stayed at 181. Then the C library's free memory, too.
        // TR_QUIET_GC_AGGRESSIVE=0: forced, as before (and where the runtime does not know the mode).
        static string mode = Environment.GetEnvironmentVariable("TR_QUIET_GC_AGGRESSIVE") == "0" ? "forced" : "aggressive";

        static void Collect()
        {
            if (mode == "aggressive")
            {
                try
                {
                    GC.Collect(2, (GCCollectionMode)3, true, true);
                    MallocTrim(UIntPtr.Zero);
                    return;
                }
                catch (Exception e)
                {
                    mode = "forced";
                    Console.Error.WriteLine("[trpatch] aggressive collection: " + e.Message);
                }
            }
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }

        public static void Trim(string why)
        {
            try
            {
                long before = Anon();
                var t = Stopwatch.StartNew();
                MallocTrim(UIntPtr.Zero);
                Console.Error.WriteLine($"[trpatch] trim ({why}): anonymous {before >> 10} -> {Anon() >> 10} MB in {t.Elapsed.TotalMilliseconds:F1} ms");
            }
            catch (Exception e) { Console.Error.WriteLine("[trpatch] trim: " + e.Message); }
        }

        // (FNAPatches) portmaster-terraria reads the seconds and the memory available from its eviction (Evict.Now,
        // Evict.Free); this port's eviction is in ReLogic.dll, so they are kept here: seconds since start, and MemAvailable
        // read every 2 s
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static int Now => (int)(clock.ElapsedMilliseconds / 1000);
        static int Free => free;
        static int free, freeAt = -2;
        static void Available()
        {
            int now = Now;
            if (now - freeAt < 2)
                return;
            freeAt = now;
            try
            {
                foreach (string l in System.IO.File.ReadLines("/proc/meminfo"))
                    if (l.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        free = (int)(long.Parse(l.Substring(13).Replace("kB", "").Trim()) / 1024);
                        return;
                    }
            }
            catch (Exception) { free = 0; }
        }

        // Main.DoUpdate, as it starts (TileConverter/QuietPass.cs)
        public static void Tick()
        {
            Available();
            if (trimEvery > 0 && Now >= trimAt)
            {
                if (trimAt > 0)
                    Trim("test");
                trimAt = Now + trimEvery;
            }
            if (!on || Main.gameMenu)
                return;
            int n = GC.CollectionCount(2);
            if (n != fulls)
            {
                // a full collection since (the runtime's or this one): the heap it left is the mark
                fulls = n;
                after = GC.GetTotalMemory(false);
                return;
            }
            Player p = Main.LocalPlayer;
            string why = "quiet";
            if (!(p != null && p.dead || Main.mapFullscreen || Main.gamePaused))
            {
                int free = Free;
                if (free <= 0 || free >= tight || Now - lastAt < 120)
                    return;
                if (free >= floor)
                {
                    if (!Calm())
                        return;
                    why = $"calm, {free} MB available";
                }
                else
                    why = $"short, {free} MB available";
            }
            long heap = GC.GetTotalMemory(false);
            if (heap - after < growth)
                return;
            lastAt = Now;
            long anon = Anon();
            var t = Stopwatch.StartNew();
            Collect();
            fulls = GC.CollectionCount(2);
            after = GC.GetTotalMemory(false);
            long ms = t.ElapsedMilliseconds;
            Collections++;
            Console.Error.WriteLine($"[trpatch] {why} collection (dead {p?.dead}, map {Main.mapFullscreen}, paused {Main.gamePaused}, active {Main.instance?.IsActive}, inventory {Main.playerInventory}, options {Main.ingameOptionsWindow}): heap {heap >> 20} -> {after >> 20} MB{(anon >= 0 ? $", anonymous {anon >> 10} -> {Anon() >> 10} MB" : "")} in {ms} ms ({mode})");
        }
    }
}
