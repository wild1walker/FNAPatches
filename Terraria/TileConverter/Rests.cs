// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/Rests.cs, MIT), except where marked (FNAPatches).
// (FNAPatches) the whole file now; Stack.cs held its stack helpers (Pops, Pushes) for the scans.
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Chest lids: the game's walk over its chest list passes the places TrPatches.Rests has marked
// as at rest, and every write of a lid's field or to the list outside that walk calls
// Rests.Dirty. Left out where a write could not be seen: the address of a field or of a place
// taken, or the list handed to code that is not followed.
static class Rests
{
    const ulong Written = 0x926d4d810c3a78f3;
    static bool known;

    static IEnumerable<TypeDefinition> Nested(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Nested));

    static bool Is(object operand, FieldDefinition field) =>
        operand is FieldReference f && f.Name == field.Name && f.DeclaringType.FullName == field.DeclaringType.FullName;

    static MethodDefinition Walk(ModuleDefinition m) =>
        m.GetType("Terraria.Chest")?.Methods.FirstOrDefault(x => x.Name == "UpdateChestFrames" && x.HasBody);

    // on the game's code as shipped, before any other patch
    public static void Look(ModuleDefinition terraria)
    {
        ulong h = 14695981039346656037;
        var walk = Walk(terraria);
        if (walk != null)
            foreach (var i in walk.Body.Instructions)
                h = Guard.Sum(h, i);
        known = h == Written;
        if (!known)
            Console.Error.WriteLine($"rests: not the game this was written for (its chest lid routine sums to {h:x16}, expected {Written:x16}); left out");
    }

    internal static int Pops(Instruction i)
    {
        if (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
        {
            var r = (MethodReference)i.Operand;
            return r.Parameters.Count + (r.HasThis && i.OpCode != OpCodes.Newobj ? 1 : 0);
        }
        switch (i.OpCode.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi:
                return 2;
            case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
            case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref:
                return 3;
            default: return 99;
        }
    }

    internal static int Pushes(Instruction i)
    {
        if (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            return ((MethodReference)i.Operand).ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
        switch (i.OpCode.StackBehaviourPush)
        {
            case StackBehaviour.Push0: return 0;
            case StackBehaviour.Push1_push1: return 2;
            default: return 1;
        }
    }

    static bool Outside(Instruction i) =>
        (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference r
        && (r.DeclaringType.FullName == "System.Array" || r.DeclaringType.Namespace == "Terraria.Testing");

    // what becomes of the list that instruction q loads: an element read or written, its length,
    // a local used the same ways, or a call whose writes are told (Outside); anything else: false
    static bool Followed(IList<Instruction> code, int q, int aliases)
    {
        int depth = 1;
        for (int j = q + 1; j < code.Count; j++)
        {
            var i = code[j];
            int pops = Pops(i);
            if (pops >= depth)
            {
                if (i.OpCode == OpCodes.Ldelem_Ref || i.OpCode == OpCodes.Ldlen || Outside(i))
                    return true;
                if (i.OpCode == OpCodes.Stelem_Ref)
                    return depth == 3;
                if (i.OpCode == OpCodes.Stloc && aliases > 0)
                {
                    for (int k = 0; k < code.Count; k++)
                        if (code[k].OpCode == OpCodes.Ldloc && code[k].Operand == i.Operand && !Followed(code, k, aliases - 1))
                            return false;
                    return !code.Any(x => x.OpCode == OpCodes.Ldloca && x.Operand == i.Operand);
                }
                return false;
            }
            if (i.OpCode.FlowControl != FlowControl.Next && i.OpCode.FlowControl != FlowControl.Call)
                return false;
            depth += Pushes(i) - pops;
        }
        return false;
    }

    public static int Apply(ModuleDefinition m, ModuleDefinition tr)
    {
        if (!known)
            return 0;
        var chest = m.GetType("Terraria.Chest");
        var list = m.GetType("Terraria.Main").Fields.Single(x => x.Name == "chest");
        var used = chest.Fields.Single(x => x.Name == "_chestInUse");
        var lids = new[] { "frameCounter", "frame", "eatingAnimationTime" }.Select(n => chest.Fields.Single(x => x.Name == n)).ToArray();
        var walk = Walk(m);
        var rests = tr.GetType("TrPatches.Rests");
        MethodReference To(string name) => m.ImportReference(rests.Methods.Single(x => x.Name == name));
        var methods = m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody).ToList();
        // the testing code's putting back of a kept state, which the list is part of
        var restore = m.GetType("Terraria.Testing.StateSnapshot")?.Methods.Where(x => x.Name == "Restore" && x.HasBody && x.ReturnType.MetadataType == MetadataType.Void).ToList()
            ?? new List<MethodDefinition>();

        foreach (var method in methods)
        {
            method.Body.SimplifyMacros();
            var code = method.Body.Instructions;
            bool has = code.Any(x => Is(x.Operand, list));
            string why = null;
            for (int q = 0; q < code.Count && why == null; q++)
            {
                var i = code[q];
                if (i.OpCode == OpCodes.Ldflda && lids.Any(f => Is(i.Operand, f)))
                    why = "the address of a lid's field is taken";
                else if (i.OpCode == OpCodes.Ldsflda && Is(i.Operand, list))
                    why = "the address of the list is taken";
                else if (has && i.OpCode == OpCodes.Ldelema && i.Operand is TypeReference t && t.FullName == chest.FullName)
                    why = "the address of a place in the list is taken";
                else if (i.OpCode == OpCodes.Ldsfld && Is(i.Operand, list) && !Followed(code, q, 3))
                    why = "the list goes where it is not followed";
                else if (has && Outside(i) && ((MethodReference)i.Operand).DeclaringType.Namespace == "Terraria.Testing" && restore.Count == 0)
                    why = "the list is given to testing code whose putting back was not found";
            }
            method.Body.OptimizeMacros();
            if (why != null)
            {
                Console.Error.WriteLine($"rests: {method.FullName}: {why}; left out");
                return 0;
            }
        }

        // the walk: its loop over the list
        walk.Body.SimplifyMacros();
        var c = walk.Body.Instructions;
        int b = -1;
        for (int q = 0; q + 3 < c.Count; q++)
            if (c[q].OpCode == OpCodes.Ldsfld && Is(c[q].Operand, list) && c[q + 1].OpCode == OpCodes.Ldloc
                && c[q + 2].OpCode == OpCodes.Ldelem_Ref && c[q + 3].OpCode == OpCodes.Stloc)
                b = b == -1 ? q : -2;
        bool ok = b >= 3;
        object j = null;
        Instruction init = null, step = null, test = null;
        if (ok)
        {
            j = c[b + 1].Operand;
            init = c[b - 3];
            test = c[b - 1].Operand as Instruction;
            // j = 0 and a jump to the loop's test, which is the routine's end
            ok = init.OpCode == OpCodes.Ldc_I4 && (int)init.Operand == 0 && c[b - 2].OpCode == OpCodes.Stloc && c[b - 2].Operand == j
                && c[b - 1].OpCode == OpCodes.Br && test != null && test.OpCode == OpCodes.Ldloc && test.Operand == j
                && (test.Next?.OpCode == OpCodes.Ldloc || test.Next?.OpCode == OpCodes.Ldc_I4)
                && test.Next.Next?.OpCode == OpCodes.Blt && test.Next.Next.Operand == c[b] && test.Next.Next.Next?.OpCode == OpCodes.Ret;
        }
        if (ok)
        {
            // j++ above the test
            step = test.Previous?.Previous?.Previous?.Previous;
            ok = step != null && step.OpCode == OpCodes.Ldloc && step.Operand == j && step.Next.OpCode == OpCodes.Ldc_I4
                && (int)step.Next.Operand == 1 && step.Next.Next.OpCode == OpCodes.Add && step.Next.Next.Next.OpCode == OpCodes.Stloc
                && step.Next.Next.Next.Operand == j
                && c.Count(x => x.OpCode == OpCodes.Stloc && x.Operand == j) == 2
                && c.Count(x => x.OpCode == OpCodes.Stloc && x.Operand == c[b + 3].Operand) <= 2;
        }
        if (!ok)
        {
            walk.Body.OptimizeMacros();
            Console.Error.WriteLine("rests: the chest lid routine's loop is not as expected; left out");
            return 0;
        }
        var il = walk.Body.GetILProcessor();
        var one = (VariableDefinition)c[b + 3].Operand;
        var body = c[b];
        // j = Rests.Begin(the chests in use)
        init.OpCode = OpCodes.Ldsfld;
        init.Operand = used;
        il.InsertAfter(init, il.Create(OpCodes.Call, To("Begin")));
        // j++ becomes: Rests.Done(chest, j); passed: j++
        var passedTo = il.Create(OpCodes.Ldloc, (VariableDefinition)j);
        step.Operand = one;
        il.InsertAfter(step, il.Create(OpCodes.Ldloc, (VariableDefinition)j));
        il.InsertAfter(step.Next, il.Create(OpCodes.Call, To("Done")));
        il.InsertAfter(step.Next.Next, passedTo);
        // the loop's body begins: if (Rests.Rest(j)) goto passed
        body.OpCode = OpCodes.Ldloc;
        body.Operand = j;
        il.InsertAfter(body, il.Create(OpCodes.Call, To("Rest")));
        il.InsertAfter(body.Next, il.Create(OpCodes.Brtrue, passedTo));
        il.InsertAfter(body.Next.Next, il.Create(OpCodes.Ldsfld, list));
        walk.Body.OptimizeMacros();

        // the writes the walk does not do
        var dirty = To("Dirty");
        int n = 0;
        foreach (var method in methods)
        {
            var code = method.Body.Instructions;
            bool has = code.Any(x => Is(x.Operand, list));
            var at = code.Where(x => x.OpCode == OpCodes.Stfld && method != walk && lids.Any(f => Is(x.Operand, f))
                || x.OpCode == OpCodes.Stsfld && Is(x.Operand, list)
                || has && (x.OpCode == OpCodes.Stelem_Ref || Outside(x))
                || x.OpCode == OpCodes.Ret && restore.Contains(method)).ToList();
            if (at.Count == 0)
                continue;
            method.Body.SimplifyMacros();
            var edit = method.Body.GetILProcessor();
            foreach (var x in at)
            {
                if (x.OpCode == OpCodes.Ret)
                {
                    // in the return's place, for the jumps to it
                    x.OpCode = OpCodes.Call;
                    x.Operand = dirty;
                    edit.InsertAfter(x, edit.Create(OpCodes.Ret));
                }
                else
                    edit.InsertAfter(x, edit.Create(OpCodes.Call, dirty));
            }
            method.Body.OptimizeMacros();
            n += at.Count;
        }
        Console.Error.WriteLine($"rests: the chest lid routine passes chests at rest; {n} writes tell");
        return n + 1;
    }
}
