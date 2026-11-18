using MonoMod.InlineRT;
using MonoMod;
using System.IO;
using System.Runtime;
using System.Threading;
using System;

#pragma warning disable 1591, 0649, 0169, 0414

// The first call of a method, done ahead (wild1walker, portmaster-terraria, MIT: runtime/Jit.cs): the runtime's "multi-core
// JIT" records the methods a session calls, and the next session has a thread of its own compile and load them ahead instead
// of the main thread in the middle of a frame (a first death, the first flock of birds: 30-60 ms each on the RK3326). Three
// records: "boot" as the game starts, "world" as a world starts to load, "later" some minutes in (TrPatches.Jit has the rules).
// The records are this handheld's own, beside the game (trpatch-jit, or TR_JIT_DIR). TR_JIT=0: none; TR_JIT_LATER=<seconds>:
// when "later" starts (300). TERRARIA_JIT=0 leaves the hooks out when patching.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_JIT") == "0")
				return;
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Jit, "jit", m => TerrariaPatches.TileConvert.JitPass.Apply(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
    // The first time a method is called the runtime has work to do on the calling thread: find its
    // code compiled at setup and load the types it names, or compile it where setup compiled none
    // (the libraries beside the game, the runtime's own stubs). In the world that is the main
    // thread, in the middle of a frame: a first death, the first flock of birds, 30 - 60 ms each
    // (docs/notes.md).
    //
    // The runtime can do that work ahead, on a thread of its own, from its record of the methods
    // the last session called (System.Runtime.ProfileOptimization, the "multi-core JIT"): it
    // compiles and loads, it runs none of the game's code, static constructors included. A record
    // is written when the next one is started or at a clean exit, and its thread works for a minute
    // at most. So three records, each started once: "boot" at the start, "world" as the first world
    // starts to load (WorldGen.playWorld; in multiplayer, at the first update in the world), "later"
    // some minutes after the entry (which writes "world" in a session that is ended by killing the
    // game). "world" starts with the load and not at the entry: a record takes half a second to
    // start, the first calls made meanwhile are in no record, and the thread then works through
    // the load screen instead of the first frames.
    //
    // What the runtime does not do there (dotnet/runtime 8.0, multicorejitplayer.cpp, prestub.cpp):
    // a method whose code compiled at setup it refuses is left to the thread that calls it; a
    // method is in the next record only if the game called it; and no record is written in a
    // session in which an assembly was not found and the game was asked for it (AssemblyResolve).
    //
    // The records are this handheld's own, of the player's sessions, beside the game
    // (trpatch-jit, or TR_JIT_DIR). TR_JIT=0: none. TR_JIT_LATER=<seconds>: when "later" starts (300).
    public static class Jit
    {
        static readonly bool slow = Environment.GetEnvironmentVariable("TR_SLOW") != null;   // (FNAPatches) tests: told when a record starts
        static int phase;       // 0: not started, 1: boot, 2: world (loading), 3: in the world, 4: later
        static long worldAt;
        static readonly int later = Later();

        static int Later() =>
            int.TryParse(Environment.GetEnvironmentVariable("TR_JIT_LATER"), out int s) && s > 0 ? s : 300;

        // Main.Initialize, as the game starts (TileConverter/JitPass.cs)
        // (FNAPatches) portmaster-terraria also asks .NET 10 for SIGTERM here (Term); PortMaster's exit hotkey kills with
        // SIGKILL (gptokeyb2: kill -9), which no handler sees, so it is left out
        public static void Start()
        {
            if (phase != 0 || Environment.GetEnvironmentVariable("TR_JIT") == "0")
                return;
            try
            {
                string root = Environment.GetEnvironmentVariable("TR_JIT_DIR");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(AppContext.BaseDirectory, "trpatch-jit");
                Directory.CreateDirectory(root);
                ProfileOptimization.SetProfileRoot(root);
                phase = 1;
                Begin("boot");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("[trpatch] jit records not used: " + e.Message);
            }
        }

        // WorldGen.playWorld: a world starts to load
        public static void World()
        {
            if (Interlocked.CompareExchange(ref phase, 2, 1) == 1)
                Begin("world");
        }

        // every update
        public static void Tick()
        {
            if (phase < 3)
            {
                if (phase == 0 || Terraria.Main.gameMenu)
                    return;
                World();
                phase = 3;
                worldAt = Environment.TickCount;
            }
            else if (phase == 3 && Environment.TickCount - worldAt > later * 1000L)
            {
                phase = 4;
                Begin("later");
            }
        }

        // off the main thread: the record before this one is written to the card here
        static void Begin(string name)
        {
            new Thread(() =>
            {
                try
                {
                    long t = System.Diagnostics.Stopwatch.GetTimestamp();
                    ProfileOptimization.StartProfile(name);
                    if (slow)
                        Console.Error.WriteLine($"[jit] record \"{name}\" started in {(System.Diagnostics.Stopwatch.GetTimestamp() - t) * 1000 / System.Diagnostics.Stopwatch.Frequency} ms");
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("[trpatch] jit record " + name + ": " + e.Message);
                }
            }) { IsBackground = true, Name = "trpatch jit" }.Start();
        }
    }
}
