// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/Install.cs: PatchThrottle, Near, Retarget, MIT),
// except where marked (FNAPatches).
using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TerrariaPatches.TileConvert
{
// Work the game redoes every frame that a handheld cannot afford (TrPatches.Throttle has the reasons): the map's player heads
// rendered every 6th frame, the game's debug frame statistics worked out only while something reads them, and the map's
// section drawing after a world loads given 2 ms a frame instead of 5.
public static class ThrottlePass
{
    static MethodReference Import(ModuleDefinition into, ModuleDefinition tr, string type, string method) =>
        into.ImportReference(tr.GetType(type).Methods.Single(m => m.Name == method));

    public static int Apply(ModuleDefinition m) => Apply(m, m);     // (FNAPatches) the runtime is woven into Terraria.exe

    // TrPatches.Throttle: the map heads' render every few frames, the map sections' budget
    public static int Apply(ModuleDefinition m, ModuleDefinition tr)
    {
        int changes = 0;
        var prepare = m.GetType("Terraria.Graphics.Renderers.MapHeadRenderer").Methods.Single(x => x.Name == "PrepareRenderTarget");
        {
            var il = prepare.Body.GetILProcessor();
            var first = prepare.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Call, Import(m, tr, "TrPatches.Throttle", "MapHeads")));
            il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            changes++;
        }
        // TimeLogger.DataSeries.StartNextFrame: the ring of the last frames' values moves on as before;
        // the statistics over it (collect, sort, three quantiles, for each of about 120 series) are
        // worked out only when TrPatches.Throttle.Stats says something reads them
        {
            var logger = m.GetType("Terraria.TimeLogger");
            var series = logger.NestedTypes.Single(t => t.Name == "DataSeries").Methods.Single(x => x.Name == "StartNextFrame");
            var code = series.Body.Instructions;
            // "usedCount = 0": the statistics start
            var start = code.First(i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Name == "usedCount").Previous.Previous;
            // "values[next] = 0": the ring's last step, after the statistics
            var tail = code.Last(i => i.OpCode == OpCodes.Stelem_I4);
            for (int back = 0; back < 5; back++) tail = tail.Previous;
            if (start.OpCode != OpCodes.Ldarg_0 || tail.OpCode != OpCodes.Ldarg_0 || !(tail.Next.Operand is FieldReference values) || values.Name != "values")
                throw new InvalidOperationException("TimeLogger.DataSeries.StartNextFrame is not the code this patch knows");
            var il = series.Body.GetILProcessor();
            var first = il.Create(OpCodes.Ldsfld, logger.Fields.Single(f => f.Name == "currentlyLogging"));
            il.InsertBefore(start, first);
            il.InsertBefore(start, il.Create(OpCodes.Ldsfld, logger.Fields.Single(f => f.Name == "startLoggingNextFrame")));
            il.InsertBefore(start, il.Create(OpCodes.Or));
            il.InsertBefore(start, il.Create(OpCodes.Call, Import(m, tr, "TrPatches.Throttle", "Stats")));
            il.InsertBefore(start, il.Create(OpCodes.Brfalse, tail));
            Retarget(series.Body, start, first);
            changes++;
        }
        // DoDraw: "while (elapsed.TotalMilliseconds < 5.0 && sectionManager.GetNextMapDraw(...))"
        var budget = m.ImportReference(tr.GetType("TrPatches.Throttle").Fields.Single(f => f.Name == "MapBudget"));
        // (FNAPatches) DoDraw, or MonoMod's orig_DoDraw where another patch wraps it
        foreach (var doDraw in m.GetType("Terraria.Main").Methods.Where(x => (x.Name == "DoDraw" || x.Name == "orig_DoDraw") && x.HasBody))
        foreach (var ins in doDraw.Body.Instructions)
            if (ins.OpCode == OpCodes.Ldc_R8 && (double)ins.Operand == 5.0 && ins.Previous?.Operand is MethodReference r && r.Name == "get_TotalMilliseconds" &&
                ins.Next.Next?.Next?.Operand is MethodReference g && g.Name == "GetNextMapDraw" || ins.OpCode == OpCodes.Ldc_R8 && (double)ins.Operand == 5.0 &&
                ins.Previous?.Operand is MethodReference r2 && r2.Name == "get_TotalMilliseconds" && Near(ins, "GetNextMapDraw", 12))
            {
                ins.OpCode = OpCodes.Ldsfld;
                ins.Operand = budget;
                changes++;
            }
        return changes;
    }

    static bool Near(Instruction ins, string method, int count)
    {
        for (var i = ins.Next; i != null && count-- > 0; i = i.Next)
            if (i.Operand is MethodReference r && r.Name == method) return true;
        return false;
    }

    // branches and handlers that ended at <from> now end at <to>, which was inserted before it
    static void Retarget(MethodBody body, Instruction from, Instruction to)
    {
        foreach (var ins in body.Instructions)
        {
            if (ins.Operand == from && ins != to) ins.Operand = to;
            if (ins.Operand is Instruction[] targets)
                for (int i = 0; i < targets.Length; i++) if (targets[i] == from) targets[i] = to;
        }
        foreach (var h in body.ExceptionHandlers)
        {
            if (h.TryEnd == from) h.TryEnd = to;
            if (h.HandlerEnd == from) h.HandlerEnd = to;
        }
    }
}
}
