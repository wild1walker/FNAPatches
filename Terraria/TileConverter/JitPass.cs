using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TerrariaPatches.TileConvert
{
	// TrPatches.Jit's three hooks (Terraria.Jit.mm.cs; portmaster-terraria calls them from its asset and eviction runtimes):
	// Start as Main.Initialize begins, World as WorldGen.playWorld begins, Tick as every update begins (each in the routine's own
	// body: MonoMod's orig_ copy where another patch wraps it)
	public static class JitPass
	{
		static MethodDefinition Own(TypeDefinition type, string name) =>
			type.Methods.Where(x => (x.Name == name || x.Name == "orig_" + name) && x.HasBody).OrderBy(x => x.Name == name ? 1 : 0).First();

		public static int Apply(ModuleDefinition m)
		{
			var jit = m.GetType("TrPatches.Jit");
			var main = m.GetType("Terraria.Main");
			foreach (var (method, call) in new[] {
				(Own(main, "Initialize"), "Start"),
				(Own(m.GetType("Terraria.WorldGen"), "playWorld"), "World"),
				(Own(main, "DoUpdate"), "Tick") })
			{
				var il = method.Body.GetILProcessor();
				il.InsertBefore(method.Body.Instructions[0], il.Create(OpCodes.Call, jit.Methods.Single(x => x.Name == call)));
			}
			return 3;
		}
	}
}
