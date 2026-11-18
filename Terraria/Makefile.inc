# Terraria 1.4.5.8 (GOG Linux) on an ASTC-capable FNA, patched with MonoMod.
# The speed and memory patches follow the PortMaster port by wild1walker (portmaster-terraria, MIT),
# reimplemented here as MonoMod patches; ASTC texture support comes from our own FNA/FNA3D builds.
# One assembly per Terraria/*.mm.cs (Makefile.common's pattern rule).
#
# deps/Terraria needs: Terraria.exe, ReLogic.dll (extracted from Terraria.exe's resources),
# FNA.dll (our ASTC build) -- see Terraria/README.md.
SRC := $(wildcard $(MAKECMDGOALS)/*.mm.cs)
# TileConverter.dll runs inside MonoMod (from the rules in Terraria.TileRecords.mm.cs), so it is built first and is a dependency
# of every patch assembly
ASM := out/Terraria/TileConverter.dll $(SRC:%.cs=out/%.dll) out/Terraria/trsetup.exe out/Terraria/weave.exe
DEPS_FOLDER ?= deps/$(MAKECMDGOALS)
DEPS := $(DEPS_FOLDER)/Terraria.exe \
        $(DEPS_FOLDER)/ReLogic.dll \
        deps/Cecil/*.dll \
        deps/MonoMod/MonoMod.exe \
        $(DEPS_FOLDER)/FNA.dll \
        out/Terraria/TileConverter.dll

out/Terraria/TileConverter.dll: $(wildcard Terraria/TileConverter/*.cs)
	@mkdir -p out/Terraria
	@MONO_PATH=$(MONO_PATH) $(MONO) $(COMPILER) $@ $^ -- deps/Cecil/*.dll $(COMMON_DEPS)

# weave.exe: MonoMod.exe's steps with the modules read lazily and no symbols (Terraria/Weave/Weave.cs has the why), run on
# the handheld under .NET in MonoMod.exe's place
out/Terraria/weave.exe: $(wildcard Terraria/Weave/*.cs)
	@mkdir -p out/Terraria
	@MONO_PATH=$(MONO_PATH) $(MONO) $(COMPILER) $@ $^ -- deps/MonoMod/MonoMod.exe deps/MonoMod/MonoMod.Utils.dll deps/Cecil/Mono.Cecil.dll $(COMMON_DEPS)

# trsetup.exe: the setup's extract, stubs and content steps (from wild1walker's trpatch), run on the handheld under .NET
out/Terraria/trsetup.exe: $(wildcard Terraria/TrSetup/*.cs)
	@mkdir -p out/Terraria
	@MONO_PATH=$(MONO_PATH) $(MONO) $(COMPILER) $@ $^ -- deps/Cecil/Mono.Cecil.dll $(COMMON_DEPS)

# Terraria.exe's other patches are built with Terraria.TileRecords.mm.dll as a reference: a patch whose runtime reads tiles
# uses its TrPatches.Tiles (MonoMod relinks the reference into Terraria.exe as it weaves both)
TILE_USERS := $(filter-out out/Terraria/Terraria.TileRecords.mm.dll,$(filter out/Terraria/Terraria.%,$(SRC:%.cs=out/%.dll)))
$(TILE_USERS): out/Terraria/%.dll: Terraria/%.cs out/Terraria/TileConverter.dll out/Terraria/Terraria.TileRecords.mm.dll
	@MONO_PATH=$(MONO_PATH) $(MONO) $(COMPILER) $@ $< -- $(DEPS) out/Terraria/Terraria.TileRecords.mm.dll $(COMMON_DEPS)
