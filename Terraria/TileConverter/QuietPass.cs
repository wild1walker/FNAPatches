using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TerrariaPatches.TileConvert
{
	// TrPatches.Quiet.Tick at the start of every update (Terraria.Quiet.mm.cs; portmaster-terraria inserts it the same way,
	// Install.cs): in Main.DoUpdate's own body, or MonoMod's orig_DoUpdate where another patch wraps it
	public static class QuietPass
	{
		public static int Apply(ModuleDefinition m)
		{
			var tick = m.GetType("TrPatches.Quiet").Methods.Single(x => x.Name == "Tick");
			var update = m.GetType("Terraria.Main").Methods.Where(x => (x.Name == "orig_DoUpdate" || x.Name == "DoUpdate") && x.HasBody)
				.OrderBy(x => x.Name == "orig_DoUpdate" ? 0 : 1).First();
			var il = update.Body.GetILProcessor();
			il.InsertBefore(update.Body.Instructions[0], il.Create(OpCodes.Call, tick));
			return 1;
		}
	}
}
