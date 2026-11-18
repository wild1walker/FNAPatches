// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/TileCachePatch.cs, MIT), except where marked (FNAPatches).
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// The tile cache (runtime/TileCache.cs): what the game's routine for one tile puts in the tile
// batch is kept, and put there again while nothing the routine reads has changed.
//
// - TileDrawing.Draw calls TrPatches.TileCache.Draw where it called DrawSingleTile,
//   TileCache.Pass as it starts and TileCache.Area with the tiles it is about to walk;
// - its walk offers each tile to TileCache.Early before doing anything for it, and steps on
//   when the cache took it; AddSpecialPoint and EmitLiquidDrops, which the walk can call for a
//   tile before DrawSingleTile, set TileCache.Noted; its step to the next column asks
//   TileCache.Step (runtime/TileCacheSplit.cs);
// - while TileCache.Recording, TileBatch.SetLayerStack and GetNextSpriteIndex tell the cache what
//   they are asked;
// - in DrawSingleTile, the calls for a dark tile, for a tile's particles and for its light go
//   through the cache, which notes them and passes them on.
//
// The cache's rules are written for the routines of Terraria 1.4.5.8: Look takes a sum of their
// code as shipped, and another game is left as it is.
static class TileCachePatch
{
    const ulong Written = 0xce126df8f66ee79b;
    static bool known;

    static readonly string[][] Read =
    {
        new[] { "Terraria.GameContent.Drawing.TileDrawing", "Draw", "DrawSingleTile", "DrawBasicTile", "GetTileDrawData", "GetTileDrawTexture",
            "GetFinalLight", "DrawTiles_GetLightOverride", "DrawTiles_EmitParticles", "IsVisible", "CacheSpecialDraws_Part1", "CacheSpecialDraws_Part2",
            "DrawTile_LiquidBehindTile", "ShouldSwayInWind", "AddSpecialPoint", "EmitLiquidDrops" },
        new[] { "Terraria.GameContent.Drawing.DrawBlackHelper", "DrawBlack", "EndStrip" },
        new[] { "Terraria.Graphics.TileBatch", "SetLayer", "SetLayerStack", "GetNextSpriteIndex", "SwitchBatch", "Draw" },
    };

    // on the game's code as shipped, before any other patch
    public static void Look(ModuleDefinition terraria)
    {
        ulong h = 14695981039346656037;
        foreach (string[] of in Read)
        {
            var type = terraria.GetType(of[0]);
            if (type == null)
                continue;
            foreach (var m in type.Methods.Where(x => x.HasBody && of.Skip(1).Contains(x.Name)).OrderBy(x => x.FullName, StringComparer.Ordinal))
                foreach (var i in m.Body.Instructions)
                    h = Guard.Sum(h, i);
        }
        known = h == Written;
        if (!known)
            Console.Error.WriteLine($"tile cache: not the game this was written for (its tile routines sum to {h:x16}, expected {Written:x16}); left out");
    }

    // what the cache reads of the game, made public: in the reference TrPatches.dll is compiled
    // against and at install alike
    public static void Open(ModuleDefinition terraria)
    {
        var batch = terraria.GetType("Terraria.Graphics.TileBatch");
        var drawing = terraria.GetType("Terraria.GameContent.Drawing.TileDrawing");
        var random = terraria.GetType("Terraria.Utilities.UnifiedRandom");
        if (batch == null || drawing == null || random == null)
            return;
        var sprite = batch.NestedTypes.FirstOrDefault(t => t.Name == "SpriteData");
        if (sprite != null)
            sprite.IsNestedPublic = true;
        Fields(batch, "_spriteDataQueue");
        foreach (var m in batch.Methods.Where(x => x.Name == "GetNextSpriteIndex" && x.Parameters.Count == 1 && !x.Parameters[0].ParameterType.IsByReference))
            m.IsPublic = true;
        Fields(drawing, "drawBlackHelper", "_rand", "_isActiveAndNotPaused", "_perspectivePlayer", "_shouldShowInvisibleBlocks");
        foreach (var m in drawing.Methods.Where(x => x.Name == "DrawSingleTile" || x.Name == "DrawTiles_EmitParticles" || x.Name == "DrawTiles_GetLightOverride"
            || (x.Name == "GetFinalLight" && x.ReturnType.Name == "Color")))
            m.IsPublic = true;
        Fields(random, "inext", "SeedArray");
    }

    static void Fields(TypeDefinition type, params string[] names)
    {
        foreach (var f in type.Fields.Where(x => names.Contains(x.Name)))
        {
            f.IsPublic = true;
            f.IsInitOnly = false;
        }
    }

    static List<Instruction> Calls(MethodDefinition m, string type, string name) =>
        m.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference r
            && r.Name == name && r.DeclaringType.Name == type).ToList();

    public static int Apply(ModuleDefinition terraria, ModuleDefinition tr)
    {
        Open(terraria);
        if (!known)
            return 0;
        var cache = tr.GetType("TrPatches.TileCache");
        Func<string, MethodReference> at = name => terraria.ImportReference(cache.Methods.Single(m => m.Name == name));
        var recording = terraria.ImportReference(cache.Fields.Single(f => f.Name == "Recording"));
        var batch = terraria.GetType("Terraria.Graphics.TileBatch");
        var drawing = terraria.GetType("Terraria.GameContent.Drawing.TileDrawing");
        var draw = drawing.Methods.Single(m => m.Name == "Draw" && m.Parameters.Count == 3);
        var single = drawing.Methods.Single(m => m.Name == "DrawSingleTile");
        var setLayer = batch.Methods.Single(m => m.Name == "SetLayerStack");
        var next = batch.Methods.Single(m => m.Name == "GetNextSpriteIndex" && m.Parameters[0].ParameterType.Name == "Texture2D");

        var tiles = Calls(draw, "TileDrawing", "DrawSingleTile");
        var black = Calls(single, "DrawBlackHelper", "DrawBlack");
        var emit = Calls(single, "TileDrawing", "DrawTiles_EmitParticles");
        var light = Calls(single, "TileDrawing", "DrawTiles_GetLightOverride");
        var areas = Calls(draw, "TileDrawing", "GetScreenDrawArea");
        if (tiles.Count != 1 || black.Count != 1 || emit.Count != 1 || light.Count != 1 || areas.Count != 1)
            throw new Exception($"tile cache: {tiles.Count} tile, {black.Count} dark, {emit.Count} particle, {light.Count} light and {areas.Count} area calls, expected one of each");
        int n = 0;

        // Draw: the pass starts; each tile goes to the cache
        draw.Body.SimplifyMacros();
        var il = draw.Body.GetILProcessor();
        var first = draw.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_2));
        il.InsertBefore(first, il.Create(OpCodes.Call, at("Pass")));
        tiles[0].OpCode = OpCodes.Call;
        tiles[0].Operand = at("Draw");
        n += 2;

        // Draw: the tiles it is going to walk, as soon as it has them (the call's last four results)
        var outs = new List<VariableDefinition>();
        for (var i = areas[0].Previous; outs.Count < 4 && i != null && i.OpCode == OpCodes.Ldloca; i = i.Previous)
            outs.Insert(0, (VariableDefinition)i.Operand);
        if (outs.Count != 4 || outs.Any(v => v.VariableType.MetadataType != MetadataType.Int32))
            throw new Exception("tile cache: the screen's tiles are not four whole numbers handed to GetScreenDrawArea");
        var after = areas[0].Next;
        foreach (var v in outs)
            il.InsertBefore(after, il.Create(OpCodes.Ldloc, v));
        il.InsertBefore(after, il.Create(OpCodes.Call, at("Area")));
        n++;

        // Draw: each tile of the pass's layer is offered to the cache before the walk does
        // anything for it, with what DrawSingleTile is given; taken, the walk steps to the next.
        // The offer stands where "if (solidLayer)" stood, so that what jumped there comes to it.
        var args = new List<Instruction>();
        for (var i = tiles[0].Previous; args.Count < 5 && i != null; i = i.Previous)
            args.Insert(0, i);
        var layers = Calls(draw, "TileBatch", "SetLayer");
        var solid = layers.Count > 0 ? layers[0].Previous?.Previous?.Previous?.Previous?.Previous : null;
        if (args.Count != 5 || args[0].OpCode != OpCodes.Ldarg || args.Skip(1).Any(i => i.OpCode != OpCodes.Ldloc)
            || solid == null || solid.OpCode != OpCodes.Ldarg || solid.Operand != draw.Parameters[0] || solid.Next.OpCode != OpCodes.Brfalse)
            throw new Exception("tile cache: the walk over the tiles is not the one this was written for");
        var column = args[3].Operand;
        var steps = draw.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldloc && i.Operand == column
            && i.Next.OpCode == OpCodes.Ldc_I4 && (int)i.Next.Operand == 1 && i.Next.Next.OpCode == OpCodes.Add
            && i.Next.Next.Next.OpCode == OpCodes.Stloc && i.Next.Next.Next.Operand == column).ToList();
        if (steps.Count != 1)
            throw new Exception($"tile cache: the walk steps to its next tile in {steps.Count} places, expected one");
        var test = solid.Next;
        solid.Operand = draw.Body.ThisParameter;
        foreach (var a in args.Skip(1))
            il.InsertBefore(test, il.Create(OpCodes.Ldloc, (VariableDefinition)a.Operand));
        il.InsertBefore(test, il.Create(OpCodes.Call, at("Early")));
        il.InsertBefore(test, il.Create(OpCodes.Brtrue, steps[0]));
        il.InsertBefore(test, il.Create(OpCodes.Ldarg, draw.Parameters[0]));
        n++;

        // Draw: the walk's next column is TileCache.Step(column, this, screen, offset, row) where it
        // was column + 1 (Step answers column + 1 unless the tiles' thread found tiles to step over)
        var one = steps[0].Next;
        var add = one.Next;
        one.OpCode = OpCodes.Ldarg;
        one.Operand = draw.Body.ThisParameter;
        add.OpCode = OpCodes.Ldloc;
        add.Operand = args[1].Operand;
        il.InsertAfter(add, il.Create(OpCodes.Call, at("Step")));
        il.InsertAfter(add, il.Create(OpCodes.Ldloc, (VariableDefinition)args[4].Operand));
        il.InsertAfter(add, il.Create(OpCodes.Ldloc, (VariableDefinition)args[2].Operand));
        draw.Body.OptimizeMacros();
        n++;

        // the two routines a tile's case can call before DrawSingleTile tell that they were called
        var noted = terraria.ImportReference(cache.Fields.Single(f => f.Name == "Noted"));
        foreach (string name in new[] { "AddSpecialPoint", "EmitLiquidDrops" })
        {
            var m = drawing.Methods.Single(x => x.Name == name);
            il = m.Body.GetILProcessor();
            first = m.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(first, il.Create(OpCodes.Stsfld, noted));
            n++;
        }

        // DrawSingleTile: the three calls the cache notes
        black[0].OpCode = OpCodes.Call;
        black[0].Operand = at("Black");
        emit[0].OpCode = OpCodes.Call;
        emit[0].Operand = at("Emit");
        light[0].OpCode = OpCodes.Call;
        light[0].Operand = at("Light");
        n += 3;

        // TileBatch.SetLayerStack: while recording, the layer asked for
        setLayer.Body.SimplifyMacros();
        il = setLayer.Body.GetILProcessor();
        first = setLayer.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldsfld, recording));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, at("Layer")));
        setLayer.Body.OptimizeMacros();
        n++;

        // TileBatch.GetNextSpriteIndex(Texture2D): while recording, the place it answers with
        next.Body.SimplifyMacros();
        il = next.Body.GetILProcessor();
        foreach (var ret in next.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            // the old return becomes the test, so that what jumps to it is tested too
            var leave = il.Create(OpCodes.Ret);
            ret.OpCode = OpCodes.Ldsfld;
            ret.Operand = recording;
            il.InsertAfter(ret, leave);
            il.InsertBefore(leave, il.Create(OpCodes.Brfalse, leave));
            il.InsertBefore(leave, il.Create(OpCodes.Dup));
            il.InsertBefore(leave, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(leave, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(leave, il.Create(OpCodes.Call, at("Sprite")));
            n++;
        }
        next.Body.OptimizeMacros();
        Console.Error.WriteLine($"tile cache: {n} changes");
        return n;
    }
}
