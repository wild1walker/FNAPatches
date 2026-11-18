# Terraria patches

MonoMod patches for Terraria 1.4.5.8 (GOG Linux), for the PortMaster port on 1 GB ARM handhelds. The game runs on .NET 10 with our SDL2 builds of FNA, FNA3D and FAudio (their `terraria` branches). The port itself lives in the `jantrueno` branch of wild1walker's portmaster-terraria.

The lazy textures and fonts and the trpatch speed passes follow wild1walker's port (MIT), redone here as MonoMod patches. Each file says at the top what it takes from it.

## The patches

- **TerrariaPatches** (Terraria.exe, ReLogic.dll): the game calls SDL3 directly in a few places. Those calls go to SDL2 instead.
- **NetAccess**: lets the patched game reach private members under .NET.
- **Lazy**: textures and sounds load the first time they're used. Textures that haven't been drawn for a while are unloaded.
- **Fonts**: a font page gets its texture only when one of its glyphs is first drawn. Without this the fonts take about 200 MB.
- **TileRecords**: the world's tiles become 14-byte structs in one flat array instead of objects. This is the biggest memory saving.
- **Pack**: reads the textures from one ASTC archive made by [GPU-ASTC](https://github.com/JanTrueno/GPU-ASTC).
- **Speed**: wild1walker's trpatch passes:
  - draw cuts, with the clouds' shader where it adds nothing, the dust out of sight and the water's first question from a copy;
  - the interface's draw: the minimap's bags and icons, projectile searches, players and lock-on marks with nothing to do;
  - field and loop scans;
  - splitting the huge routines;
  - no zeroed locals.
- **Bosses**: bosses of many parts (the Destroyer, the Twins, Skeletron Prime): six places where the game does work for every part, or every NPC, whose answer is already known.
- **TileCache**: what the game's routine draws for a tile is kept and put back the next frame while nothing it reads has changed; the solid pass's questions are asked on a second core.
- **Frames**: the tiles of the sections the view is moving towards are framed ahead, a little each update, instead of 30,000 at once when a section comes into view.
- **Throttle**: the game's debug frame statistics are worked out only while something reads them, the map's player heads are rendered every 6th frame, and the map's section drawing gets 2 ms a frame instead of 5.
- **Lights**: Retro, White and Trippy lighting no longer hands the light dictionary lights that cannot change a tile.
- **Rests**: the chest list's lid walk passes the chests at rest.
- **Settled**: an item lying still keeps the answers of its five block questions while the blocks around it stay as they were.
- **Quiet**: full collections are made while the player is dead, the map is open or the game is paused (or at a calm moment when memory is short), not as a long pause in play.
- **Jit**: the runtime records the methods a session calls, and the next session compiles them ahead on a spare core.

`TrSetup/` builds `trsetup.exe`, which the handheld runs during setup. It covers three steps from wild1walker's trpatch: it pulls out the libraries Terraria.exe carries, makes stand-ins for the Windows types .NET lacks, and writes the texture-size table and the uncompressed fonts.

`TileConverter/` holds the Cecil passes the patches' rules run, and `Passes.cs` runs them in a fixed order. They take effect when the game is patched. At that point `TERRARIA_SPEED=0` leaves out all the speed passes, and one of these set to `0` leaves out one of them: `TERRARIA_DRAWCUTS`, `TERRARIA_UI`, `TERRARIA_SCANS`, `TERRARIA_SPLIT`, `TERRARIA_INITS`, `TERRARIA_BOSSES`, `TERRARIA_TILECACHE`, `TERRARIA_FRAMES`, `TERRARIA_THROTTLE`, `TERRARIA_LIGHTS`, `TERRARIA_RESTS`, `TERRARIA_SETTLED`. `TERRARIA_QUIET=0` and `TERRARIA_JIT=0` leave out those two hooks. Each patch file lists its run-time switches (`TR_...=0`) and audits at its top.

A pass that checks the game's code before it changes it (Bosses, TileCache, Frames, Rests, Settled, the interface's cuts) takes its sums of the game as shipped, before MonoMod weaves: the weave rewrites the method bodies.

## Building

```bash
make Terraria
Terraria/make-port.sh
```

`make-port.sh` copies the build, MonoMod and Cecil, FNA, FNA3D, FAudio and gpuastc into the port folder. With `PACK=1` it also writes `terraria.zip`. The script lists what it needs at the top.

To try the game on a PC: `test-net.sh` builds and runs it under .NET the way the handheld does, and `test-pc.sh` runs it under Mono.
