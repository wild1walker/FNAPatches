using System;
using MonoMod.InlineRT;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;
using MonoMod;
using SDL2;
using Terraria.Utilities;

// Terraria 1.4.5.8 calls a handful of SDL3 functions directly (not through FNA). We build FNA for SDL2 and ship no
// SDL3 library, so each of those calls is redirected to its SDL2 equivalent here.

namespace Terraria.Patches
{
	static class Sdl3ToSdl2
	{
		// Program.LogFNANativeLibVersions: "SDL v<int>.<revision>". SDL3's version is major*1000000 + minor*1000 + micro.
		[MonoModLinkFrom("System.Int32 SDL3.SDL::SDL_GetVersion()")]
		public static int SDL_GetVersion()
		{
			SDL.SDL_version v;
			SDL.SDL_GetVersion(out v);
			return v.major * 1000000 + v.minor * 1000 + v.patch;
		}

		[MonoModLinkFrom("System.String SDL3.SDL::SDL_GetRevision()")]
		public static string SDL_GetRevision()
		{
			return SDL.SDL_GetRevision();
		}

		// Main..ctor and LegacyAudioSystem.Init read these for logging / backend decisions
		[MonoModLinkFrom("System.String SDL3.SDL::SDL_GetCurrentVideoDriver()")]
		public static string SDL_GetCurrentVideoDriver()
		{
			return SDL.SDL_GetCurrentVideoDriver();
		}

		[MonoModLinkFrom("System.String SDL3.SDL::SDL_GetCurrentAudioDriver()")]
		public static string SDL_GetCurrentAudioDriver()
		{
			return SDL.SDL_GetCurrentAudioDriver();
		}
	}
}

namespace Terraria.Utilities
{
	// Screenshots and map captures. The original builds an SDL3 surface and calls SDL_SavePNG_IO, which SDL2 lacks; FNA's
	// own Texture2D.SaveAsPng writes the same RGBA data.
	[MonoModPatch("global::Terraria.Utilities.PlatformUtilities")]
	static class patch_PlatformUtilities
	{
		[MonoModReplace]
		public static void SavePng(string path, int width, int height, byte[] data)
		{
			if (width * height * 4 > data.Length)
			{
				throw new IndexOutOfRangeException();
			}
			GraphicsDevice device = Main.instance.GraphicsDevice;
			using (Texture2D texture = new Texture2D(device, width, height, false, SurfaceFormat.Color))
			{
				texture.SetData(data, 0, width * height * 4);
				using (FileStream stream = File.Create(path))
				{
					texture.SaveAsPng(stream, width, height);
				}
			}
		}
	}

	// Error and confirmation dialogs through SDL2's message box instead of SDL3's.
	[MonoModPatch("global::Terraria.Utilities.MessageBox")]
	static class patch_MessageBox
	{
		// The game's message boxes go to the log and answer with their default button. A handheld has no window manager to show a
		// box and often no way to press its buttons, and FNA's SDL2 binding of SDL_ShowMessageBox faults under .NET 10 while it
		// marshals the struct (Marshal.StructureToPtr of the null colorScheme: AccessViolationException), so an error the game
		// wanted to show (Program.DisplayException) ended the process without a word of it in log.txt.
		[MonoModReplace]
		public static int Show(string message, string title, string[] buttonLabels, int returnButtonIndex = -1, int cancelButtonIndex = -1, MessageBoxIcon icon = MessageBoxIcon.None)
		{
			int answer = returnButtonIndex >= 0 && returnButtonIndex < buttonLabels.Length ? returnButtonIndex : 0;
			Console.Error.WriteLine("[message box] " + title + ": " + message
				+ (buttonLabels.Length > 1 ? " (answered: " + buttonLabels[answer] + ")" : ""));
			return answer;
		}
	}
}

namespace MonoMod
{
	public static class MonoModRules
	{
		static MonoModRules()
		{
			if (TerrariaPatches.TileConvert.Passes.Add(TerrariaPatches.TileConvert.Passes.View, "view", m => TerrariaPatches.TileConvert.View.Apply(m)))
				MonoModRule.Modder.PostProcessors += delegate (MonoModder modder) { TerrariaPatches.TileConvert.Passes.Run(modder.Module); };
		}
	}
}

namespace Terraria.Patches
{
	// The game's smallest window, 800 x 600 unless TR_VIEW_MIN_W/H (the launcher: the screen's size) ask for less
	public static class View
	{
		public static int MinWidth() { return Read("TR_VIEW_MIN_W", 800); }
		public static int MinHeight() { return Read("TR_VIEW_MIN_H", 600); }

		static int Read(string name, int fallback)
		{
			int v;
			return int.TryParse(Environment.GetEnvironmentVariable(name), out v) && v >= 320 && v < fallback ? v : fallback;
		}
	}

	// Switches on the opt-in SpriteBatch extensions our FNA build adds (see the FNA `terraria` branch).
	static class FnaHooks
	{
		public static void Install()
		{
			SpriteBatch.SkipTransparentSpritesEXT = Environment.GetEnvironmentVariable("TR_ZERO") != "0";
			SpriteBatch.ImmediateDiscardEXT = Environment.GetEnvironmentVariable("TR_DISCARD") != "0";
		}
	}
}

namespace Terraria
{
	[MonoModPatch("global::Terraria.Program")]
	static class patch_Program
	{
		public extern static void orig_LaunchGame(string[] args, bool monoArgs);

		public static void LaunchGame(string[] args, bool monoArgs = false)
		{
			Terraria.Patches.FnaHooks.Install();
			orig_LaunchGame(args, monoArgs);
		}

		// Terraria compiles every method of every loaded assembly on a background thread at start ("force JIT"). That costs
		// memory and time, and on .NET it throws for assemblies that cannot be loaded here; the game's code is precompiled instead.
		public static void ForceLoadAssembly(System.Reflection.Assembly assembly, bool initializeStaticMembers)
		{
		}
	}
}
