// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/Settles.cs, MIT), except where marked (FNAPatches).
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Items lying still: the five questions an item's update asks of the blocks around it go through
// TrPatches.Settled, which keeps the answers. That holds only while each answer comes from what
// Settled compares: the item's place and size, what is handed in, the blocks, the tables named
// here, and nothing else. Look reads the five routines and all they call, on the game's code as
// shipped, and leaves the patch out where one reads or writes anything more. Every write of a
// table calls Settled.Touched, which has Settled compare the tables with its copy of them before
// the next answer; a table that goes where its writes cannot be seen leaves the patch out.
static class Settles
{
    const ulong Written = 0x17031bd5d882fb88;
    static bool known;

    static readonly string[] Five = { "ApplyConveyorBeltMovementToVelocity", "LavaCollision", "WetCollision", "TileCollision", "SlopeCollision" };
    static readonly string[] Wrappers = { "Belt", "Lava", "Wet", "Tile", "Slope" };
    // the flags each can leave changed, which Settled puts back with its answer
    static readonly string[][] Leaves = { new string[0], new string[0], new[] { "honey", "shimmer" }, new[] { "down", "up" },
        new[] { "down", "sloping", "stair", "stairFall", "up" } };
    static readonly string[] Tables = { "Terraria.Main::tileSolid", "Terraria.Main::tileSolidTop", "Terraria.ID.TileID/Sets::Platforms", "Terraria.ID.TileID/Sets::ConveyorDirection",
        "Terraria.ID.TileID/Sets/ForAdvancedCollision::ClosedDoors" };
    static readonly string[] Reads = { "Terraria.Main::tile", "Terraria.Main::maxTilesX", "Terraria.Main::maxTilesY", "Terraria.Collision::contacts" };

    static string Name(FieldReference f) => f.DeclaringType.FullName + "::" + f.Name;

    static IEnumerable<TypeDefinition> Nested(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Nested));

    static MethodDefinition Move(ModuleDefinition m) =>
        m.GetType("Terraria.WorldItem")?.Methods.FirstOrDefault(x => x.Name == "MoveInWorld" && x.HasBody);

    // the routine and all it calls in the game, into all; a reason where it is not what Settled takes it for
    static string Closure(ModuleDefinition m, MethodDefinition root, string[] leaves, SortedDictionary<string, MethodDefinition> all)
    {
        var seen = new HashSet<MethodDefinition> { root };
        var todo = new Queue<MethodDefinition>(seen);
        var read = new SortedSet<string>(StringComparer.Ordinal);
        var flagsRead = new SortedSet<string>(StringComparer.Ordinal);
        var flagsWritten = new SortedSet<string>(StringComparer.Ordinal);
        while (todo.Count > 0)
        {
            var method = todo.Dequeue();
            all[method.FullName] = method;
            foreach (var i in method.Body.Instructions)
            {
                var op = i.OpCode;
                string at = method.FullName + ": ";
                if (op == OpCodes.Calli || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn || op == OpCodes.Ldelema || op == OpCodes.Ldsflda
                    || op.Code >= Code.Stelem_I && op.Code <= Code.Stelem_Ref || op == OpCodes.Stelem_Any)
                    return at + op.Name;
                if (i.Operand is FieldReference f)
                {
                    bool flag = f.DeclaringType.FullName == "Terraria.Collision" && Leaves.Any(l => l.Contains(f.Name));
                    if (op == OpCodes.Stsfld)
                    {
                        if (!flag)
                            return at + "writes " + Name(f);
                        flagsWritten.Add(f.Name);
                    }
                    else if (op == OpCodes.Ldsfld)
                    {
                        if (flag)
                            flagsRead.Add(f.Name);
                        else if (!Tables.Contains(Name(f)) && !Reads.Contains(Name(f)))
                            return at + "reads " + Name(f);
                        else
                            read.Add(Name(f));
                    }
                    else
                    {
                        var d = f.Resolve();
                        if (d == null)
                            return at + Name(f) + " was not found";
                        var t = d.DeclaringType;
                        bool ok = t.IsValueType
                            || t.FullName == "Terraria.Tile" && op == OpCodes.Ldfld
                            || t.FullName == "Terraria.Entity" && (d.Name == "position" || op == OpCodes.Ldfld && (d.Name == "width" || d.Name == "height"));
                        if (!ok)
                            return at + op.Name + " " + Name(f);
                    }
                }
                if (i.Operand is MethodReference r && (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj))
                {
                    // a cell that holds no tile is given an empty one; Settled keeps nothing in a
                    // world that has such cells
                    bool fresh = op == OpCodes.Newobj && r.DeclaringType.FullName == "Terraria.Tile" && r.Parameters.Count == 0
                        && i.Next?.Operand is MethodReference put && put.DeclaringType is ArrayType && put.Name == "Set";
                    if (fresh)
                        continue;
                    if (r.DeclaringType is ArrayType)
                    {
                        if (r.Name != "Get" && !(r.Name == "Set" && i.Previous.OpCode == OpCodes.Newobj))
                            return at + "calls " + r.FullName;
                        continue;
                    }
                    var d = r.Resolve();
                    if (d == null)
                        return at + r.FullName + " was not found";
                    if (d.Module.Assembly.Name.Name == m.Assembly.Name.Name)
                    {
                        // the game's clamp of a number, whose comparing is the number's own
                        if (d.DeclaringType.FullName == "Terraria.Utils" && d.Name == "Clamp" && r is GenericInstanceMethod g
                            && g.GenericArguments.All(x => x.IsPrimitive))
                        {
                            all[d.FullName] = d;
                            continue;
                        }
                        if (!d.HasBody || d.IsVirtual)
                            return at + "calls " + d.FullName + ", which is not one routine";
                        if (seen.Add(d))
                            todo.Enqueue(d);
                        continue;
                    }
                    string type = d.DeclaringType.FullName;
                    bool list = type == "System.Collections.Generic.List`1" && (d.Name == "Clear" || d.Name == "Add" || d.Name == "get_Count" || d.Name == "get_Item");
                    if (!list && type != "Microsoft.Xna.Framework.Vector2" && type != "Microsoft.Xna.Framework.Vector4" && type != "System.Math" && type != "System.MathF")
                        return at + "calls " + r.FullName;
                }
            }
        }
        if (Environment.GetEnvironmentVariable("TRPATCH_DEBUG") != null)
            Console.Error.WriteLine($"settles: {root.Name}: {seen.Count} routines; reads {string.Join(" ", read)}; flags read {string.Join(" ", flagsRead)}; written {string.Join(" ", flagsWritten)}");
        if (!flagsWritten.SequenceEqual(leaves))
            return root.FullName + ": leaves the flags " + string.Join(" ", flagsWritten);
        if (!flagsRead.IsSubsetOf(flagsWritten))
            return root.FullName + ": reads the flags " + string.Join(" ", flagsRead);
        return null;
    }

    // on the game's code as shipped, before any other patch
    public static void Look(ModuleDefinition terraria)
    {
        var collision = terraria.GetType("Terraria.Collision");
        var move = Move(terraria);
        var all = new SortedDictionary<string, MethodDefinition>(StringComparer.Ordinal);
        string why = collision == null || move == null ? "the item's routine was not found" : null;
        for (int k = 0; k < Five.Length && why == null; k++)
        {
            var roots = collision.Methods.Where(x => x.Name == Five[k] && x.HasBody).ToList();
            why = roots.Count != 1 ? Five[k] + " is not one routine" : Closure(terraria, roots[0], Leaves[k], all);
        }
        if (why != null)
        {
            Console.Error.WriteLine($"settles: {why}; left out");
            return;
        }
        ulong h = 14695981039346656037;
        foreach (var method in new[] { move }.Concat(all.Values))
            foreach (var i in method.Body.Instructions)
                h = Guard.Sum(h, i);
        known = h == Written;
        if (!known)
            Console.Error.WriteLine($"settles: not the game this was written for (the item's routines sum to {h:x16}, expected {Written:x16}); left out");
    }

    // the instruction that takes what instruction q loads, and how deep under the stack's top it
    // then is (1: on top); null where a jump comes first
    static Instruction Use(IList<Instruction> code, int q, out int depth)
    {
        depth = 1;
        for (int j = q + 1; j < code.Count; j++)
        {
            var i = code[j];
            int pops = Rests.Pops(i);
            if (pops >= depth)
                return i;
            if (i.OpCode.FlowControl != FlowControl.Next && i.OpCode.FlowControl != FlowControl.Call)
                return null;
            depth += Rests.Pushes(i) - pops;
        }
        return null;
    }

    static bool Reading(Instruction i) =>
        i.OpCode == OpCodes.Ldlen || i.OpCode == OpCodes.Ldelem_Any || i.OpCode.Code >= Code.Ldelem_I1 && i.OpCode.Code <= Code.Ldelem_Ref;

    static bool Writing(Instruction i) =>
        i.OpCode == OpCodes.Stelem_Any || i.OpCode.Code >= Code.Stelem_I && i.OpCode.Code <= Code.Stelem_Ref;

    // the parameter an instruction loads, or -1
    static int Arg(Instruction i, MethodDefinition method)
    {
        int self = method.HasThis ? 1 : 0;
        switch (i.OpCode.Code)
        {
            case Code.Ldarg_0: return 0 - self;
            case Code.Ldarg_1: return 1 - self;
            case Code.Ldarg_2: return 2 - self;
            case Code.Ldarg_3: return 3 - self;
            case Code.Ldarg: case Code.Ldarg_S: return ((ParameterDefinition)i.Operand).Index;
            default: return -1;
        }
    }

    // a table handed to one of the game's routines as its parameter p
    static string Handed(MethodDefinition method, int p, int calls, Dictionary<MethodDefinition, List<Instruction>> tells)
    {
        if (p < 0 || method.Parameters[p].ParameterType.IsByReference)
            return "a table is handed to " + method.FullName + " by its address";
        var code = method.Body.Instructions;
        for (int k = 0; k < code.Count; k++)
        {
            var c = code[k].OpCode.Code;
            if ((c == Code.Starg || c == Code.Starg_S || c == Code.Ldarga || c == Code.Ldarga_S) && ((ParameterDefinition)code[k].Operand).Index == p)
                return method.FullName + " does more than read the table handed to it";
            if (Arg(code[k], method) == p)
            {
                string why = Follow(method, code, k, 0, calls, tells);
                if (why != null)
                    return why + " in " + method.FullName;
            }
        }
        return null;
    }

    static readonly HashSet<MethodDefinition> givers = new HashSet<MethodDefinition>();
    static readonly Queue<MethodDefinition> given = new Queue<MethodDefinition>();

    static void Tell(Dictionary<MethodDefinition, List<Instruction>> tells, MethodDefinition method, Instruction after)
    {
        if (!tells.TryGetValue(method, out var list))
            tells[method] = list = new List<Instruction>();
        if (!list.Contains(after))
            list.Add(after);
    }

    // what becomes of the table that instruction q loads: read, or written or handed to code
    // outside the game, with the instruction to tell after put into tells; a reason otherwise
    static string Follow(MethodDefinition method, IList<Instruction> code, int q, int aliases, int calls, Dictionary<MethodDefinition, List<Instruction>> tells)
    {
        var i = Use(code, q, out int depth);
        if (i == null)
            return "a table goes past a jump";
        if (Reading(i))
            return null;
        if (i.OpCode == OpCodes.Ret && method.ReturnType.IsArray)
        {
            // a routine that gives a table out: its callers are followed the same way
            if (givers.Add(method))
                given.Enqueue(method);
            return null;
        }
        if (Writing(i))
        {
            if (depth != 3)
                return "a table is put into a list";
            Tell(tells, method, i);
            return null;
        }
        if (i.OpCode == OpCodes.Ldelema && depth == 2)
        {
            // the address of one entry: read through, written through, or handed to a routine,
            // which cannot keep it past its return
            var use = Use(code, code.IndexOf(i), out _);
            if (use == null)
                return "the address of a table's entry goes past a jump";
            string name = use.OpCode.Name;
            if (name.StartsWith("ldind") || name == "ldobj")
                return null;
            if (name.StartsWith("stind") || name == "stobj" || name == "call" || name == "callvirt")
            {
                Tell(tells, method, use);
                return null;
            }
            return "the address of a table's entry meets " + name;
        }
        if (i.OpCode == OpCodes.Stloc && aliases > 0)
        {
            if (code.Any(x => x.OpCode == OpCodes.Ldloca && x.Operand == i.Operand))
                return "the address of a local that holds a table is taken";
            for (int k = 0; k < code.Count; k++)
                if (code[k].OpCode == OpCodes.Ldloc && code[k].Operand == i.Operand)
                {
                    string why = Follow(method, code, k, aliases - 1, calls, tells);
                    if (why != null)
                        return why;
                }
            return null;
        }
        if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference r)
        {
            string type = r.DeclaringType.FullName;
            if (type == "System.Array" || type == "System.Buffer")
            {
                Tell(tells, method, i);
                return null;
            }
            var d = r.Resolve();
            if (d != null && d.Module == method.Module && d.HasBody && !d.IsVirtual && calls > 0)
                return Handed(d, d.Parameters.Count - depth, calls - 1, tells);
            return "a table is handed to " + r.FullName;
        }
        return "a table meets " + i.OpCode.Name;
    }

    public static int Apply(ModuleDefinition m, ModuleDefinition tr)
    {
        if (!known)
            return 0;
        var settled = tr.GetType("TrPatches.Settled");
        MethodReference To(string name) => m.ImportReference(settled.Methods.Single(x => x.Name == name));
        var collision = m.GetType("Terraria.Collision");
        var move = Move(m);
        var sites = new List<Instruction>[Five.Length];
        for (int k = 0; k < Five.Length; k++)
            sites[k] = move.Body.Instructions.Where(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference r && r.Name == Five[k]
                && r.DeclaringType.FullName == collision.FullName
                && r.Parameters.Count == settled.Methods.Single(y => y.Name == Wrappers[k]).Parameters.Count).ToList();
        if (!sites.Select(s => s.Count).SequenceEqual(new[] { 1, 1, 1, 2, 1 }))
        {
            Console.Error.WriteLine("settles: the item's routine does not ask its five questions as expected; left out");
            return 0;
        }

        // the writes of a table
        var methods = m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody).ToList();
        var tells = new Dictionary<MethodDefinition, List<Instruction>>();
        var simplified = new List<MethodDefinition>();
        string left = null;
        foreach (var method in methods)
        {
            if (!method.Body.Instructions.Any(x => x.Operand is FieldReference f && Tables.Contains(Name(f))))
                continue;
            method.Body.SimplifyMacros();
            simplified.Add(method);
            var code = method.Body.Instructions;
            string why = null;
            for (int q = 0; q < code.Count && why == null; q++)
            {
                if (!(code[q].Operand is FieldReference f) || !Tables.Contains(Name(f)))
                    continue;
                if (code[q].OpCode == OpCodes.Stsfld)
                    Tell(tells, method, code[q]);
                else if (code[q].OpCode == OpCodes.Ldsfld)
                    why = Follow(method, code, q, 2, 2, tells);
                else
                    why = "the address of a table is taken";
            }
            if (why != null)
            {
                left = $"settles: {method.FullName}: {why}; left out";
                break;
            }
        }
        while (left == null && given.Count > 0)
        {
            var giver = given.Dequeue();
            foreach (var method in methods)
            {
                if (!method.Body.Instructions.Any(x => x.Operand is MethodReference r && r.Name == giver.Name && r.Resolve() == giver))
                    continue;
                if (!simplified.Contains(method))
                {
                    method.Body.SimplifyMacros();
                    simplified.Add(method);
                }
                var code = method.Body.Instructions;
                string why = null;
                for (int q = 0; q < code.Count && why == null; q++)
                    if (code[q].Operand is MethodReference r && r.Name == giver.Name && r.Resolve() == giver)
                        why = code[q].OpCode == OpCodes.Call || code[q].OpCode == OpCodes.Callvirt
                            ? Follow(method, code, q, 2, 2, tells) : "a routine that gives a table out is not called but named";
                if (why != null)
                {
                    left = $"settles: {method.FullName}: {why}; left out";
                    break;
                }
            }
        }
        if (left != null)
        {
            foreach (var method in simplified)
                method.Body.OptimizeMacros();
            Console.Error.WriteLine(left);
            return 0;
        }
        var touched = To("Touched");
        int n = 0;
        foreach (var pair in tells)
        {
            pair.Key.Body.SimplifyMacros();
            var edit = pair.Key.Body.GetILProcessor();
            foreach (var x in pair.Value)
                edit.InsertAfter(x, edit.Create(OpCodes.Call, touched));
            n += pair.Value.Count;
        }
        foreach (var method in simplified.Concat(tells.Keys).Distinct())
            method.Body.OptimizeMacros();

        for (int k = 0; k < Five.Length; k++)
            foreach (var x in sites[k])
                x.Operand = To(Wrappers[k]);
        Console.Error.WriteLine($"settles: an item lying still keeps the answers of its five questions; {n} writes of a table tell");
        return n + 6;
    }
}
