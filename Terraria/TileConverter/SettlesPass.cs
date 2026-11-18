using Mono.Cecil;

namespace TerrariaPatches.TileConvert
{
	// Items lying still (Settles.cs, wild1walker, MIT), for the MonoModRules in Terraria.Settled.mm.cs. The runtime it calls
	// (TrPatches.Settled) is woven into Terraria.exe itself.
	public static class SettlesPass
	{
		public static int Look(ModuleDefinition m) { global::Settles.Look(m); return 0; }
		public static int Apply(ModuleDefinition m) { return global::Settles.Apply(m, m); }
	}
}
