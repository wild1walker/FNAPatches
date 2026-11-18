using System;
using System.IO;
using Mono.Cecil;
using MonoMod;

// weave <assembly>: MonoMod.exe's own steps (its Program.Main: Read, ReadMod of the assembly's folder, MapDependencies, AutoPatch,
// Write to MONOMODDED_<assembly>) with two settings changed, for a 1 GB handheld's memory:
//   - modules are read as their members are asked for (ReadingMode.Deferred). MonoMod.exe reads every module whole, the
//     dependencies too: the framework's reference assemblies alone are 300 MB of objects before the weave begins;
//   - no debug symbols are read or written (the setup deleted the .mdb MonoMod wrote).
// The woven Terraria.exe and ReLogic.dll come out byte for byte as MonoMod.exe writes them. Weaving Terraria.exe with all the
// patches, peak resident size on a desktop: MonoMod.exe 742 MB, this 587 MB, and 534 MB with DOTNET_GCConserveMemory=9 and
// DOTNET_GCgen0MaxBudget=0x1000000 (what the port's setup sets for it).
// WEAVE_READ=immediate, WEAVE_SYMBOLS=1: as MonoMod.exe. WEAVE_MEM=1: the live heap after each step.
static class Weave
{
    static void Mem(string what)
    {
        if (Environment.GetEnvironmentVariable("WEAVE_MEM") != "1") return;
        long before = GC.GetTotalMemory(false), live = GC.GetTotalMemory(true);
        Console.WriteLine("[weave] " + what + ": live " + (live >> 20) + " MB (heap " + (before >> 20) + " MB)");
    }

    static int Main(string[] args)
    {
        string input = args[0];
        string output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)), "MONOMODDED_" + Path.GetFileName(input));
        if (File.Exists(output)) File.Delete(output);
        bool deferred = Environment.GetEnvironmentVariable("WEAVE_READ") != "immediate";
        bool symbols = Environment.GetEnvironmentVariable("WEAVE_SYMBOLS") == "1";
        try
        {
            using (var mm = new MonoModder { InputPath = input, OutputPath = output })
            {
                if (deferred)
                    mm.ReadingMode = ReadingMode.Deferred;
                if (!symbols)
                {
                    mm.ReaderParameters.ReadSymbols = false;
                    mm.WriterParameters.WriteSymbols = false;
                    mm.WriterParameters.SymbolWriterProvider = null;
                }
                mm.Read(); Mem("read");
                mm.ReadMod(Directory.GetParent(Path.GetFullPath(input)).FullName); Mem("mods read");
                mm.MapDependencies(); Mem("dependencies mapped");
                mm.PostProcessors = (PostProcessor)Delegate.Combine(new PostProcessor(m => Mem("patched, before the passes")), mm.PostProcessors);
                mm.PostProcessors += m => Mem("after the passes");
                mm.AutoPatch(); Mem("autopatch done");
                mm.Write(); Mem("written");
                mm.Log("[Main] Done.");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return -1;
        }
        return 0;
    }
}
