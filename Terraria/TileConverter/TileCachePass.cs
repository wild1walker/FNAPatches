using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TerrariaPatches.TileConvert
{
	// The tile cache (TileCachePatch.cs, wild1walker, MIT), for the MonoModRules in Terraria.TileCache.mm.cs. The runtime it
	// calls (TrPatches.TileCache) is woven into Terraria.exe itself.
	public static class TileCachePass
	{
		public static int Look(ModuleDefinition m) { global::TileCachePatch.Look(m); return 0; }
		public static int Apply(ModuleDefinition m)
		{
			global::TileCachePatch.Open(m);
			int n = global::TileCachePatch.Apply(m, m);
			return n > 0 ? n + Pointers(m) : n;
		}

		// TrPatches.TileCache.GameParticles and GameLight hand the game's routines a null placeholder for the tile (the runtime
		// is compiled with Tile a class); after the conversion those routines take a pointer to the tile's record, which the
		// helpers are given as their fourth argument
		static int Pointers(ModuleDefinition m)
		{
			int n = 0;
			foreach (var helper in m.GetType("TrPatches.TileCache").Methods.Where(x => x.Name == "GameParticles" || x.Name == "GameLight"))
			{
				var holes = helper.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldnull).ToList();
				if (holes.Count != 1)
					throw new InvalidOperationException("TileCache." + helper.Name + ": " + holes.Count + " placeholders, 1 expected");
				holes[0].OpCode = OpCodes.Ldarg;
				holes[0].Operand = helper.Parameters[3];
				n++;
			}
			return n;
		}
	}
}
