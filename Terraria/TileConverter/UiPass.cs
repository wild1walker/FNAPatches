using Mono.Cecil;

namespace TerrariaPatches.TileConvert
{
	// The interface's cuts (UiCuts.cs, wild1walker, MIT), for the MonoModRules in Terraria.Speed.mm.cs. The runtime they call
	// (TrPatches.Ui) is woven into Terraria.exe itself.
	public static class UiPass
	{
		public static int Look(ModuleDefinition m) { global::UiCuts.Look(m); return 0; }
		public static int Apply(ModuleDefinition m) { return global::UiCuts.Apply(m, m); }
	}
}
