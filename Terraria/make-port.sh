#!/bin/bash
# Fills the Terraria port repo (Terraria-PortMaster: Terraria.sh + terraria/, laid out as PortMaster ships it) with what is built
# here and elsewhere: our patches and the rules' helper, MonoMod, the reference assemblies the weave resolves against, our FNA /
# FNA3D / FAudio, gpuastc, trsetup. The scripts, settings and texts in the repo are edited there; this only refreshes binaries.
#   PORT         the port repo  (default ../Terraria-PortMaster next to FNAPatches)
#   REFDIR       Mono 4.5 reference assemblies  (default /usr/lib/mono/4.5)
#   GPUASTC      GPU-ASTC checkout with the aarch64 gpuastc in build-aarch64/  (default ../GPU-ASTC)
#   RUNTIME      the .NET runtime squashfs, carried in the port (runtime/) so nothing has to be fetched by hand
#                (default ../PortMaster-dotnet/out/dotnet-10.0.15-aarch64.squashfs, from its build.sh)
#   PACK=1       also writes terraria.zip next to the repo
# Run `make Terraria` first. The launcher prefers PortMaster's own copy of the runtime (PortMaster/libs) when there is one.
set -euo pipefail
FP=$(cd "$(dirname "$0")/.." && pwd); OURS=$FP/deps/Terraria
PORT=$(cd "${PORT:-$FP/../Terraria-PortMaster}" && pwd); T=$PORT/terraria
REFDIR=${REFDIR:-/usr/lib/mono/4.5}; GA=${GPUASTC:-$FP/../GPU-ASTC}
RT=${RUNTIME:-$FP/../PortMaster-dotnet/out/dotnet-10.0.15-aarch64.squashfs}
for f in "$OURS/FNA.dll" "$OURS/FNA.dll.config" "$OURS/arm64-sdl2-libs/libFNA3D.so.0.26.11" "$OURS/arm64-sdl2-libs/libFAudio.so.0.26.11" \
         "$RT" "$FP/out/Terraria/Terraria.TerrariaPatches.mm.dll" "$FP/out/Terraria/TileConverter.dll" "$FP/out/Terraria/weave.exe" "$GA/build-aarch64/gpuastc" "$FP/out/Terraria/trsetup.exe" "$PORT/Terraria.sh"; do
  [ -f "$f" ] || { echo "missing $f"; exit 1; }
done
rm -rf "$T/monomod" "$T/patches" "$T/libs" "$T/dlls" "$T/runtime" "$T/tools/trpatch.exe"
mkdir -p "$T/monomod/ref" "$T/patches" "$T/libs" "$T/dlls" "$T/runtime" "$T/tools" "$T/licenses"
# the .NET runtime, for handhelds whose PortMaster does not carry it
cp "$RT" "$T/runtime/dotnet-10.0.15-aarch64.squashfs"
# our patches and the helper their rules run
# (every patch assembly the build made: a new Terraria/*.mm.cs needs no line here)
cp "$FP"/out/Terraria/*.mm.dll "$T/patches/"
rm -f "$T"/patches/ReLogic.TileRecords.mm.dll
cp "$FP"/deps/MonoMod/{MonoMod.Common,MonoMod.Utils}.dll "$FP/deps/MonoMod/MonoMod.exe" "$FP/out/Terraria/TileConverter.dll" \
   "$FP/out/Terraria/weave.exe" "$T/monomod/"
for a in mscorlib System System.Core System.Xml System.Drawing System.Windows.Forms System.Runtime.Serialization WindowsBase \
         System.Configuration System.Data System.Numerics System.Security System.Transactions System.EnterpriseServices \
         System.Xml.Linq System.Xaml System.ServiceModel.Internals System.Runtime.Serialization.Formatters.Soap \
         Accessibility Microsoft.CSharp Mono.Posix Mono.Security Mono.WebBrowser; do
  cp "$REFDIR/$a.dll" "$T/monomod/ref/"
done
# trsetup (extract, stubs, content) and the Cecil it and MonoMod run on; licenses
cp "$FP/out/Terraria/trsetup.exe" "$T/tools/"
cp "$FP"/deps/Cecil/{Mono.Cecil.dll,Mono.Cecil.Mdb.dll,Mono.Cecil.Pdb.dll,Mono.Cecil.Rocks.dll} "$T/monomod/"
cp "$FP"/Terraria/licenses/* "$T/licenses/"
cp "$FP/../FNA3D/LICENSE" "$T/licenses/LICENSE.FNA3D.txt"; cp "$FP/../FAudio/LICENSE" "$T/licenses/LICENSE.FAudio.txt"
# our native libraries and FNA, gpuastc
# plain files (no symlinks: exFAT cards), named as .NET looks for them: FNA's DllImport "FNA3D" -> libFNA3D.so
for l in FNA3D FAudio; do cp "$OURS/arm64-sdl2-libs/lib$l.so.0.26.11" "$T/libs/lib$l.so"; done
cp "$OURS/FNA.dll" "$OURS/FNA.dll.config" "$T/dlls/"
cp "$GA/build-aarch64/gpuastc" "$T/tools/"
cp "$GA/THIRD_PARTY_NOTICES.md" "$T/licenses/gpuastc.THIRD_PARTY_NOTICES.md"
cp "$FP/LICENSE" "$T/licenses/fnapatches.LICENSE"
echo "port repo refreshed: $PORT"
if [ "${PACK:-0}" = 1 ]; then
  Z=$(cd "$PORT/.." && pwd)/terraria.zip; rm -f "$Z"
  (cd "$PORT" && zip -q -r -y "$Z" Terraria.sh terraria/gamedata/gitkeep.txt \
     terraria/{dlls,libs,licenses,monomod,patches,runtime,tools,terraria.ini})
  echo "built $Z ($(du -h "$Z" | cut -f1))"
fi
