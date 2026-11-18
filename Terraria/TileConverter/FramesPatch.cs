// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/FramesPatch.cs, MIT), except where marked (FNAPatches).
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// trpatch install: the game's pass over a section of tiles that comes into view is split in two
// (TrPatches.Frames, docs/notes.md): the tiles' pictures are made ahead of the view, a slice an
// update, and what the pass changes in the world still happens when the section comes into view.
//
//   the frame routine         asks TrPatches.Frames.Enter first: while pictures are made ahead, a
//                             call for a neighbour is answered there
//   the cactus routine        asks Frames.Cactus first, likewise (it can break the cactus)
//   the section pass          tells Frames as it starts and ends, and frames each tile through
//                             Frames.Reframe, which skips a tile done ahead
//   Main.DoUpdate             Frames.Tick: the slice
//
// This is only sound for the routines it was written against, so they are compared first: which
// of the game's routines each calls, in order (the frame routine, the passes) or as a set (the
// picture routine, far too long for more). Another version of the game is left as it is.
static class FramesPatch
{
    static readonly string[] FrameCalls =
    {
        "Terraria.Map.MapUpdateQueue::Add", "Terraria.Tile::active", "Terraria.Tile::halfBrick", "Terraria.Tile::ClearBlockPaintAndCoating",
        "Terraria.Tile::slope", "Terraria.Liquid::AddWater", "Terraria.Tile::active", "Terraria.WorldGen::TileFrameImportant",
        "Terraria.WorldGen::CheckOnTable1x1", "Terraria.WorldGen::CheckCactus", "Terraria.WorldGen::CheckVines",
        "Terraria.WorldGen::SpawnFallingBlockProjectile", "Terraria.WorldGen::TileFrameCosmetic",
    };
    static readonly string[] ReframeCalls = { "Terraria.WorldGen::TileFrameCosmetic", "Terraria.WorldGen::TileFrame", "Terraria.Framing::WallFrame" };
    static readonly string[] SectionCalls =
    {
        "Terraria.WorldGen::GetSectionBounds", "Terraria.Map.MapUpdateQueue::Add", "Terraria.WorldGen::Reframe", "Terraria.WorldSections::SetSectionFramed",
    };
    // the picture routine: what it calls in WorldGen, and the other types of the game it calls into
    static readonly string[] LooksCalls =
    {
        "CactusFrame", "GetTileMergeCulling", "IsRope", "TileFrame", "TileFrameCosmetic", "TileMergeAttempt", "TileMergeAttemptFrametest",
        "TileMergeAttemptWeird", "get_genRand",
    };
    static readonly string[] LooksTypes = { "Terraria.Tile", "Terraria.Utils", "Terraria.Framing", "Terraria.Utilities.UnifiedRandom" };
    static readonly string[] CactusCalls = { "Terraria.Tile::active", "Terraria.WorldGen::CheckCactus", "Terraria.WorldGen::TileFrameCosmetic" };

    public static int Apply(ModuleDefinition terraria, ModuleDefinition tr)
    {
        var worldGen = terraria.GetType("Terraria.WorldGen");
        var frame = One(worldGen, "TileFrame", 4);
        var looks = One(worldGen, "TileFrameCosmetic", 4);
        var cactus = One(worldGen, "CactusFrame", 2);
        var reframe = One(worldGen, "Reframe", 3);
        var section = One(worldGen, "SectionTileFrame", 4);
        // (FNAPatches) Main.DoUpdate's own body: MonoMod's orig_DoUpdate where another patch wraps it
        var update = One(terraria.GetType("Terraria.Main"), "orig_DoUpdate", 1) ?? One(terraria.GetType("Terraria.Main"), "DoUpdate", 1);
        string why = frame == null || looks == null || cactus == null || reframe == null || section == null || update == null ? "a routine is missing" : null;
        why = why ?? Same("the frame routine", Calls(frame), FrameCalls);
        why = why ?? Same("the tile's part of the pass", Calls(reframe), ReframeCalls);
        why = why ?? Same("the section pass", Calls(section), SectionCalls);
        why = why ?? Same("the cactus routine", Calls(cactus).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(), CactusCalls);
        why = why ?? Looks(looks);
        if (why == null && section.Body.Instructions.Count(x => x.OpCode == OpCodes.Ret) != 1)
            why = "the section pass ends in more than one place";
        if (why != null)
        {
            Console.WriteLine($"frames: sections are framed as the game does it, when they come into view ({why}: not the game this was written for)");
            return 0;
        }

        Func<string, MethodReference> at = name => terraria.ImportReference(tr.GetType("TrPatches.Frames").Methods.Single(m => m.Name == name));
        Ask(frame, at("Enter"));
        Ask(cactus, at("Cactus"));
        {
            var il = section.Body.GetILProcessor();
            var first = section.Body.Instructions[0];
            for (int k = 0; k < 4; k++)
                il.InsertBefore(first, il.Create(OpCodes.Ldarg, section.Parameters[k]));
            il.InsertBefore(first, il.Create(OpCodes.Call, at("Begin")));
            section.Body.Instructions.Single(x => x.OpCode == OpCodes.Call && ((MethodReference)x.Operand).Name == "Reframe").Operand = at("Reframe");
            var ret = section.Body.Instructions.Single(x => x.OpCode == OpCodes.Ret);
            var end = il.Create(OpCodes.Call, at("End"));
            il.InsertBefore(ret, end);
            foreach (var x in section.Body.Instructions)
            {
                if (x.Operand == ret)
                    x.Operand = end;
                else if (x.Operand is Instruction[] targets)
                    for (int k = 0; k < targets.Length; k++)
                        if (targets[k] == ret)
                            targets[k] = end;
            }
        }
        {
            var il = update.Body.GetILProcessor();
            il.InsertBefore(update.Body.Instructions[0], il.Create(OpCodes.Call, at("Tick")));
        }
        return 4;
    }

    static MethodDefinition One(TypeDefinition type, string name, int parameters)
    {
        var all = type.Methods.Where(m => m.Name == name && m.IsStatic == (type.Name == "WorldGen") && m.Parameters.Count == parameters && m.HasBody).ToList();
        return all.Count == 1 ? all[0] : null;
    }

    // if (TrPatches.Frames.<it>(the routine's arguments)) return; before the routine's own code
    static void Ask(MethodDefinition m, MethodReference it)
    {
        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions[0];
        foreach (var p in m.Parameters)
            il.InsertBefore(first, il.Create(OpCodes.Ldarg, p));
        il.InsertBefore(first, il.Create(OpCodes.Call, it));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
    }

    // the game's routines a method calls, in order (an array's accessors and the libraries' are not)
    static List<string> Calls(MethodDefinition m) =>
        m.Body.Instructions.Where(x => x.OpCode == OpCodes.Call || x.OpCode == OpCodes.Callvirt)
            .Select(x => (MethodReference)x.Operand)
            .Where(r => !(r.DeclaringType is ArrayType) && r.DeclaringType.FullName.StartsWith("Terraria."))
            .Select(r => r.DeclaringType.FullName + "::" + r.Name).ToList();

    static string Same(string what, List<string> calls, string[] expected) =>
        calls.SequenceEqual(expected) ? null : what + " calls " + string.Join(", ", calls.Select(c => c.Substring(c.LastIndexOf(':') + 1)));

    static string Looks(MethodDefinition looks)
    {
        var inWorldGen = new HashSet<string>();
        int frames = 0;
        foreach (var x in looks.Body.Instructions)
        {
            if (x.OpCode != OpCodes.Call && x.OpCode != OpCodes.Callvirt)
                continue;
            var r = (MethodReference)x.Operand;
            string type = r.DeclaringType.FullName;
            if (r.DeclaringType is ArrayType || !type.StartsWith("Terraria.") || type.StartsWith("Terraria.WorldGen/"))
                continue;
            if (type == "Terraria.WorldGen")
            {
                inWorldGen.Add(r.Name);
                if (r.Name == "TileFrame")
                    frames++;
            }
            else if (!LooksTypes.Contains(type))
                return "the picture routine calls into " + type;
        }
        if (!inWorldGen.SetEquals(LooksCalls) || frames != 10)
            return "the picture routine calls " + string.Join(", ", inWorldGen.OrderBy(x => x, StringComparer.Ordinal)) + $" ({frames} frame calls)";
        return null;
    }
}
