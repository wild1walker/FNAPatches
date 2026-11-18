using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TerrariaPatches.TileConvert
{
	// The sections' tiles framed ahead (FramesPatch.cs, wild1walker, MIT), for the MonoModRules in Terraria.Frames.mm.cs. The
	// runtime it calls (TrPatches.Frames) is woven into Terraria.exe itself.
	public static class FramesPass
	{
		public static int Apply(ModuleDefinition m) { return global::FramesPatch.Apply(m, m); }

		// TrPatches.Frames.GameAllowsSandfall and GameCosmetic hand WorldGen.AllowsSandfall and TileFrameCosmetic a null
		// placeholder for the tile (the runtime is compiled with Tile a class); after the tile conversion those routines take a
		// pointer to the tile's record, which the helpers are given (their tile parameter)
		public static int Pointers(ModuleDefinition m)
		{
			int n = 0;
			foreach (var helper in m.GetType("TrPatches.Frames").Methods.Where(x => x.Name == "GameAllowsSandfall" || x.Name == "GameCosmetic"))
			{
				var holes = helper.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldnull).ToList();
				if (holes.Count != 1)
					throw new InvalidOperationException("Frames." + helper.Name + ": " + holes.Count + " placeholders, 1 expected");
				holes[0].OpCode = OpCodes.Ldarg;
				holes[0].Operand = helper.Parameters.Single(p => p.Name == "tile");
				// and the call names the routine as it is now (its tile a pointer): the reference the runtime was compiled with
				// still has the tile as an object, which no routine has any more
				var call = helper.Body.Instructions.Single(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt);
				var named = (MethodReference)call.Operand;
				call.Operand = m.GetType(named.DeclaringType.FullName).Methods.Single(x => x.Name == named.Name && x.Parameters.Count == named.Parameters.Count);
				n++;
			}
			if (n != 2)
				throw new InvalidOperationException("Frames: " + n + " placeholder helpers, 2 expected");
			return n;
		}
	}
}
