// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/LightsPatch.cs, MIT), except where marked (FNAPatches).
using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// LegacyLighting.AddLight asks TrPatches.Lights first and returns when the call cannot change anything;
// PreRenderPhase tells it after clearing the lights. The dictionary must be touched nowhere else: the
// patch is left out otherwise.
static class LightsPatch
{
    const string Type = "Terraria.Graphics.Light.LegacyLighting";

    // what TrPatches reads (the reference it is compiled against has the same: Program.Refs)
    public static void Open(ModuleDefinition m)
    {
        var legacy = m.GetType(Type);
        if (legacy == null)
            return;
        foreach (var f in legacy.Fields.Where(x => x.Name == "_tempLights" || x.Name == "_rgb"))
            f.IsPublic = true;
        var triplet = legacy.NestedTypes.FirstOrDefault(t => t.Name == "ColorTriplet");
        if (triplet != null)
            triplet.IsNestedPublic = true;
    }

    public static int Apply(ModuleDefinition m, ModuleDefinition tr)
    {
        Open(m);
        var legacy = m.GetType(Type);
        var lights = legacy.Fields.Single(f => f.Name == "_tempLights");
        var add = legacy.Methods.Single(x => x.Name == "AddLight" && x.Parameters.Count == 3);
        var phase = legacy.Methods.Single(x => x.Name == "PreRenderPhase");
        var rebuild = legacy.Methods.Single(x => x.Name == "Rebuild");
        string dictionary = lights.FieldType.FullName;
        bool Is(Instruction i) => i.Operand is FieldReference f && f.Name == lights.Name && f.DeclaringType.FullName == Type;
        // every use of the field: loaded in AddLight (to count, look up, write, add), PreRenderPhase (to
        // walk, then clear), stored in Rebuild; each load followed by its one call on the dictionary
        string[] allowed = { "get_Count", "TryGetValue", "set_Item", "Add", "GetEnumerator", "Clear" };
        foreach (var method in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody))
            foreach (var i in method.Body.Instructions.Where(Is))
            {
                bool ok = method == rebuild ? i.OpCode == OpCodes.Stfld
                    : (method == add || method == phase) && i.OpCode == OpCodes.Ldfld && Next(i) is MethodReference r
                        && r.DeclaringType.FullName == dictionary && allowed.Contains(r.Name)
                        && (method == phase) == (r.Name == "GetEnumerator" || r.Name == "Clear");
                if (!ok)
                {
                    Console.Error.WriteLine($"lights: {method.FullName} uses the lights' dictionary unlike the known game; left out");
                    return 0;
                }
            }
        var clears = phase.Body.Instructions.Where(i => Is(i) && ((MethodReference)Next(i)).Name == "Clear").ToList();
        if (clears.Count != 1 || add.Parameters[2].ParameterType.FullName != "Microsoft.Xna.Framework.Vector3")
        {
            Console.Error.WriteLine("lights: LegacyLighting is not as expected; left out");
            return 0;
        }
        var helper = tr.GetType("TrPatches.Lights");
        var il = phase.Body.GetILProcessor();
        il.InsertAfter(Call(clears[0]), il.Create(OpCodes.Call, m.ImportReference(helper.Methods.Single(x => x.Name == "Cleared"))));
        il = add.Body.GetILProcessor();
        var first = add.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_2));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_3));
        il.InsertBefore(first, il.Create(OpCodes.Call, m.ImportReference(helper.Methods.Single(x => x.Name == "Same"))));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        Console.Error.WriteLine("lights: a light that cannot change the tile's is not handed to the dictionary");
        return 2;
    }

    // the call made on the value an instruction loads: the next call, with only loads of arguments between
    static Instruction Call(Instruction i)
    {
        for (var n = i.Next; n != null; n = n.Next)
            if (n.OpCode == OpCodes.Call || n.OpCode == OpCodes.Callvirt)
                return n;
        return null;
    }

    static object Next(Instruction i) => Call(i)?.Operand;

    static System.Collections.Generic.IEnumerable<TypeDefinition> Nested(TypeDefinition t) =>
        new[] { t }.Concat(t.NestedTypes.SelectMany(Nested));
}
