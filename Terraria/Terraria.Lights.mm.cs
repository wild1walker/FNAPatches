using Microsoft.Xna.Framework;
using MonoMod.InlineRT;
using MonoMod;
using System.Collections.Generic;
using System;
using Terraria.DataStructures;
using Terraria.Graphics.Light;

#pragma warning disable 1591, 0649, 0169, 0414

// The legacy lighting's lights (wild1walker, portmaster-terraria, MIT: tools/trpatch/LightsPatch.cs and runtime/Lights.cs):
// Retro, White and Trippy gather an update's lights in a dictionary by tile, and a light for a tile that has one already
// raises it; LegacyLighting.AddLight now asks TrPatches.Lights first and returns where the call cannot change anything (the
// rules there). TR_LIGHTS=0: as before; TR_LIGHTS_AUDIT=1 checks. TERRARIA_LIGHTS=0 leaves the pass out when patching.
namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (Environment.GetEnvironmentVariable("TERRARIA_SPEED") == "0" || Environment.GetEnvironmentVariable("TERRARIA_LIGHTS") == "0")
				return;
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.Lights, "lights", m => TerrariaPatches.TileConvert.LightsPass.Apply(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace TrPatches
{
    // The legacy lighting (Retro, the port's setting; White, Trippy) gathers the lights of an update in
    // a dictionary by tile: a light for a tile that has one already raises it where it is brighter
    // (each colour by itself, or their mean in Retro and White), and leaves it as it is otherwise.
    // Dust lights its tile at every update, and dust comes in groups: a bullet's trail leaves ten in a
    // row on one tile, each as bright as the others, every update (in the pirate fight about 600 calls
    // an update, 0.2 ms of a frame on the RK3326, most of it in the dictionary's lookups).
    //
    // LegacyLighting.AddLight asks here first (trpatch, LightsPatch) and returns at once when the call
    // cannot change anything: a call for the same tile as the call just before it, with a light not
    // brighter than the one the dictionary holds for the tile, by the game's own comparison (the game
    // would write nothing, or the same value back). The value is looked up here when a tile's second
    // call in a row comes, and stays right while the calls go on for that tile: every write of the
    // dictionary is made by AddLight, which asks here first (a call for another tile, or one that the
    // game will write, forgets the value), or is its clearing (PreRenderPhase tells: Cleared), or a new
    // dictionary (Rebuild: compared by reference). A call for another tile looks nothing up, so a lone
    // light costs a comparison. The game's other early returns (outside the light's area, 2000 lights)
    // change nothing either, so a call left out here never differs from the game's.
    // TR_LIGHTS=0: every call goes to the game. TR_LIGHTS_AUDIT=1 (tests): nothing is left out; for
    // each call that would have been, the dictionary's value is looked up and checked.
    public static class Lights
    {
        static readonly bool on = Environment.GetEnvironmentVariable("TR_LIGHTS") != "0";
        static readonly bool audit = Environment.GetEnvironmentVariable("TR_LIGHTS_AUDIT") == "1";
        public static bool Off;         // tests (Parts.Sweep, "@lights")
        public static long Asked, Passed, Looked, Wrong;

        static object of;     // the dictionary the value is of (FNAPatches: as an object, for its identity only)
        static int lastX = int.MinValue, lastY;
        static bool known;
        // (FNAPatches) the looked-up value's three channels: a field of the game's private ColorTriplet does not compile here
        static float valueR, valueG, valueB;

        // LegacyLighting.AddLight, first: true when the game's call would change nothing
        public static bool Same(LegacyLighting engine, int x, int y, Vector3 color)
        {
            // (FNAPatches) portmaster-terraria also sends every call to the game while its frame lighting runs on a thread of
            // its own (LightAhead, opt-in there); that thread is not in this port
            if (!on || Off)
                return false;
            Asked++;
            var lights = engine._tempLights;
            if (x != lastX || y != lastY || (object)lights != of || lights == null)
            {
                lastX = x;
                lastY = y;
                of = lights;
                known = false;
                return false;
            }
            if (!known)
            {
                Looked++;
                if (!lights.TryGetValue(new Point16(x, y), out var found))
                    return false;
                valueR = found.R; valueG = found.G; valueB = found.B;
                known = true;
            }
            if (!Dim(engine, valueR, valueG, valueB, color))
            {
                known = false;
                return false;
            }
            Passed++;
            if (audit)
            {
                if (!lights.TryGetValue(new Point16(x, y), out var now) || !Dim(engine, now.R, now.G, now.B, color))
                {
                    Wrong++;
                    if (Wrong <= 20)
                        Console.Error.WriteLine($"[lights] a light at {x},{y} would have been left out that the game writes");
                }
                return false;
            }
            return true;
        }

        // the game's own test for a light at a tile that has one: nothing is written when it is false
        static bool Dim(LegacyLighting engine, float r, float g, float b, Vector3 color)
        {
            if (engine._rgb)
                return !(r < color.X) && !(g < color.Y) && !(b < color.Z);
            return !(r < (color.X + color.Y + color.Z) / 3f);
        }

        // LegacyLighting.PreRenderPhase, after it cleared the dictionary (on the lighting thread when
        // that is on; the game thread's lights are held meanwhile)
        public static void Cleared()
        {
            known = false;
            of = null;
            lastX = int.MinValue;
        }

        public static string Line() => $"lights {Passed} of {Asked} passed, {Looked} looked up" + (audit ? $" wrong {Wrong}" : "");
    }
}
