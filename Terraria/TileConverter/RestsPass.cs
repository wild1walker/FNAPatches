using Mono.Cecil;

namespace TerrariaPatches.TileConvert
{
	// The chest lids at rest (Rests.cs, wild1walker, MIT), for the MonoModRules in Terraria.Rests.mm.cs. The runtime it calls
	// (TrPatches.Rests) is woven into Terraria.exe itself.
	public static class RestsPass
	{
		public static int Look(ModuleDefinition m) { global::Rests.Look(m); return 0; }
		public static int Apply(ModuleDefinition m) { return global::Rests.Apply(m, m); }
	}
}
