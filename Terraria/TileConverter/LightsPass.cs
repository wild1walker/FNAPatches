using Mono.Cecil;

namespace TerrariaPatches.TileConvert
{
	// The legacy lighting's lights (LightsPatch.cs, wild1walker, MIT), for the MonoModRules in Terraria.Lights.mm.cs. The
	// runtime it calls (TrPatches.Lights) is woven into Terraria.exe itself.
	public static class LightsPass
	{
		public static int Apply(ModuleDefinition m) { global::LightsPatch.Open(m); return global::LightsPatch.Apply(m, m); }
	}
}
