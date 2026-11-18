// From the PortMaster port by wild1walker (portmaster-terraria, tools/trpatch/DrawCuts.cs, MIT), except where marked (FNAPatches).
using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Work the game's drawing does for every tile or sprite with nothing to show for it. What is drawn
// stays the same. (After the tile conversion: the code here is written for Tile pointers.)
static class DrawCuts
{
    public static int Apply(ModuleDefinition m, ModuleDefinition tr)
    {
        Open(m);
        return TileInfo(m) + EveryFrame(m) + SpriteUpload(m) + Clouds(m, tr) + Stars(m, tr) + Pylons(m) + Dust(m, tr) + Water(m, tr);
    }

    // what TrPatches reads of the game's own (the reference it is compiled against has the same:
    // Program.Refs): the sprite buffer's vertices and textures (TrPatches.Clouds, the runs)
    public static void Open(ModuleDefinition m)
    {
        var buffer = m.GetType("Terraria.DataStructures.SpriteDrawBuffer");
        if (buffer == null)
            return;
        foreach (var f in buffer.Fields.Where(x => x.Name == "vertices" || x.Name == "textures"))
            f.IsPublic = true;
    }

    static bool Calls(Instruction i, MethodDefinition method) =>
        (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
        && i.Operand is MethodReference r && r.Name == method.Name && r.DeclaringType.FullName == method.DeclaringType.FullName
        && r.Resolve() == method;

    static bool Is(object operand, FieldDefinition field) =>
        operand is FieldReference r && r.Name == field.Name && r.DeclaringType.FullName == field.DeclaringType.FullName;

    static System.Collections.Generic.IEnumerable<MethodDefinition> Bodies(ModuleDefinition m) =>
        m.Types.Where(TerrariaPatches.TileConvert.Passes.Game).SelectMany(Nested)  /* the game's types only (FNAPatches) */.SelectMany(t => t.Methods).Where(x => x.HasBody);

    static System.Collections.Generic.IEnumerable<TypeDefinition> Nested(TypeDefinition t) =>
        new[] { t }.Concat(t.NestedTypes.SelectMany(Nested));

    // TileDrawing.DrawSingleTile makes a TileDrawInfo and its array of colours for every tile it draws,
    // and nothing keeps either after the call: about 1.1 ms of a frame on the H700, and the collections
    // that follow. One object instead, set back to what a new one holds.
    static int TileInfo(ModuleDefinition m)
    {
        var info = m.GetType("Terraria.DataStructures.TileDrawInfo");
        var ctor = info.Methods.Single(x => x.IsConstructor && !x.IsStatic);
        var c = ctor.Body.Instructions;
        // the known constructor: the array of colours, nothing else
        if (c.Count != 7 || c[0].OpCode != OpCodes.Ldarg_0 || c[1].OpCode != OpCodes.Ldc_I4_S || c[2].OpCode != OpCodes.Newarr
            || c[3].OpCode != OpCodes.Stfld || c[5].OpCode != OpCodes.Call
            || ((MethodReference)c[5].Operand).DeclaringType.FullName != "System.Object")
            throw new InvalidOperationException("TileDrawInfo's constructor is not the known one");
        var slices = ((FieldReference)c[3].Operand).Resolve();
        int length = (sbyte)c[1].Operand;
        if (info.Methods.Count != 1 || info.Fields.Any(f => f.IsStatic))
            throw new InvalidOperationException("TileDrawInfo is not the known class");
        foreach (var method in Bodies(m))
            foreach (var ins in method.Body.Instructions)
                if (method != ctor && (ins.OpCode == OpCodes.Stfld || ins.OpCode == OpCodes.Ldflda)
                    && Is(ins.Operand, slices))
                    throw new InvalidOperationException($"{method.FullName} changes TileDrawInfo's array");

        var shared = new FieldDefinition("trShared", FieldAttributes.Private | FieldAttributes.Static, info);
        var fresh = new MethodDefinition("trFresh", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, info);
        var il = fresh.Body.GetILProcessor();
        var reset = il.Create(OpCodes.Nop);
        il.Emit(OpCodes.Ldsfld, shared);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, reset);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Newobj, ctor);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Stsfld, shared);
        il.Emit(OpCodes.Ret);
        il.Append(reset);                                     // (the object is on the stack)
        var array = new TypeReference("System", "Array", m, m.TypeSystem.CoreLibrary);
        var clear = new MethodReference("Clear", m.TypeSystem.Void, array);
        clear.Parameters.Add(new ParameterDefinition(array));
        clear.Parameters.Add(new ParameterDefinition(m.TypeSystem.Int32));
        clear.Parameters.Add(new ParameterDefinition(m.TypeSystem.Int32));
        foreach (var f in info.Fields)
        {
            il.Emit(OpCodes.Dup);
            if (f == slices)
            {
                il.Emit(OpCodes.Ldfld, f);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldc_I4, length);
                il.Emit(OpCodes.Call, clear);
            }
            else if (f.FieldType.IsPointer)
            {
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Stfld, f);
            }
            else
            {
                il.Emit(OpCodes.Ldflda, f);
                il.Emit(OpCodes.Initobj, f.FieldType);
            }
        }
        il.Emit(OpCodes.Ret);

        int made = 0;
        foreach (var method in Bodies(m))
            foreach (var ins in method.Body.Instructions)
                if (ins.OpCode == OpCodes.Newobj && Calls(ins, ctor))
                {
                    if (method.Name != "DrawSingleTile")
                        throw new InvalidOperationException($"{method.FullName} makes a TileDrawInfo");
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = fresh;
                    made++;
                }
        if (made != 1)
            throw new InvalidOperationException($"{made} places make a TileDrawInfo, 1 expected");
        info.Fields.Add(shared);
        info.Methods.Add(fresh);
        return 3;
    }

    // The map's pylon layer is drawn with the small map, every frame, and starts by searching the tiles
    // within the player's reach for a pylon (0.23 ms on the H700) to pick the colour of the pylons'
    // icons. With no pylon in the world there is no icon, and nothing else in the method runs: it
    // returns once it has the empty list.
    static int Pylons(ModuleDefinition m)
    {
        var draw = m.GetType("Terraria.Map.TeleportPylonsMapLayer").Methods.Single(x => x.Name == "Draw");
        var i = draw.Body.Instructions;
        var count = i.Select(x => x.Operand).OfType<MethodReference>().First(r => r.Name == "get_Count"
            && r.DeclaringType.FullName.StartsWith("System.Collections.Generic.List`1<Terraria.GameContent.TeleportPylonInfo>"));
        if (!(i[0].OpCode == OpCodes.Ldsfld && ((FieldReference)i[0].Operand).Name == "PylonSystem" && i[1].OpCode == OpCodes.Callvirt
            && ((MethodReference)i[1].Operand).Name == "get_Pylons" && i[2].OpCode == OpCodes.Stloc_0))
            throw new InvalidOperationException("TeleportPylonsMapLayer.Draw does not start with the list of pylons");
        // every use of the list after: its Count as a loop's bound, and elements within those loops
        var il = draw.Body.GetILProcessor();
        var next = i[3];
        il.InsertBefore(next, il.Create(OpCodes.Ldloc_0));
        il.InsertBefore(next, il.Create(OpCodes.Callvirt, count));
        il.InsertBefore(next, il.Create(OpCodes.Brtrue, next));
        il.InsertBefore(next, il.Create(OpCodes.Ret));
        return 1;
    }

    // Lighting.UpdateEveryFrame asks the view's matrix whether the view is zoomed, and DrawSingleTile
    // asks it for every tile: about 0.5 ms of a frame. The answer cannot change within a pass over the
    // tiles: TileDrawing.Draw asks once, as it starts.
    static int EveryFrame(ModuleDefinition m)
    {
        var drawing = m.GetType("Terraria.GameContent.Drawing.TileDrawing");
        var getter = m.GetType("Terraria.Lighting").Methods.Single(x => x.Name == "get_UpdateEveryFrame");
        var single = drawing.Methods.Single(x => x.Name == "DrawSingleTile");
        var draw = drawing.Methods.Single(x => x.Name == "Draw");
        foreach (var method in Bodies(m))
            if (method != draw && method.Body.Instructions.Any(i => Calls(i, single)))
                throw new InvalidOperationException($"{method.FullName} calls DrawSingleTile");
        var asked = single.Body.Instructions.Where(i => Calls(i, getter)).ToList();
        if (asked.Count != 1)
            throw new InvalidOperationException($"DrawSingleTile asks UpdateEveryFrame {asked.Count} times, 1 expected");
        var field = new FieldDefinition("trEveryFrame", FieldAttributes.Private | FieldAttributes.Static, m.TypeSystem.Boolean);
        drawing.Fields.Add(field);
        asked[0].OpCode = OpCodes.Ldsfld;
        asked[0].Operand = field;
        var il = draw.Body.GetILProcessor();
        var first = draw.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Call, getter));
        il.InsertBefore(first, il.Create(OpCodes.Stsfld, field));
        return 3;
    }

    // SpriteDrawBuffer.DrawRange uploads its whole array of vertices (8192 of them, 196 KB, into a new
    // buffer) with the first sprite it draws after sprites were queued, whatever their number: the clouds'
    // three layers and the player, every frame. The queued sprites only, and never fewer than the call
    // draws.
    static int SpriteUpload(ModuleDefinition m)
    {
        var buffer = m.GetType("Terraria.DataStructures.SpriteDrawBuffer");
        var vertices = buffer.Fields.Single(f => f.Name == "vertices");
        var vertexCount = buffer.Fields.Single(f => f.Name == "vertexCount");
        var queued = new FieldDefinition("trQueued", FieldAttributes.Private, m.TypeSystem.Int32);
        buffer.Fields.Add(queued);

        // the vertices are written by PushVertex only, which QueueSprite alone calls
        var push = buffer.Methods.Single(x => x.Name == "PushVertex");
        var queue = buffer.Methods.Single(x => x.Name == "QueueSprite");
        foreach (var method in Bodies(m))
        {
            if (method != queue && method.Body.Instructions.Any(i => Calls(i, push)))
                throw new InvalidOperationException($"{method.FullName} calls SpriteDrawBuffer.PushVertex");
            if (method != push && method.Name != "DrawRange" && method.Body.Instructions.Any(i =>
                    (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Ldflda) && Is(i.Operand, vertexCount)))
                throw new InvalidOperationException($"{method.FullName} writes SpriteDrawBuffer.vertexCount");
        }
        var il = queue.Body.GetILProcessor();
        var rets = queue.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        if (rets.Count != 1 || queue.Body.Instructions.Any(i => i.Operand == rets[0]))
            throw new InvalidOperationException("SpriteDrawBuffer.QueueSprite is not as expected");
        il.InsertBefore(rets[0], il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(rets[0], il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(rets[0], il.Create(OpCodes.Ldfld, vertexCount));
        il.InsertBefore(rets[0], il.Create(OpCodes.Stfld, queued));

        // min(vertices.Length - index * 4, ...) becomes
        // min(min(max(queued, (index + count) * 4), vertices.Length) - index * 4, ...)
        var range = buffer.Methods.Single(x => x.Name == "DrawRange");
        var lens = range.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldlen).ToList();
        var min = range.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).FullName
            == "System.Int32 System.Math::Min(System.Int32,System.Int32)").Select(i => (MethodReference)i.Operand).FirstOrDefault();
        if (lens.Count != 1 || min == null || lens[0].Previous.OpCode != OpCodes.Ldfld
            || !Is(lens[0].Previous.Operand, vertices) || lens[0].Next.OpCode != OpCodes.Conv_I4
            || lens[0].Previous.Previous.OpCode != OpCodes.Ldarg_0 || range.Parameters.Count != 2)
            throw new InvalidOperationException("SpriteDrawBuffer.DrawRange is not as expected");
        var max = new MethodReference("Max", m.TypeSystem.Int32, min.DeclaringType);
        max.Parameters.Add(new ParameterDefinition(m.TypeSystem.Int32));
        max.Parameters.Add(new ParameterDefinition(m.TypeSystem.Int32));
        il = range.Body.GetILProcessor();
        var at = lens[0].Previous.Previous;                   // ldarg.0, of vertices.Length
        il.InsertBefore(at, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(at, il.Create(OpCodes.Ldfld, queued));
        il.InsertBefore(at, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(at, il.Create(OpCodes.Ldarg_2));
        il.InsertBefore(at, il.Create(OpCodes.Add));
        il.InsertBefore(at, il.Create(OpCodes.Ldc_I4_4));
        il.InsertBefore(at, il.Create(OpCodes.Mul));
        il.InsertBefore(at, il.Create(OpCodes.Call, max));
        il.InsertAfter(lens[0].Next, il.Create(OpCodes.Call, min));
        return 3;
    }

    // The background's clouds are drawn one by one under the game's cloud shader, which reads the cloud's
    // texture five times for every pixel to light the edges that face the sun or the moon, times a
    // strength. The strength is 0 for most of the day, and the result is then the texture times the
    // colour: the stock sprite shader's, with one read (TrPatches.Clouds decides, cloud by cloud as the
    // game does). NextHorizonRenderer.CloudsEnd asks TrPatches.Clouds at the four places; under the
    // sprite shader the clouds' draw calls are then joined into runs (TrPatches.Clouds.Single).
    static int Clouds(ModuleDefinition m, ModuleDefinition tr)
    {
        var end = m.GetType("Terraria.GameContent.Drawing.NextHorizonRenderer").Methods.Single(x => x.Name == "CloudsEnd");
        var clouds = tr.GetType("TrPatches.Clouds");
        int changed = 0;
        foreach (var (type, name, to) in new[] {
            ("Terraria.Graphics.Shaders.MiscShaderData", "UseShaderSpecificData", "Strength"),
            ("Terraria.Graphics.Shaders.MiscShaderData", "Apply", "Apply"),
            ("Terraria.DataStructures.SpriteDrawBuffer", "DrawSingle", "Single"),
            ("Terraria.DataStructures.SpriteDrawBuffer", "Unbind", "End") })
        {
            var calls = end.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference r && r.Name == name && r.DeclaringType.FullName == type).ToList();
            var target = clouds.Methods.Single(x => x.Name == to);
            var from = calls.Count == 1 ? (MethodReference)calls[0].Operand : null;
            // the same values on the stack: the object, then the call's arguments
            if (from == null || !from.HasThis || target.Parameters.Count != from.Parameters.Count + 1
                || target.ReturnType.FullName != from.ReturnType.FullName
                || target.Parameters[0].ParameterType.FullName != type
                || from.Parameters.Where((p, k) => p.ParameterType.FullName != target.Parameters[k + 1].ParameterType.FullName).Any())
                throw new InvalidOperationException($"NextHorizonRenderer.CloudsEnd: {name} is not called as expected");
            calls[0].OpCode = OpCodes.Call;
            calls[0].Operand = m.ImportReference(target);
            changed++;
        }
        return changed;
    }

    // The sky's stars, one draw call each where the texture changes: held and drawn texture by texture
    // (TrPatches.Stars has the reason the picture is the same). Main.DrawStarsInBackground tells it
    // where the stars start and end, and DrawStar's two draws go through it.
    static int Stars(ModuleDefinition m, ModuleDefinition tr)
    {
        var main = m.GetType("Terraria.Main");
        var stars = tr.GetType("TrPatches.Stars");
        var batch = main.Fields.Single(f => f.Name == "spriteBatch" && f.IsStatic);
        var all = main.Methods.Single(x => x.Name == "DrawStarsInBackground");
        var one = main.Methods.Single(x => x.Name == "DrawStar");
        var target = stars.Methods.Single(x => x.Name == "Draw");
        var draws = one.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == "Draw"
            && r.DeclaringType.FullName == "Microsoft.Xna.Framework.Graphics.SpriteBatch").ToList();
        if (draws.Count != 2 || all.Body.HasExceptionHandlers || draws.Any(d =>
        {
            var from = (MethodReference)d.Operand;
            return from.Parameters.Count + 1 != target.Parameters.Count
                || from.Parameters.Where((p, k) => p.ParameterType.FullName != target.Parameters[k + 1].ParameterType.FullName).Any();
        }))
            throw new InvalidOperationException("Main.DrawStar: the stars are not drawn as expected");
        foreach (var d in draws)
        {
            d.OpCode = OpCodes.Call;
            d.Operand = m.ImportReference(target);
        }
        var il = all.Body.GetILProcessor();
        var first = all.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldsfld, batch));
        il.InsertBefore(first, il.Create(OpCodes.Call, m.ImportReference(stars.Methods.Single(x => x.Name == "Begin"))));
        // every return: the instruction a branch may aim at becomes the first of the three
        foreach (var ret in all.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            ret.OpCode = OpCodes.Ldsfld;
            ret.Operand = batch;
            var end = il.Create(OpCodes.Call, m.ImportReference(stars.Methods.Single(x => x.Name == "End" && x.IsPublic)));
            il.InsertAfter(ret, end);
            il.InsertAfter(end, il.Create(OpCodes.Ret));
        }
        return draws.Count + 1;
    }

    // Main.DrawDust's own Begin, End and Draw of the sprite batch go through TrPatches.Offscreen, which
    // knows the batch's state from them and leaves out the sprites that cannot reach the screen. Every
    // call DrawDust makes on the batch must be one of the three, with Offscreen's like it. Its asks for
    // a tile's light (Lighting.GetColor(int, int)) go there too.
    static int Dust(ModuleDefinition m, ModuleDefinition tr)
    {
        var draw = m.GetType("Terraria.Main").Methods.Single(x => x.Name == "DrawDust");
        var offscreen = tr.GetType("TrPatches.Offscreen");
        var counts = new System.Collections.Generic.Dictionary<string, int> { ["Begin"] = 0, ["End"] = 0, ["Draw"] = 0 };
        var light = offscreen.Methods.Single(x => x.Name == "Light");
        int lights = 0;
        foreach (var i in draw.Body.Instructions)
        {
            if (i.OpCode == OpCodes.Call && i.Operand is MethodReference g && g.Name == "GetColor" && g.DeclaringType.FullName == "Terraria.Lighting"
                && g.Parameters.Count == 2 && g.Parameters.All(p => p.ParameterType.FullName == "System.Int32")
                && g.ReturnType.FullName == light.ReturnType.FullName)
            {
                i.Operand = m.ImportReference(light);
                lights++;
                continue;
            }
            if ((i.OpCode != OpCodes.Callvirt && i.OpCode != OpCodes.Call) || !(i.Operand is MethodReference r)
                || r.DeclaringType.FullName != "Microsoft.Xna.Framework.Graphics.SpriteBatch")
                continue;
            var target = offscreen.Methods.SingleOrDefault(x => x.Name == r.Name && x.IsPublic && x.IsStatic
                && x.Parameters.Count == r.Parameters.Count + 1 && r.HasThis
                && x.Parameters[0].ParameterType.FullName == r.DeclaringType.FullName
                && x.ReturnType.FullName == r.ReturnType.FullName
                && r.Parameters.Select((p, k) => p.ParameterType.FullName == x.Parameters[k + 1].ParameterType.FullName).All(b => b));
            if (target == null || !counts.ContainsKey(r.Name))
                throw new InvalidOperationException($"Main.DrawDust: SpriteBatch.{r.Name} is not called as expected");
            i.OpCode = OpCodes.Call;
            i.Operand = m.ImportReference(target);
            counts[r.Name]++;
        }
        if (counts["Begin"] != 3 || counts["End"] != 2 || counts["Draw"] != 11 || lights != 6)
            throw new InvalidOperationException($"Main.DrawDust: {counts["Begin"]} Begin, {counts["End"]} End, {counts["Draw"]} Draw, {lights} lights; 3, 2, 11 and 6 expected");
        return counts.Values.Sum() + lights;
    }

    // Main.oldDrawWater's first question of a tile, Main.tile[x, y].liquid in the loops (x the inner
    // loop's), answered from TrPatches.Water's copy, made as the routine starts
    static int Water(ModuleDefinition m, ModuleDefinition tr)
    {
        var draw = m.GetType("Terraria.Main").Methods.Single(x => x.Name == "oldDrawWater");
        var water = tr.GetType("TrPatches.Water");
        var c = draw.Body.Instructions;
        int k = Enumerable.Range(2, c.Count - 4).First(n => c[n].OpCode == OpCodes.Call && c[n].Operand is MethodReference r
            && r.Name == "At" && r.DeclaringType.FullName == "TrPatches.Tiles" && r.Parameters.Count == 2
            && c[n + 1].OpCode == OpCodes.Ldfld && ((FieldReference)c[n + 1].Operand).Name == "liquid");
        // the known question: x and y in locals, then "<= 0" ends the round
        bool Local(Instruction i) => i.OpCode == OpCodes.Ldloc || i.OpCode == OpCodes.Ldloc_S;
        if (!Local(c[k - 2]) || !Local(c[k - 1]) || c[k + 2].OpCode != OpCodes.Ldc_I4_0
            || (c[k + 3].OpCode != OpCodes.Ble && c[k + 3].OpCode != OpCodes.Ble_S))
            throw new InvalidOperationException("Main.oldDrawWater: the liquid question is not the known one");
        c[k].Operand = m.ImportReference(water.Methods.Single(x => x.Name == "Liquid"));
        c[k + 1].OpCode = OpCodes.Nop;
        c[k + 1].Operand = null;
        var il = draw.Body.GetILProcessor();
        il.InsertBefore(c[0], il.Create(OpCodes.Call, m.ImportReference(water.Methods.Single(x => x.Name == "Begin"))));
        return 2;
    }
}
