// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/UiCuts.cs, MIT), except where marked (FNAPatches).
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Work of the interface's draw whose answer is known without it (TrPatches.Ui, TrPatches.Prep; the
// comments there have the reasons the game runs the same). Each patch is applied only where the
// game's code is the code it was written for, and leaves itself out otherwise.
static class UiCuts
{
    static bool Is(object operand, string type, string name) =>
        operand is MemberReference r && r.Name == name && r.DeclaringType.FullName == type;

    static IEnumerable<TypeDefinition> Nested(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Nested));

    static void Say(string s) => Console.Error.WriteLine("ui: " + s);

    // NPC.GetBossHeadTextureIndex as 1.4.5.8 has it: the table, and seven types worked out apart
    // (TrPatches.Ui.apart); summed on the game's code as shipped, before any other patch
    const ulong HeadsWritten = 0xf5f1a5d4dcfaaaaf;
    // LockOnHelper.Draw as 1.4.5.8 has it (TrPatches.Ui.LockOnIdle has its loop's rules)
    const ulong LockOnWritten = 0xe95ecac5271631c0;
    static ulong heads, lockOn;

    public static void Look(ModuleDefinition m)
    {
        var method = m.GetType("Terraria.NPC")?.Methods.FirstOrDefault(x => x.Name == "GetBossHeadTextureIndex" && x.HasBody);
        heads = method == null ? 0 : Sum(method.Body);
        method = m.GetType("Terraria.GameInput.LockOnHelper")?.Methods.FirstOrDefault(x => x.Name == "Draw" && x.HasBody);
        lockOn = method == null ? 0 : Sum(method.Body);
    }

    // a routine's code summed as the other version guards sum theirs (Guard.Sum: the same under Mono
    // and .NET)
    static ulong Sum(MethodBody body)
    {
        ulong h = 14695981039346656037;
        foreach (var i in body.Instructions)
            h = Guard.Sum(h, i);
        return h;
    }

    // Terraria.exe, before Scans (the loops are matched as the game wrote them)
    public static int Apply(ModuleDefinition m, ModuleDefinition tr)
    {
        var ui = tr.GetType("TrPatches.Ui");
        MethodReference Ui(string name) => m.ImportReference(ui.Methods.Single(x => x.Name == name));
        int n = Restores(m, Ui("Restored"));
        int restores = n;
        n += Bags(m, Ui("NextBag"));
        n += Icons(m, Ui("NextIcon"), Ui("NpcType"), Ui("NpcTown"));
        int typed = ProjTypes(m, Ui("ProjType"), restores > 0);
        if (typed > 0)
        {
            n += typed;
            n += Search(m, "Terraria.Main", "DrawInterface_Resources_GolfPower", new[] { 722 }, Ui("Made"));
            n += Search(m, "Terraria.Map.DigtoiseMapLayer", "Draw", new[] { 1098, 1123 }, Ui("Made"));
        }
        n += Pings(m, Ui("NoPings"));
        n += Seen(m, Ui("NextSeen"));
        n += LockOn(m, Ui("LockOnIdle"));
        return n;
    }

    // LockOnHelper.Draw's loop over the 200 NPCs' lock-on marks: "for (i = 0; i < _drawProgress.GetLength(0);
    // i++)" moves each NPC's two progress values toward its mark (Clamp(p +- 0.1, 0, 1)) and draws where a
    // value is above 0. With every value 0 and no NPC marked, every round writes 0 over 0 and draws
    // nothing: Ui.LockOnIdle says so before the loop, from the same fields, and the loop is not begun.
    static int LockOn(ModuleDefinition m, MethodReference idle)
    {
        var type = m.GetType("Terraria.GameInput.LockOnHelper");
        var draw = type?.Methods.SingleOrDefault(x => x.Name == "Draw" && x.HasBody);
        if (draw == null || lockOn != LockOnWritten)
        {
            Say($"LockOnHelper.Draw is not the code this patch knows (it sums to {lockOn:x16}); left as it is");
            return 0;
        }
        var body = draw.Body;
        body.SimplifyMacros();
        var c = body.Instructions;
        FieldDefinition F(string name) => type.Fields.Single(f => f.Name == name);
        // the loop's start: "flag = false; i = 0; br cond", cond: "i < _drawProgress.GetLength(0)", then ret
        var init = c.Single(x => x.OpCode == OpCodes.Br && x.Operand is Instruction cond && cond.OpCode == OpCodes.Ldloc
            && cond.Next.OpCode == OpCodes.Ldsfld && Is(cond.Next.Operand, type.FullName, "_drawProgress")).Previous.Previous;
        var flagStore = init.Previous;
        var flag = (VariableDefinition)flagStore.Operand;
        var blt = ((Instruction)init.Next.Next.Operand).Next.Next.Next.Next;
        if (flagStore.OpCode != OpCodes.Stloc || flagStore.Previous.OpCode != OpCodes.Ldc_I4 || (int)flagStore.Previous.Operand != 0
            || c.Count(x => x.OpCode == OpCodes.Stloc && x.Operand == flag) != 1 || blt.OpCode != OpCodes.Blt || blt.Next.OpCode != OpCodes.Ret)
        {
            body.OptimizeMacros();
            Say("LockOnHelper.Draw's loop is not the one this patch knows; left as it is");
            return 0;
        }
        var il = body.GetILProcessor();
        var guard = new[]
        {
            il.Create(OpCodes.Ldsfld, F("_drawProgress")), il.Create(OpCodes.Ldsfld, F("_pickedTarget")), il.Create(OpCodes.Ldsfld, F("_targets")),
            il.Create(OpCodes.Ldsfld, F("UseMode")), il.Create(OpCodes.Ldsfld, F("_threeDSTarget")), il.Create(OpCodes.Ldsfld, F("_targetClosestTarget")),
            il.Create(OpCodes.Ldloc, flag), il.Create(OpCodes.Call, idle), il.Create(OpCodes.Brtrue, blt.Next),
        };
        foreach (var x in guard)
            il.InsertBefore(init, x);
        Retarget(body, init, guard[0]);
        body.OptimizeMacros();
        Say("the lock-on marks' loop is not begun while every mark is 0 and no NPC is marked");
        return 1;
    }

    // Main.DrawMap's loops "for (j = 0; j < 255; j++) { if (!DrawPlayerMapIcon_CanBeSeen(j)) continue; ... }":
    // for a player other than the local one that Scan has marked inactive, the question is false and
    // reads only. Ui.NextSeen at the loop's head.
    static int Seen(ModuleDefinition m, MethodReference next)
    {
        var map = m.GetType("Terraria.Main")?.Methods.SingleOrDefault(x => x.Name == "DrawMap" && x.HasBody);
        var seen = m.GetType("Terraria.Main")?.Methods.SingleOrDefault(x => x.Name == "DrawPlayerMapIcon_CanBeSeen");
        // the question as 1.4.5.8 asks it: the local player is seen; another player inactive is not
        bool ok = map != null && seen != null && seen.Parameters.Count == 1;
        if (ok)
        {
            var q = seen.Body.Instructions;
            ok = q.Count > 20 && q[0].OpCode == OpCodes.Ldarg_0 && Is(q[1].Operand, "Terraria.Main", "myPlayer") && q[3].OpCode == OpCodes.Ldc_I4_1 && q[4].OpCode == OpCodes.Ret
                && Is(q[5].Operand, "Terraria.Main", "player") && Is(q[6].Operand, "Terraria.Main", "myPlayer") && q[7].OpCode == OpCodes.Ldelem_Ref && q[8].OpCode == OpCodes.Stloc_0
                && Is(q[9].Operand, "Terraria.Main", "player") && q[10].OpCode == OpCodes.Ldarg_0 && q[11].OpCode == OpCodes.Ldelem_Ref && q[12].OpCode == OpCodes.Stloc_1
                && q[13].OpCode == OpCodes.Ldloc_1 && Is(q[14].Operand, "Terraria.Player", "active") && (q[15].OpCode == OpCodes.Brfalse_S || q[15].OpCode == OpCodes.Brfalse)
                && q[15].Operand is Instruction no && no.OpCode == OpCodes.Ldc_I4_0 && no.Next.OpCode == OpCodes.Ret;
        }
        if (!ok)
        {
            Say("Main.DrawPlayerMapIcon_CanBeSeen is not the code this patch knows; the map's player loops left as they are");
            return 0;
        }
        var body = map.Body;
        body.SimplifyMacros();
        var il = body.GetILProcessor();
        int n = 0;
        foreach (var init in body.Instructions.Where(x => x.OpCode == OpCodes.Ldc_I4 && (int)x.Operand == 0).ToList())
        {
            var (step, exit, j) = Loop(body, init, 255);
            if (step == null)
                continue;
            var head = init.Next.Next.Next;
            if (!(head.OpCode == OpCodes.Ldloc && head.Operand == j && head.Next.OpCode == OpCodes.Call && Is(head.Next.Operand, "Terraria.Main", "DrawPlayerMapIcon_CanBeSeen")
                && head.Next.Next.OpCode == OpCodes.Brfalse && head.Next.Next.Operand == step))
                continue;
            if (body.ExceptionHandlers.Any(h => new[] { h.TryStart, h.TryEnd, h.HandlerStart, h.HandlerEnd }.Any(x => x != null
                && x.Offset > init.Offset && x.Offset <= exit.Offset)))
                continue;
            var t = new VariableDefinition(m.TypeSystem.Int32);
            body.Variables.Add(t);
            var first = il.Create(OpCodes.Ldloc, j);
            var go = il.Create(OpCodes.Ldloc, t);
            var code = new[]
            {
                first, il.Create(OpCodes.Ldc_I4, 255), il.Create(OpCodes.Call, next), il.Create(OpCodes.Stloc, t),
                il.Create(OpCodes.Ldloc, t), il.Create(OpCodes.Ldc_I4, 0), il.Create(OpCodes.Bge, go),
                il.Create(OpCodes.Ldc_I4, 255), il.Create(OpCodes.Stloc, j), il.Create(OpCodes.Br, exit),
                go, il.Create(OpCodes.Stloc, j),
            };
            foreach (var x in code)
                il.InsertBefore(head, x);
            Retarget(body, head, first);
            n++;
        }
        body.OptimizeMacros();
        Say($"the map's player loops pass the inactive players ({n} loops)");
        return n;
    }

    // a for loop "for (i = 0; i < bound; i++)" whose head is at h: (the loop's step, its exit), or nulls;
    // the bound a number or (boundField) a static's value
    static (Instruction step, Instruction exit, VariableDefinition i) Loop(MethodBody body, Instruction init, int bound, string boundField = null)
    {
        // ldc.i4 0; stloc i; br cond ... step: ldloc i; ldc.i4 1; add; stloc i; cond: ldloc i; ldc.i4 bound; blt head
        if (init.OpCode != OpCodes.Ldc_I4 || (int)init.Operand != 0 || init.Next?.OpCode != OpCodes.Stloc
            || init.Next.Next?.OpCode != OpCodes.Br)
            return (null, null, null);
        var i = (VariableDefinition)init.Next.Operand;
        var cond = (Instruction)init.Next.Next.Operand;
        var head = init.Next.Next.Next;
        var step = cond.Previous?.Previous?.Previous?.Previous;
        bool ok = cond.OpCode == OpCodes.Ldloc && cond.Operand == i
            && (boundField == null ? cond.Next.OpCode == OpCodes.Ldc_I4 && (int)cond.Next.Operand == bound
                : cond.Next.OpCode == OpCodes.Ldsfld && Is(cond.Next.Operand, "Terraria.Main", boundField))
            && cond.Next.Next.OpCode == OpCodes.Blt && cond.Next.Next.Operand == head
            && step != null && step.OpCode == OpCodes.Ldloc && step.Operand == i && step.Next.OpCode == OpCodes.Ldc_I4 && (int)step.Next.Operand == 1
            && step.Next.Next.OpCode == OpCodes.Add && step.Next.Next.Next.OpCode == OpCodes.Stloc && step.Next.Next.Next.Operand == i;
        return ok ? (step, cond.Next.Next.Next, i) : (null, null, null);
    }

    static void Retarget(MethodBody body, Instruction from, Instruction to)
    {
        foreach (var x in body.Instructions)
        {
            if (x.Operand == from && x != to) x.Operand = to;
            if (x.Operand is Instruction[] targets)
                for (int k = 0; k < targets.Length; k++) if (targets[k] == from) targets[k] = to;
        }
        foreach (var h in body.ExceptionHandlers)
        {
            if (h.TryStart == from) h.TryStart = to;
            if (h.TryEnd == from) h.TryEnd = to;
            if (h.HandlerStart == from) h.HandlerStart = to;
            if (h.HandlerEnd == from) h.HandlerEnd = to;
            if (h.FilterStart == from) h.FilterStart = to;
        }
    }

    // BossBagMapLayer.Draw: for (i = 0; i < 400; i++) { w = Main.item[i]; if (w != null && w.active &&
    // ItemID.Sets.BossBag[w.type]) { ... } }, then the method's end. Ui.NextBag at the loop's head.
    static int Bags(ModuleDefinition m, MethodReference next)
    {
        var draw = m.GetType("Terraria.Map.BossBagMapLayer")?.Methods.SingleOrDefault(x => x.Name == "Draw");
        if (draw == null)
        {
            Say("BossBagMapLayer.Draw not found; left as it is");
            return 0;
        }
        var body = draw.Body;
        body.SimplifyMacros();
        var c = body.Instructions;
        var init = c.FirstOrDefault(x => x.OpCode == OpCodes.Ldc_I4 && (int)x.Operand == 0);
        var (step, exit, i) = init == null ? (null, null, null) : Loop(body, init, 400);
        var head = init?.Next.Next.Next;
        // the head as written: the item, its null test, active, the bag table, each "no" to the step
        bool ok = step != null && !body.HasExceptionHandlers && exit.OpCode == OpCodes.Ret && exit.Next == null
            && c.Count(x => x.OpCode == OpCodes.Br && x.Operand == (Instruction)init.Next.Next.Operand) == 1;
        VariableDefinition w = null;
        if (ok)
        {
            var h = head;
            Instruction At(int k) { var x = h; for (int j = 0; j < k && x != null; j++) x = x.Next; return x; }
            ok = Is(At(0).Operand, "Terraria.Main", "item") && At(0).OpCode == OpCodes.Ldsfld && At(1).OpCode == OpCodes.Ldloc && At(1).Operand == i
                && At(2).OpCode == OpCodes.Ldelem_Ref && At(3).OpCode == OpCodes.Stloc
                && At(4).OpCode == OpCodes.Ldloc && At(4).Operand == At(3).Operand && At(5).OpCode == OpCodes.Brfalse && At(5).Operand == step
                && At(6).OpCode == OpCodes.Ldloc && At(6).Operand == At(3).Operand && At(7).OpCode == OpCodes.Callvirt && Is(At(7).Operand, "Terraria.WorldItem", "get_active")
                && At(8).OpCode == OpCodes.Brfalse && At(8).Operand == step
                && At(9).OpCode == OpCodes.Ldsfld && Is(At(9).Operand, "Terraria.ID.ItemID/Sets", "BossBag")
                && At(10).OpCode == OpCodes.Ldloc && At(10).Operand == At(3).Operand && At(11).OpCode == OpCodes.Callvirt && Is(At(11).Operand, "Terraria.WorldItem", "get_type")
                && At(12).OpCode == OpCodes.Ldelem_U1 && At(13).OpCode == OpCodes.Brfalse && At(13).Operand == step;
            if (ok)
                w = (VariableDefinition)At(3).Operand;
        }
        // the table is the game's constant set: stored once by its class's initializer, and everywhere
        // else loaded only to read an element or its length
        if (ok)
            foreach (var method in m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody))
            {
                var all = method.Body.Instructions.ToArray();
                for (int j = 0; j < all.Length; j++)
                {
                    if (!Is(all[j].Operand, "Terraria.ID.ItemID/Sets", "BossBag"))
                        continue;
                    bool set = all[j].OpCode == OpCodes.Stsfld && method.IsConstructor && method.IsStatic
                        && method.DeclaringType.FullName == "Terraria.ID.ItemID/Sets";
                    int q = all[j].OpCode == OpCodes.Ldsfld ? Consumer(all, j) : -1;
                    if (set || q >= 0 && (all[q].OpCode == OpCodes.Ldelem_U1 || all[q].OpCode == OpCodes.Ldlen))
                        continue;
                    Say($"ItemID.Sets.BossBag is used in {method.FullName} other than read; the bag layer left as it is");
                    ok = false;
                }
            }
        if (!ok)
        {
            body.OptimizeMacros();
            Say("BossBagMapLayer.Draw is not the code this patch knows; left as it is");
            return 0;
        }
        var il = body.GetILProcessor();
        var t = new VariableDefinition(m.TypeSystem.Int32);
        body.Variables.Add(t);
        var first = il.Create(OpCodes.Ldloc, i);
        var go = il.Create(OpCodes.Ldloc, t);
        var code = new[]
        {
            first, il.Create(OpCodes.Ldc_I4, 400), il.Create(OpCodes.Call, next), il.Create(OpCodes.Stloc, t),
            il.Create(OpCodes.Ldloc, t), il.Create(OpCodes.Ldc_I4, 0), il.Create(OpCodes.Bge, go),
            // none left: the index and the item's local as the last round would leave them
            il.Create(OpCodes.Ldc_I4, 400), il.Create(OpCodes.Stloc, i),
            il.Create(OpCodes.Ldsfld, (FieldReference)head.Operand), il.Create(OpCodes.Ldc_I4, 399), il.Create(OpCodes.Ldelem_Ref), il.Create(OpCodes.Stloc, w),
            il.Create(OpCodes.Br, exit),
            go, il.Create(OpCodes.Stloc, i),
        };
        foreach (var x in code)
            il.InsertBefore(head, x);
        Retarget(body, head, first);
        body.OptimizeMacros();
        Say("the minimap's boss bag layer passes the marked world items");
        return 1;
    }

    // the instruction that takes the value instruction j puts on the stack (within its block), or -1
    static int Consumer(Instruction[] all, int j)
    {
        int depth = 1;
        for (int q = j + 1; q < all.Length; q++)
        {
            int pops = Rests.Pops(all[q]);
            if (pops >= depth)
                return q;
            if (all[q].OpCode.FlowControl != FlowControl.Next && all[q].OpCode.FlowControl != FlowControl.Call)
                return -1;
            depth += Rests.Pushes(all[q]) - pops;
        }
        return -1;
    }

    // The three map icon loops of Main (DrawNPCMapIcons, 2, 3): "for (i = 0; i < maxNPCs; i++) { if
    // (npc[i].active && npc[i].townNPC && ...) { ... } if (!npc[i].active || npc[i].GetBossHeadTextureIndex()
    // == -1) continue; ... }". A round for an NPC that is inactive, or neither a town NPC nor of a type
    // with a head icon, does nothing. Ui.NextIcon at the loop's head; every write of NPC.type and
    // NPC.townNPC through Ui.NpcType and Ui.NpcTown.
    static int Icons(ModuleDefinition m, MethodReference next, MethodReference setType, MethodReference setTown)
    {
        if (heads != HeadsWritten)
        {
            Say($"NPC.GetBossHeadTextureIndex is not the code this patch knows (it sums to {heads:x16}); the map icons left as they are");
            return 0;
        }
        var all = m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody).ToList();
        foreach (var method in all)
            foreach (var x in method.Body.Instructions)
                if (x.OpCode == OpCodes.Ldflda && (Is(x.Operand, "Terraria.NPC", "type") || Is(x.Operand, "Terraria.NPC", "townNPC")) && !ByAddressRead(x))
                {
                    Say($"an NPC's type or town flag by address in {method.FullName}; the map icons left as they are");
                    return 0;
                }
        // the head table is the game's constant set
        foreach (var method in all)
        {
            var code = method.Body.Instructions.ToArray();
            for (int j = 0; j < code.Length; j++)
            {
                if (!Is(code[j].Operand, "Terraria.ID.NPCID/Sets", "BossHeadTextures"))
                    continue;
                bool set = code[j].OpCode == OpCodes.Stsfld && method.IsConstructor && method.IsStatic && method.DeclaringType.FullName == "Terraria.ID.NPCID/Sets";
                int q = code[j].OpCode == OpCodes.Ldsfld ? Consumer(code, j) : -1;
                if (set || q >= 0 && (code[q].OpCode == OpCodes.Ldelem_I4 || code[q].OpCode == OpCodes.Ldlen))
                    continue;
                Say($"NPCID.Sets.BossHeadTextures is used in {method.FullName} other than read; the map icons left as they are");
                return 0;
            }
        }
        // the loops
        var main = m.GetType("Terraria.Main");
        var loops = new List<(MethodBody, Instruction, Instruction, Instruction, VariableDefinition)>();
        foreach (string name in new[] { "DrawNPCMapIcons", "DrawNPCMapIcons2", "DrawNPCMapIcons3" })
        {
            var method = main.Methods.SingleOrDefault(x => x.Name == name && x.HasBody);
            if (method == null)
            {
                Say($"Main.{name} not found; the map icons left as they are");
                return 0;
            }
            var body = method.Body;
            body.SimplifyMacros();
            var inits = body.Instructions.Where(x => x.OpCode == OpCodes.Ldc_I4 && (int)x.Operand == 0 && Loop(body, x, 0, "maxNPCs").step != null).ToList();
            bool ok = inits.Count == 1 && !body.HasExceptionHandlers;
            if (ok)
            {
                var (step, exit, i) = Loop(body, inits[0], 0, "maxNPCs");
                var head = inits[0].Next.Next.Next;
                bool Npc(Instruction x) => x.OpCode == OpCodes.Ldsfld && Is(x.Operand, "Terraria.Main", "npc") && x.Next.OpCode == OpCodes.Ldloc && x.Next.Operand == i
                    && x.Next.Next.OpCode == OpCodes.Ldelem_Ref;
                Instruction Skip(Instruction x, int k) { for (; k > 0 && x != null; k--) x = x.Next; return x; }
                // if (npc[i].active && npc[i].townNPC ...) to L2
                var a = head;
                ok = Npc(a) && Skip(a, 3).OpCode == OpCodes.Ldfld && Is(Skip(a, 3).Operand, "Terraria.NPC", "active") && Skip(a, 4).OpCode == OpCodes.Brfalse;
                var l2 = ok ? (Instruction)Skip(a, 4).Operand : null;
                var b = Skip(a, 5);
                ok = ok && Npc(b) && Skip(b, 3).OpCode == OpCodes.Ldfld && Is(Skip(b, 3).Operand, "Terraria.NPC", "townNPC")
                    && Skip(b, 4).OpCode == OpCodes.Brfalse && Skip(b, 4).Operand == l2;
                // L2: if (!npc[i].active || npc[i].GetBossHeadTextureIndex() == -1) continue;
                ok = ok && Npc(l2) && Skip(l2, 3).OpCode == OpCodes.Ldfld && Is(Skip(l2, 3).Operand, "Terraria.NPC", "active")
                    && Skip(l2, 4).OpCode == OpCodes.Brfalse && Skip(l2, 4).Operand == step
                    && Npc(Skip(l2, 5)) && Skip(l2, 8).OpCode == OpCodes.Callvirt && Is(Skip(l2, 8).Operand, "Terraria.NPC", "GetBossHeadTextureIndex")
                    && Skip(l2, 9).OpCode == OpCodes.Ldc_I4 && (int)Skip(l2, 9).Operand == -1 && Skip(l2, 10).OpCode == OpCodes.Beq && Skip(l2, 10).Operand == step;
                if (ok)
                    loops.Add((body, inits[0], head, exit, i));
            }
            if (!ok)
            {
                foreach (var (bd, _, _, _, _) in loops) bd.OptimizeMacros();
                body.OptimizeMacros();
                Say($"Main.{name} is not the code this patch knows; the map icons left as they are");
                return 0;
            }
        }
        int n = 0;
        foreach (var method in all)
            foreach (var x in method.Body.Instructions)
                if (x.OpCode == OpCodes.Stfld && (Is(x.Operand, "Terraria.NPC", "type") || Is(x.Operand, "Terraria.NPC", "townNPC")))
                {
                    x.Operand = ((FieldReference)x.Operand).Name == "type" ? setType : setTown;
                    x.OpCode = OpCodes.Call;
                    n++;
                }
        foreach (var (body, init, head, exit, i) in loops)
        {
            var il = body.GetILProcessor();
            var t = new VariableDefinition(m.TypeSystem.Int32);
            body.Variables.Add(t);
            var first = il.Create(OpCodes.Ldloc, i);
            var go = il.Create(OpCodes.Ldloc, t);
            var bound = Is(head.Operand, "Terraria.Main", "npc") ? main.Fields.Single(f => f.Name == "maxNPCs") : null;
            var code = new[]
            {
                first, il.Create(OpCodes.Ldsfld, bound), il.Create(OpCodes.Call, next), il.Create(OpCodes.Stloc, t),
                il.Create(OpCodes.Ldloc, t), il.Create(OpCodes.Ldc_I4, 0), il.Create(OpCodes.Bge, go),
                il.Create(OpCodes.Ldsfld, bound), il.Create(OpCodes.Stloc, i), il.Create(OpCodes.Br, exit),
                go, il.Create(OpCodes.Stloc, i),
            };
            foreach (var x in code)
                il.InsertBefore(head, x);
            Retarget(body, head, first);
            body.OptimizeMacros();
            n++;
        }
        Say($"the map's creature icons pass the marked NPCs ({loops.Count} loops; {n - loops.Count} writes of an NPC's type or town flag through Ui)");
        return n;
    }

    // the testing code's putting back of a kept state (StateSnapshot.Restore) copies objects of the game
    // past every hook: after it, Ui drops its marks and takes every projectile type as made
    static int Restores(ModuleDefinition m, MethodReference restored)
    {
        var restore = m.GetType("Terraria.Testing.StateSnapshot")?.Methods.Where(x => x.Name == "Restore" && x.HasBody
            && x.ReturnType.MetadataType == MetadataType.Void).ToList() ?? new List<MethodDefinition>();
        foreach (var method in restore)
        {
            method.Body.SimplifyMacros();
            var il = method.Body.GetILProcessor();
            foreach (var ret in method.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToList())
            {
                var call = il.Create(OpCodes.Call, restored);
                il.InsertBefore(ret, call);
                Retarget(method.Body, ret, call);
            }
            method.Body.OptimizeMacros();
        }
        return restore.Count;
    }

    // every "projectile.type = value" through Ui.ProjType; 0 (and no change) where a projectile's type is
    // written through its address, or where an object could enter Main.projectile other than new (the
    // list is filled with new projectiles, whose constructor leaves the type 0, and only replaced by a
    // new array): Ui notes the types of the list's objects only (the game's samples of every type,
    // ContentSamples, are not in it)
    static int ProjTypes(ModuleDefinition m, MethodReference hook, bool restoreHooked)
    {
        var all = m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody).ToList();
        foreach (var method in all)
            foreach (var x in method.Body.Instructions)
                if (x.OpCode == OpCodes.Ldflda && Is(x.Operand, "Terraria.Projectile", "type") && !ByAddressRead(x))
                {
                    Say($"a projectile's type by address in {method.FullName}; the projectile searches left as they are");
                    return 0;
                }
        var ctor = m.GetType("Terraria.Projectile").Methods.Where(x => x.IsConstructor && !x.IsStatic).ToList();
        if (ctor.Any(c => c.Body.Instructions.Any(x => Is(x.Operand, "Terraria.Projectile", "type"))))
        {
            Say("Projectile's constructor sets its type; the projectile searches left as they are");
            return 0;
        }
        int bad = 0;
        foreach (var method in all)
        {
            var code = method.Body.Instructions.ToArray();
            for (int j = 0; j < code.Length; j++)
            {
                if (!Is(code[j].Operand, "Terraria.Main", "projectile"))
                    continue;
                bool fine;
                if (code[j].OpCode == OpCodes.Stsfld)
                    fine = j > 0 && code[j - 1].OpCode == OpCodes.Newarr;
                else if (code[j].OpCode == OpCodes.Ldsfld)
                {
                    int q = Consumer(code, j);
                    // the value a store takes: a new projectile ("new Projectile()", maybe kept in a local as well)
                    int v = q - 1;
                    if (v >= 2 && code[v].OpCode.Code >= Code.Stloc_0 && code[v].OpCode.Code <= Code.Stloc_3 || code[v].OpCode == OpCodes.Stloc_S || code[v].OpCode == OpCodes.Stloc)
                        v = code[v - 1].OpCode == OpCodes.Dup ? v - 2 : -1;
                    // or a look at its length (Utils.IndexInRange), or dropped, or kept in a local that is only
                    // read from, or handed to the testing code (whose putting back is hooked)
                    fine = q >= 0 && (Reads(code[q]) || code[q].OpCode == OpCodes.Pop
                        || code[q].OpCode == OpCodes.Stelem_Ref && v >= 0 && code[v].OpCode == OpCodes.Newobj
                            && ((MethodReference)code[v].Operand).DeclaringType.FullName == "Terraria.Projectile"
                        || restoreHooked && (code[q].OpCode == OpCodes.Call || code[q].OpCode == OpCodes.Callvirt)
                            && ((MethodReference)code[q].Operand).DeclaringType.Namespace == "Terraria.Testing"
                        || IsStore(code[q]) && ReadOnlyLocal(method.Body, code, q));
                }
                else
                    fine = false;
                if (!fine)
                {
                    Say($"Main.projectile used in {method.FullName} at {j} other than read or filled with new projectiles");
                    bad++;
                }
            }
        }
        if (bad > 0)
        {
            Say("the projectile searches left as they are");
            return 0;
        }
        int n = 0;
        foreach (var method in all)
            foreach (var x in method.Body.Instructions)
                if (x.OpCode == OpCodes.Stfld && Is(x.Operand, "Terraria.Projectile", "type"))
                {
                    x.OpCode = OpCodes.Call;
                    x.Operand = hook;
                    n++;
                }
        Say($"{n} writes of a projectile's type through Ui.ProjType");
        return n;
    }

    // an instruction that only reads the array it takes: an element, the length, or Utils.IndexInRange
    static bool Reads(Instruction x) => x.OpCode == OpCodes.Ldelem_Ref || x.OpCode == OpCodes.Ldlen
        || x.OpCode == OpCodes.Call && x.Operand is GenericInstanceMethod g && g.Name == "IndexInRange"
            && g.DeclaringType.FullName == "Terraria.Utils" && g.Parameters.Count == 2;

    // the local that instruction q stores the list into is stored only there, and its every load only read
    static bool ReadOnlyLocal(MethodBody body, Instruction[] code, int q)
    {
        var local = Local(body, code[q]);
        if (local == null)
            return false;
        for (int j = 0; j < code.Length; j++)
        {
            if (j != q && IsStore(code[j]) && Local(body, code[j]) == local)
                return false;
            if ((code[j].OpCode == OpCodes.Ldloca || code[j].OpCode == OpCodes.Ldloca_S) && code[j].Operand == local)
                return false;
            if (IsLoad(code[j]) && Local(body, code[j]) == local)
            {
                int c = Consumer(code, j);
                if (c < 0 || !Reads(code[c]))
                    return false;
            }
        }
        return true;
    }

    static bool IsStore(Instruction x) => x.OpCode.Code >= Code.Stloc_0 && x.OpCode.Code <= Code.Stloc_3 || x.OpCode == OpCodes.Stloc_S || x.OpCode == OpCodes.Stloc;
    static bool IsLoad(Instruction x) => x.OpCode.Code >= Code.Ldloc_0 && x.OpCode.Code <= Code.Ldloc_3 || x.OpCode == OpCodes.Ldloc_S || x.OpCode == OpCodes.Ldloc;

    static VariableDefinition Local(MethodBody body, Instruction x)
    {
        switch (x.OpCode.Code)
        {
            case Code.Stloc_0: case Code.Ldloc_0: return body.Variables[0];
            case Code.Stloc_1: case Code.Ldloc_1: return body.Variables[1];
            case Code.Stloc_2: case Code.Ldloc_2: return body.Variables[2];
            case Code.Stloc_3: case Code.Ldloc_3: return body.Variables[3];
            default: return x.Operand as VariableDefinition;
        }
    }

    // the address of a field handed to an int method that reads it (ToString, CompareTo, Equals, GetHashCode)
    static bool ByAddressRead(Instruction x)
    {
        var y = x.Next;
        if (y != null && y.OpCode == OpCodes.Constrained) y = y.Next;
        return y != null && (y.OpCode == OpCodes.Call || y.OpCode == OpCodes.Callvirt) && y.Operand is MethodReference r && r.HasThis
            && (r.DeclaringType.FullName == "System.Int32" || r.DeclaringType.FullName == "System.Object")
            && (r.Name == "ToString" || r.Name == "CompareTo" || r.Name == "Equals" || r.Name == "GetHashCode");
    }

    // a loop over the 1000 projectiles that does something only for one of the given types:
    // "for (i = 0; i < 1000; i++) { p = Main.projectile[i]; if (p.active && p.owner == <x> && <type test>) ... }"
    // where every way into the loop's body passes a test of the type against those numbers. Not begun
    // while no projectile of those types was ever made.
    static int Search(ModuleDefinition m, string typeName, string methodName, int[] types, MethodReference made)
    {
        var method = m.GetType(typeName)?.Methods.SingleOrDefault(x => x.Name == methodName);
        if (method == null)
        {
            Say($"{typeName}.{methodName} not found; left as it is");
            return 0;
        }
        var body = method.Body;
        body.SimplifyMacros();
        var c = body.Instructions;
        var inits = c.Where(x => x.OpCode == OpCodes.Ldc_I4 && (int)x.Operand == 0 && Loop(body, x, 1000).step != null).ToList();
        bool ok = inits.Count == 1 && !body.HasExceptionHandlers;
        string why = inits.Count == 1 ? "" : $"{inits.Count} loops over 1000";
        if (ok)
        {
            var init = inits[0];
            var (step, exit, i) = Loop(body, init, 1000);
            var head = init.Next.Next.Next;
            // the head: p = Main.projectile[i]; then tests whose every "no" goes to the step, and whose
            // "yes" for the type goes into the body; reads only
            var x = head;
            ok = x.OpCode == OpCodes.Ldsfld && Is(x.Operand, "Terraria.Main", "projectile") && x.Next.OpCode == OpCodes.Ldloc && x.Next.Operand == i
                && x.Next.Next.OpCode == OpCodes.Ldelem_Ref && x.Next.Next.Next.OpCode == OpCodes.Stloc;
            var p = ok ? (VariableDefinition)x.Next.Next.Next.Operand : null;
            Instruction body0 = null;
            var seen = new HashSet<int>();
            bool typeTest = false;
            if (ok)
            {
                // walk the tests: each a "ldloc p; ldfld <field>; <operand>; <compare-branch>" or a
                // "ldloc p; ldfld active; brfalse step"
                for (x = x.Next.Next.Next.Next; ok; )
                {
                    if (x.OpCode == OpCodes.Ldloc && x.Operand == p && x.Next.OpCode == OpCodes.Ldfld && Is(x.Next.Operand, "Terraria.Projectile", "active")
                        && x.Next.Next.OpCode == OpCodes.Brfalse && x.Next.Next.Operand == step)
                    {
                        x = x.Next.Next.Next;
                        continue;
                    }
                    if (x.OpCode == OpCodes.Ldloc && x.Operand == p && x.Next.OpCode == OpCodes.Ldfld && Is(x.Next.Operand, "Terraria.Projectile", "owner"))
                    {
                        // the owner against a static or a local's field: loads only, then bne.un step
                        var y = x.Next.Next;
                        int loads = 0;
                        while (y.OpCode == OpCodes.Ldsfld || y.OpCode == OpCodes.Ldloc || y.OpCode == OpCodes.Ldfld) { y = y.Next; loads++; }
                        ok = loads >= 1 && loads <= 2 && y.OpCode == OpCodes.Bne_Un && y.Operand == step;
                        x = y.Next;
                        continue;
                    }
                    if (x.OpCode == OpCodes.Ldloc && x.Operand == p && x.Next.OpCode == OpCodes.Ldfld && Is(x.Next.Operand, "Terraria.Projectile", "type")
                        && x.Next.Next.OpCode == OpCodes.Ldc_I4)
                    {
                        int v = (int)x.Next.Next.Operand;
                        var b = x.Next.Next.Next;
                        seen.Add(v);
                        typeTest = true;
                        if (b.OpCode == OpCodes.Bne_Un && b.Operand == step)
                        {
                            ok = body0 == null || body0 == b.Next;
                            body0 = b.Next;         // the last test: the body follows
                            break;
                        }
                        if (b.OpCode == OpCodes.Beq && b.Operand is Instruction into)
                        {
                            body0 = body0 ?? into;
                            ok = body0 == into;
                            x = b.Next;
                            continue;
                        }
                        ok = false;
                        break;
                    }
                    ok = false;
                }
            }
            ok = ok && typeTest && seen.SetEquals(types) && body0 != null;
            if (ok)
            {
                var il = body.GetILProcessor();
                var guard = new List<Instruction>();
                for (int k = 0; k < types.Length; k++)
                {
                    guard.Add(il.Create(OpCodes.Ldc_I4, types[k]));
                    guard.Add(il.Create(OpCodes.Call, made));
                    if (k > 0) guard.Add(il.Create(OpCodes.Or));
                }
                guard.Add(il.Create(OpCodes.Brfalse, exit));
                foreach (var g in guard)
                    il.InsertBefore(init, g);
                Retarget(body, init, guard[0]);
            }
            else
                why = "the loop's head is not the known one";
        }
        body.OptimizeMacros();
        if (!ok)
        {
            Say($"{typeName}.{methodName}: {why}; left as it is");
            return 0;
        }
        Say($"{typeName}.{methodName}: the search for projectile {string.Join(" or ", types)} not begun while none was made");
        return 1;
    }

    // PingMapLayer.Draw: with no pings in the list it draws nothing and removes nothing (DateTime.Now, a
    // frame and an enumerator that finds no slot in use): it returns at once.
    static int Pings(ModuleDefinition m, MethodReference empty)
    {
        var type = m.GetType("Terraria.Map.PingMapLayer");
        var draw = type?.Methods.SingleOrDefault(x => x.Name == "Draw");
        var add = type?.Methods.SingleOrDefault(x => x.Name == "Add");
        var pings = type?.Fields.SingleOrDefault(f => f.Name == "_pings");
        var count = add?.Body.Instructions.FirstOrDefault(x => (x.OpCode == OpCodes.Callvirt || x.OpCode == OpCodes.Call)
            && x.Operand is MethodReference r && r.Name == "get_Count")?.Operand as MethodReference;
        // the list is the class's own; only Draw removes from it, one slot per slot it finds in use, and
        // Clear empties it (Count 0, no slot in use)
        bool Clears(MethodDefinition x) => x.Body.Instructions.Where(i => Is(i.Operand, type.FullName, "_pings"))
            .All(i => i.Next.OpCode == OpCodes.Callvirt && i.Next.Operand is MethodReference r && r.Name == "Clear");
        bool ok = draw != null && pings != null && count != null && draw.ReturnType.MetadataType == MetadataType.Void
            && pings.FieldType.Name == "SlotVector`1" && pings.IsInitOnly
            && type.Methods.Where(x => x.HasBody).All(x => x == draw || x == add || x.IsConstructor || Clears(x))
            && draw.Body.Instructions.Count(i => i.Operand is MethodReference r && r.Name == "Remove") == 1;
        if (!ok)
        {
            Say("PingMapLayer is not the code this patch knows; left as it is");
            return 0;
        }
        var il = draw.Body.GetILProcessor();
        var start = draw.Body.Instructions[0];
        il.InsertBefore(start, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(start, il.Create(OpCodes.Ldfld, pings));
        il.InsertBefore(start, il.Create(OpCodes.Callvirt, count));
        il.InsertBefore(start, il.Create(OpCodes.Call, empty));
        il.InsertBefore(start, il.Create(OpCodes.Brfalse, start));
        il.InsertBefore(start, il.Create(OpCodes.Ret));
        Say("the map's ping layer returns at once with no pings");
        return 1;
    }
}
