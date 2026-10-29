#!/bin/sh
# Builds the browser (WebAssembly) version of OpenRA into OpenRA.Web/dist/.
#
# Requirements: the .NET 10 SDK with the wasm-tools workload on PATH (dotnet), python3, curl, git.
# Everything else (Lua 5.1.5, the OpenRA Eluant sources, emscripten's SDL2/FreeType ports) is
# downloaded into OpenRA.Web/obj/ on the first run.
#
# Environment:
#   CONFIGURATION       Release (default) or Debug
#   WEBRA_AOT           1 to build with Mono AOT (RunAOTCompilation), default 0
#   WEBRA_AOT_SERIAL    true (default) compiles assemblies one at a time; parallel AOT needs > 6 GB
#   WEBRA_VERSION       version string stamped into VERSION and mod.yaml
#                       (default: the nearest git tag, which must equal the server version)
#   WEBRA_MODS          mods to package (default: "ra")
#   WEBRA_CONTENT_DIR   optional local directory with the mod content (e.g. .../Content/ra/v2).
#                       It is packaged into dist/data for local testing only. Never publish it.
#   WEBRA_EM_CACHE      emscripten cache (default: OpenRA.Web/obj/emcache)
#
# Usage: ./build-web.sh            build dist/
#        ./build-web.sh serve      serve dist/ on http://127.0.0.1:8090/

set -o errexit || exit $?
set -o nounset

ROOT="$(cd "$(dirname "$0")" && pwd)"
WEB="${ROOT}/OpenRA.Web"
DIST="${WEB}/dist"
OBJ="${WEB}/obj/webra"
NATIVE="${OBJ}/native"
DEPS="${OBJ}/deps"

if [ "${1:-}" = "serve" ]; then
	exec python3 -I "${WEB}/tools/serve.py" "${DIST}" "${WEBRA_PORT:-8090}"
fi

CONFIGURATION="${CONFIGURATION:-Release}"
WEBRA_AOT="${WEBRA_AOT:-0}"
WEBRA_MODS="${WEBRA_MODS:-ra}"
WEBRA_EM_CACHE="${WEBRA_EM_CACHE:-${WEB}/obj/emcache}"
WEBRA_VERSION="${WEBRA_VERSION:-$(git -C "${ROOT}" describe --tags --abbrev=0 2>/dev/null || echo "{DEV_VERSION}")}"

LUA_VERSION="5.1.5"
LUA_SHA256="2640fc56a795f29d28ef15e13c34a47e223960b0240e8cb0a82d9b0738695333"
ELUANT_TAG="1.0.23" # must match the OpenRA-Eluant package referenced by OpenRA.Game

command -v dotnet > /dev/null || { echo "dotnet not found on PATH"; exit 1; }
DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"

# Use the emscripten that ships with the wasm-tools workload, so that native code and the .NET
# runtime are built by the same compiler.
EMSDK_TOOLS="$(ls -d "${DOTNET_ROOT}"/packs/Microsoft.NET.Runtime.Emscripten.*.Sdk.*/*/tools 2>/dev/null | sort | tail -n 1)"
EMSDK_CACHE_SEED="$(ls -d "${DOTNET_ROOT}"/packs/Microsoft.NET.Runtime.Emscripten.*.Cache.*/*/tools/emscripten/cache 2>/dev/null | sort | tail -n 1)"
EMSDK_NODE="$(ls "${DOTNET_ROOT}"/packs/Microsoft.NET.Runtime.Emscripten.*.Node.*/*/tools/bin/node 2>/dev/null | sort | tail -n 1)"
if [ -z "${EMSDK_TOOLS}" ] || [ -z "${EMSDK_CACHE_SEED}" ] || [ -z "${EMSDK_NODE}" ]; then
	echo "The wasm-tools workload was not found under ${DOTNET_ROOT}. Run: dotnet workload install wasm-tools"
	exit 1
fi

export DOTNET_EMSCRIPTEN_LLVM_ROOT="${EMSDK_TOOLS}/bin"
export DOTNET_EMSCRIPTEN_BINARYEN_ROOT="${EMSDK_TOOLS}"
export DOTNET_EMSCRIPTEN_NODE_JS="${EMSDK_NODE}"
export EM_CACHE="${WEBRA_EM_CACHE}"
export EM_FROZEN_CACHE=0
# The workload's cache was generated on the build machine of the workload; skip the sanity check
# that would otherwise wipe our (seeded) copy because of the different LLVM path.
export EM_IGNORE_SANITY=1
EMCC="${EMSDK_TOOLS}/emscripten/emcc"
EMAR="${EMSDK_TOOLS}/emscripten/emar"

# Do not leave MSBuild/compiler server processes running after the build.
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
trap 'dotnet build-server shutdown > /dev/null 2>&1 || true' EXIT

mkdir -p "${OBJ}" "${NATIVE}" "${DEPS}"

# The emscripten cache must be writable to add the SDL2 and FreeType ports.
if [ ! -d "${EM_CACHE}/sysroot" ]; then
	echo "Seeding emscripten cache ${EM_CACHE}"
	mkdir -p "$(dirname "${EM_CACHE}")"
	cp -a "${EMSDK_CACHE_SEED}" "${EM_CACHE}"
fi

# Flags shared by every native object linked into the app. -fwasm-exceptions matches the .NET
# runtime (wasm exception handling); Lua's setjmp/longjmp error handling then uses it too.
NATIVE_CFLAGS="-O2 -fwasm-exceptions -sSUPPORT_LONGJMP=wasm"

echo "== Lua ${LUA_VERSION}"
if [ ! -f "${NATIVE}/lua51.a" ]; then
	LUA_TAR="${DEPS}/lua-${LUA_VERSION}.tar.gz"
	[ -f "${LUA_TAR}" ] || curl -fsSL -o "${LUA_TAR}" "https://www.lua.org/ftp/lua-${LUA_VERSION}.tar.gz"
	echo "${LUA_SHA256}  ${LUA_TAR}" | sha256sum -c -
	rm -rf "${DEPS}/lua-${LUA_VERSION}" "${OBJ}/lua-obj"
	tar -xzf "${LUA_TAR}" -C "${DEPS}"
	mkdir -p "${OBJ}/lua-obj"
	for src in "${DEPS}/lua-${LUA_VERSION}"/src/*.c; do
		case "$(basename "${src}")" in
			lua.c|luac.c|print.c) continue ;;
		esac
		"${EMCC}" ${NATIVE_CFLAGS} -c "${src}" -o "${OBJ}/lua-obj/$(basename "${src}" .c).o"
	done
	"${EMAR}" rcs "${NATIVE}/lua51.a" "${OBJ}"/lua-obj/*.o
fi

echo "== Native glue"
"${EMCC}" ${NATIVE_CFLAGS} -c "${WEB}/native/webra_gl.c" -o "${OBJ}/webra_gl.o"
"${EMCC}" ${NATIVE_CFLAGS} -c "${WEB}/native/wasm_sjlj_compat.c" -o "${OBJ}/wasm_sjlj_compat.o"
rm -f "${NATIVE}/webra.a"
"${EMAR}" rcs "${NATIVE}/webra.a" "${OBJ}/webra_gl.o" "${OBJ}/wasm_sjlj_compat.o"
for module in SDL2 freetype6 soft_oal; do
	"${EMCC}" ${NATIVE_CFLAGS} "-DWEBRA_STUB_SYMBOL=webra_module_stub_${module}" -c "${WEB}/native/module_stub.c" -o "${OBJ}/stub_${module}.o"
	rm -f "${NATIVE}/${module}.a"
	"${EMAR}" rcs "${NATIVE}/${module}.a" "${OBJ}/stub_${module}.o"
done

# FreeTypeFont.cs reads FreeType structs through hardcoded 32-bit offsets: check them against
# the FreeType that emscripten links (this also fetches the port into the cache).
"${EMCC}" -sUSE_FREETYPE=1 -fsyntax-only "${WEB}/native/freetype_layout_check.c"

echo "== Eluant ${ELUANT_TAG} with WebAssembly callback markers"
ELUANT_SRC="${DEPS}/Eluant"
if [ ! -d "${ELUANT_SRC}" ]; then
	git clone --quiet --depth 1 --branch "${ELUANT_TAG}" https://github.com/OpenRA/Eluant.git "${ELUANT_SRC}"
fi
[ -n "${WEBRA_KEEP_ELUANT_EDITS:-}" ] || git -C "${ELUANT_SRC}" checkout --quiet -- .
cp "${WEB}/native/eluant/MonoPInvokeCallbackAttribute.cs" "${ELUANT_SRC}/Eluant/"
# Eluant declares lua_insert as returning int, but the C function returns void. Native platforms do
# not care; WebAssembly traps on the mismatched call ("function signature mismatch"). The return
# value is never used. Checked by OpenRA.Web/tools/check-pinvoke-signatures.py.
sed -i 's/public static extern int lua_insert(IntPtr L, int index);/public static extern void lua_insert(IntPtr L, int index);/' "${ELUANT_SRC}/Eluant/LuaApi.cs"
grep -q "extern void lua_insert" "${ELUANT_SRC}/Eluant/LuaApi.cs" || { echo "Failed to patch Eluant lua_insert"; exit 1; }
# The Eluant sources sit inside this repo tree: keep OpenRA's Directory.Build.props out of them.
dotnet build "${ELUANT_SRC}/Eluant/Eluant.csproj" -c Release -nologo -v quiet -p:ImportDirectoryBuildProps=false \
	-p:DefineConstants=MONOTOUCH -p:OutputPath="${OBJ}/eluant/"

echo "== GL delegate signatures"
python3 -I "${WEB}/tools/gen-gl-signatures.py" "${ROOT}/OpenRA.Platforms.Default/OpenGL.cs" "${OBJ}/GLSignatures.g.cs"

echo "== dotnet publish (${CONFIGURATION}, AOT=${WEBRA_AOT})"
RUN_AOT=false
LINK_OPT="-O2"
if [ "${WEBRA_AOT}" = "1" ]; then
	RUN_AOT=true
	# emcc's -O2 link step runs wasm-opt over the whole AOT module, which needs more than 6 GB.
	LINK_OPT="${WEBRA_AOT_LINK_OPT:--O1}"
fi
PUBLISH="${WEB}/bin/${CONFIGURATION}/publish"
rm -rf "${PUBLISH}"
dotnet publish "${WEB}/OpenRA.Web.csproj" -c "${CONFIGURATION}" -nologo \
	-p:WebraNativeDir="${NATIVE}/" \
	-p:WebraEluantPath="${OBJ}/eluant/Eluant.dll" \
	-p:WebraGeneratedDir="${OBJ}/" \
	-p:WebraEmCache="${EM_CACHE}/" \
	-p:RunAOTCompilation="${RUN_AOT}" \
	-p:DisableParallelAot="${WEBRA_AOT_SERIAL:-true}" \
	-p:EmccLinkOptimizationFlag="${LINK_OPT}" \
	-p:PublishDir="${PUBLISH}/"

echo "== Checking generated P/Invoke prototypes against the C headers"
PINVOKE_TABLE="${WEB}/obj/${CONFIGURATION}/wasm/for-publish/pinvoke-table.h"
INCLUDE="${EM_CACHE}/sysroot/include"
LUA_SRC="${DEPS}/lua-${LUA_VERSION}/src"
python3 -I "${WEB}/tools/check-pinvoke-signatures.py" "${PINVOKE_TABLE}" lua51 "${LUA_SRC}/lua.h" "${LUA_SRC}/lauxlib.h" "${LUA_SRC}/lualib.h"
python3 -I "${WEB}/tools/check-pinvoke-signatures.py" "${PINVOKE_TABLE}" soft_oal "${INCLUDE}/AL/al.h" "${INCLUDE}/AL/alc.h"
python3 -I "${WEB}/tools/check-pinvoke-signatures.py" "${PINVOKE_TABLE}" freetype6 "$(find "${EM_CACHE}" -name freetype.h | head -n 1)"

echo "== Staging ${DIST}"
rm -rf "${DIST}"
mkdir -p "${DIST}/data"
cp -a "${PUBLISH}/wwwroot/." "${DIST}/"

ENGINE_STAGE="${OBJ}/stage/engine"
rm -rf "${ENGINE_STAGE}"
mkdir -p "${ENGINE_STAGE}/mods"
cp -a "${ROOT}/glsl" "${ROOT}/global mix database.dat" "${ENGINE_STAGE}/"
cp -a "${ROOT}/mods/common" "${ENGINE_STAGE}/mods/"
for mod in ${WEBRA_MODS}; do
	cp -a "${ROOT}/mods/${mod}" "${ENGINE_STAGE}/mods/"
	[ -d "${ROOT}/mods/${mod}-content" ] && cp -a "${ROOT}/mods/${mod}-content" "${ENGINE_STAGE}/mods/"
done
[ -d "${ROOT}/mods/common-content" ] && cp -a "${ROOT}/mods/common-content" "${ENGINE_STAGE}/mods/"
(
	set +o nounset # packaging/functions.sh relies on unset positional parameters
	. "${ROOT}/packaging/functions.sh"
	set_engine_version "${WEBRA_VERSION}" "${ENGINE_STAGE}"
	set_mod_version "${WEBRA_VERSION}" "${ENGINE_STAGE}"/mods/*/mod.yaml
)
python3 -I "${WEB}/tools/pack-data.py" "${ENGINE_STAGE}" /openra "${DIST}/data/engine"

if [ -n "${WEBRA_CONTENT_DIR:-}" ]; then
	# Local testing only: this is copyrighted (freeware) game content. Do not publish it.
	python3 -I "${WEB}/tools/pack-data.py" "${WEBRA_CONTENT_DIR}" /support/Content/ra/v2 "${DIST}/data/content"
fi

cat > "${DIST}/data/build.json" <<EOF
{"version":"${WEBRA_VERSION}","configuration":"${CONFIGURATION}","aot":${RUN_AOT},"mods":"${WEBRA_MODS}","content":$([ -n "${WEBRA_CONTENT_DIR:-}" ] && echo true || echo false)}
EOF

echo "Built ${WEBRA_VERSION} into ${DIST}"
du -sh "${DIST}"
