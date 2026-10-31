#!/bin/sh
# Builds the browser (WebAssembly) version of OpenRA into OpenRA.Web/dist/.
#
# Requirements: the .NET SDK and wasm-tools workload versions pinned below (dotnet on PATH),
# python3, curl, git, patch. Everything else (Lua 5.1.5, the OpenRA Eluant sources, emscripten's
# SDL2/FreeType ports) is downloaded into OpenRA.Web/obj/ on the first run and verified.
#
# Two kinds of build:
#
#   official (default)  Stamps the upstream release version (UPSTREAM_TAG below) into VERSION and
#                       every mod.yaml. Game servers admit a client by its mod and version string
#                       alone, so this build refuses to run unless the working tree is clean, HEAD
#                       is based on UPSTREAM_TAG (merges included: every commit since the tag descends
#                       from it), the mod data equals the tag and every other changed upstream file
#                       is listed in OpenRA.Web/upstream-patches.txt. It compiles and
#                       packages a "git archive HEAD" export, never the working tree. The page ignores
#                       debug URL parameters.
#   dev (WEBRA_DEV=1)   Builds the working tree as it is and stamps {DEV_VERSION}, which no release
#                       server accepts. The page honours the debug URL parameters (?arg=, ?env=, ?rt=,
#                       ?interp=, ?luatest=).
#
# Environment:
#   CONFIGURATION       Release (default) or Debug
#   WEBRA_DEV           1 for a dev build (see above)
#   WEBRA_AOT           1 to build with Mono AOT (RunAOTCompilation), default 0
#   WEBRA_AOT_SERIAL    true (default) compiles assemblies one at a time; parallel AOT needs > 6 GB
#   WEBRA_MODS          mods to package (default: "ra")
#   WEBRA_CONTENT_DIR   optional local directory with the mod content (e.g. .../Content/ra/v2).
#                       It is packaged into dist/data for local testing only. Never publish it.
#   WEBRA_EM_CACHE      emscripten cache (default: OpenRA.Web/obj/emcache)
#   WEBRA_PROXY_URL     WebSocket-to-TCP relay the page uses for game connections, written to
#                       data/build.json: an absolute ws:// or wss:// URL, or a path resolved against the
#                       page's own address (ws: for http pages, wss: for https). Default "ws", i.e. the
#                       relay at <page directory>/ws on the same host (deploy/ kit). Dev builds also take ?proxy=
#   WEBRA_CONTENT_RELAY Path of the relay's content route (official mirror downloads), default "content"
#   WEBRA_CONTENT_URL   Where the page downloads the game content instead of the relay: a path on the page's
#                       own site, relative to the build's directory (e.g. "content": <base>/content/
#                       ra-quickinstall.<sha1>.zip). The files are not part of the build: tools/fetch-content.py
#                       writes them once, verified, and the publish manifest lists them. Default: empty (relay)
#   WEBRA_MASTER_RELAY  The relay's master server route (versioncheck, gamenews): a path, default "master",
#                       or an https:// URL on another host (the relay must allow the page's origin, -master-cors);
#                       "none" leaves those requests going to master.openra.net (CORS-blocked)
#   WEBRA_STORAGE_NAME  IndexedDB database name; default "webra" at the root of a site, "webra:<path>" under a path
#   WEBRA_META_CSP      1 writes the Content-Security-Policy into index.html as <meta http-equiv> too, for hosts
#                       that cannot be relied on to send the header (object storage behind a CDN)
#   WEBRA_PAGE_TITLE    the page's <title>, e.g. the site's name for a hosting target; default "OpenRA"
#   WEBRA_SPLASH        a start screen, shown before the game starts: the name of OpenRA.Web/hosting/<name>-splash.html
#                       (markup put into index.html), with its logo OpenRA.Web/hosting/<name>-logo.svg or .png if
#                       there is one. Default: none, the page starts the game at once
#   WEBRA_SOURCE_URL    the repository the start screen's "source code" link points into, default
#                       https://github.com/lanhouse-playra/game: the link is <url>/tree/<commit>, the exact source of the build
#   WEBRA_PUBLISH_ENCODING  br (default) or identity: the encoding of the publish manifest's objects
#                       (OpenRA.Web/publish.json, tools/publish.py), for hosts without content negotiation
#   WEBRA_TARGET        a hosting target: reads OpenRA.Web/hosting/<target>.env (these variables, for one
#                       site; variables already set in the environment win), e.g. WEBRA_TARGET=lanhouse
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

fail()
{
	echo "build-web.sh: $*" >&2
	exit 1
}

# The upstream release this branch is based on. Official builds carry exactly this version string.
UPSTREAM_TAG="playtest-20261026"

# Pinned toolchain. The Mono runtime is part of the simulation's determinism (numeric conversions,
# the interpreter bug worked around in main.js with MONO_INTERPRETER_OPTIONS=-tiering), so a different
# SDK, workload or runtime fails the build. Changing these requires rerunning the numeric conformance
# corpus (OpenRA.Web/tools/conformance.sh) on interpreter and AOT builds and the boot self-test.
DOTNET_SDK_VERSION="10.0.403"        # also OpenRA.Web/global.json
DOTNET_RUNTIME_VERSION="10.0.14"     # Microsoft.NETCore.App.Runtime.Mono.browser-wasm and the workload packs
WASM_WORKLOAD_MANIFEST="10.0.114"    # microsoft.net.workload.mono.toolchain.current
EMSCRIPTEN_VERSION="3.1.56"

LUA_VERSION="5.1.5"
LUA_SHA256="2640fc56a795f29d28ef15e13c34a47e223960b0240e8cb0a82d9b0738695333"

CONFIGURATION="${CONFIGURATION:-Release}"
WEBRA_DEV="${WEBRA_DEV:-0}"
WEBRA_AOT="${WEBRA_AOT:-0}"
WEBRA_MODS="${WEBRA_MODS:-ra}"
WEBRA_EM_CACHE="${WEBRA_EM_CACHE:-${WEB}/obj/emcache}"

[ -z "${WEBRA_VERSION:-}" ] || fail "WEBRA_VERSION is not supported: official builds stamp ${UPSTREAM_TAG}, dev builds (WEBRA_DEV=1) {DEV_VERSION}."

if [ -n "${WEBRA_TARGET:-}" ]; then
	printf '%s' "${WEBRA_TARGET}" | grep -Eq '^[a-z0-9-]{1,32}$' || fail "WEBRA_TARGET must be a plain name: ${WEBRA_TARGET}"
	TARGET_ENV="${WEB}/hosting/${WEBRA_TARGET}.env"
	[ -f "${TARGET_ENV}" ] || fail "no hosting target ${TARGET_ENV}"
	# KEY=value lines; a variable already set in the environment is kept.
	while IFS= read -r line || [ -n "${line}" ]; do
		case "${line}" in ''|'#'*) continue ;; esac
		key="${line%%=*}"
		value="${line#*=}"
		printf '%s' "${key}" | grep -Eq '^WEBRA_[A-Z_]+$' || fail "${TARGET_ENV}: not a WEBRA_ variable: ${line}"
		[ "${key}" != "WEBRA_TARGET" ] || fail "${TARGET_ENV}: cannot set WEBRA_TARGET"
		if eval "[ -z \"\${${key}+set}\" ]"; then
			export "${key}=${value}"
			echo "${WEBRA_TARGET}: ${key}=${value}"
		fi
	done < "${TARGET_ENV}"
fi

mkdir -p "${OBJ}" "${NATIVE}" "${DEPS}"

echo "== Sources"
COMMIT="$(git -C "${ROOT}" rev-parse HEAD 2> /dev/null || echo unknown)"
if [ "${WEBRA_DEV}" = "1" ]; then
	VERSION="{DEV_VERSION}"
	SRC="${ROOT}"
	[ -z "$(git -C "${ROOT}" status --porcelain 2> /dev/null)" ] || COMMIT="${COMMIT}-dirty"
	echo "Dev build of the working tree (${COMMIT}), version ${VERSION}"
else
	git -C "${ROOT}" rev-parse --git-dir > /dev/null 2>&1 || fail "official builds need a git checkout (or use WEBRA_DEV=1)"
	DIRTY="$(git -C "${ROOT}" status --porcelain --untracked-files=normal)"
	[ -z "${DIRTY}" ] || fail "the working tree has uncommitted or untracked changes; an official build must match a commit (commit them, or use WEBRA_DEV=1):
${DIRTY}"
	git -C "${ROOT}" rev-parse -q --verify "refs/tags/${UPSTREAM_TAG}^{commit}" > /dev/null || fail "tag ${UPSTREAM_TAG} not found (git fetch --tags upstream)"
	git -C "${ROOT}" merge-base --is-ancestor "${UPSTREAM_TAG}" HEAD || fail "HEAD is not based on ${UPSTREAM_TAG}"
	# A merge can bring in history that does not start at the tag (another release, a branch cut before the tag):
	# every commit since the tag must descend from it. The checks below compare trees, so they hold for merges.
	git -C "${ROOT}" rev-list --ancestry-path "${UPSTREAM_TAG}..HEAD" > "${OBJ}/ancestry.list"
	FOREIGN="$(git -C "${ROOT}" rev-list "${UPSTREAM_TAG}..HEAD" | grep -vxF -f "${OBJ}/ancestry.list" || true)"
	[ -z "${FOREIGN}" ] || fail "HEAD has $(echo "${FOREIGN}" | wc -l) commits since ${UPSTREAM_TAG} that do not descend from it (merged from another base), for example:
$(echo "${FOREIGN}" | head -5 | xargs git -C "${ROOT}" log --no-walk --oneline)"
	git -C "${ROOT}" diff --quiet "${UPSTREAM_TAG}" HEAD -- mods glsl "global mix database.dat" \
		|| fail "mod data differs from ${UPSTREAM_TAG}; servers would accept a client running other rules:
$(git -C "${ROOT}" diff --stat "${UPSTREAM_TAG}" HEAD -- mods glsl "global mix database.dat")"
	grep -v '^#' "${WEB}/upstream-patches.txt" | sed '/^[[:space:]]*$/d' > "${OBJ}/upstream-patches.list"
	UNEXPECTED="$(git -C "${ROOT}" diff --name-only "${UPSTREAM_TAG}" HEAD | grep -v '^OpenRA\.Web/' | grep -vxF -f "${OBJ}/upstream-patches.list" || true)"
	[ -z "${UNEXPECTED}" ] || fail "files changed since ${UPSTREAM_TAG} that are not in OpenRA.Web/upstream-patches.txt:
${UNEXPECTED}"

	VERSION="${UPSTREAM_TAG}"
	SRC="${OBJ}/src"
	rm -rf "${SRC}"
	mkdir -p "${SRC}"
	git -C "${ROOT}" archive --format=tar HEAD | tar -x -C "${SRC}"
	echo "Official build of ${COMMIT} (git archive), version ${VERSION}"
fi
SRC_WEB="${SRC}/OpenRA.Web"

echo "== Toolchain"
command -v dotnet > /dev/null || fail "dotnet not found on PATH"
DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
# OpenRA.Web/global.json pins the SDK; it applies to dotnet commands run from that directory.
cd "${SRC_WEB}"
SDK="$(dotnet --version 2> /dev/null || true)"
[ "${SDK}" = "${DOTNET_SDK_VERSION}" ] || fail "the .NET SDK ${DOTNET_SDK_VERSION} pinned by OpenRA.Web/global.json is required (got '${SDK}')"
[ -d "${DOTNET_ROOT}/packs/Microsoft.NETCore.App.Runtime.Mono.browser-wasm/${DOTNET_RUNTIME_VERSION}" ] \
	|| fail "Mono browser-wasm runtime pack ${DOTNET_RUNTIME_VERSION} not found under ${DOTNET_ROOT}/packs (dotnet workload install wasm-tools)"
ls -d "${DOTNET_ROOT}"/sdk-manifests/*/microsoft.net.workload.mono.toolchain.current/"${WASM_WORKLOAD_MANIFEST}" > /dev/null 2>&1 \
	|| fail "wasm-tools workload manifest ${WASM_WORKLOAD_MANIFEST} not installed"

# Use the emscripten that ships with the wasm-tools workload, so that native code and the .NET
# runtime are built by the same compiler.
EMSDK_PACKS="${DOTNET_ROOT}/packs/Microsoft.NET.Runtime.Emscripten.${EMSCRIPTEN_VERSION}"
EMSDK_TOOLS="${EMSDK_PACKS}.Sdk.linux-x64/${DOTNET_RUNTIME_VERSION}/tools"
EMSDK_CACHE_SEED="${EMSDK_PACKS}.Cache.linux-x64/${DOTNET_RUNTIME_VERSION}/tools/emscripten/cache"
EMSDK_NODE="${EMSDK_PACKS}.Node.linux-x64/${DOTNET_RUNTIME_VERSION}/tools/bin/node"
for p in "${EMSDK_TOOLS}" "${EMSDK_CACHE_SEED}" "${EMSDK_NODE}"; do
	[ -e "${p}" ] || fail "${p} not found: the wasm-tools workload for runtime ${DOTNET_RUNTIME_VERSION} (emscripten ${EMSCRIPTEN_VERSION}) is required"
done

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
"${EMCC}" --version | head -n 1 | grep -q " ${EMSCRIPTEN_VERSION} " || fail "emcc is not emscripten ${EMSCRIPTEN_VERSION}"
echo "SDK ${SDK}, runtime ${DOTNET_RUNTIME_VERSION}, workload ${WASM_WORKLOAD_MANIFEST}, emscripten ${EMSCRIPTEN_VERSION}"

# Do not leave MSBuild/compiler server processes running after the build.
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
trap 'dotnet build-server shutdown > /dev/null 2>&1 || true' EXIT

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
# lua-5.1.5-webra.patch makes the wasm32 build behave like desktop x64 Lua: 64-bit lua_Integer,
# 64-bit string positions and string.format integers, 64-bit tonumber(s, base).
LUA_PATCH="${SRC_WEB}/native/lua/lua-${LUA_VERSION}-webra.patch"
LUA_STAMP="$( (cat "${LUA_PATCH}"; echo "${LUA_SHA256} ${NATIVE_CFLAGS} ${EMSCRIPTEN_VERSION}") | sha256sum | cut -d ' ' -f 1)"
if [ ! -f "${NATIVE}/lua51.a" ] || [ "$(cat "${NATIVE}/lua51.stamp" 2> /dev/null)" != "${LUA_STAMP}" ]; then
	LUA_TAR="${DEPS}/lua-${LUA_VERSION}.tar.gz"
	[ -f "${LUA_TAR}" ] || curl -fsSL -o "${LUA_TAR}" "https://www.lua.org/ftp/lua-${LUA_VERSION}.tar.gz"
	echo "${LUA_SHA256}  ${LUA_TAR}" | sha256sum -c -
	rm -rf "${DEPS}/lua-${LUA_VERSION}" "${OBJ}/lua-obj" "${NATIVE}/lua51.a" "${NATIVE}/lua51.stamp"
	tar -xzf "${LUA_TAR}" -C "${DEPS}"
	patch -p1 -s -d "${DEPS}/lua-${LUA_VERSION}" < "${LUA_PATCH}"
	mkdir -p "${OBJ}/lua-obj"
	for src in "${DEPS}/lua-${LUA_VERSION}"/src/*.c; do
		case "$(basename "${src}")" in
			lua.c|luac.c|print.c) continue ;;
		esac
		"${EMCC}" ${NATIVE_CFLAGS} -c "${src}" -o "${OBJ}/lua-obj/$(basename "${src}" .c).o"
	done
	"${EMAR}" rcs "${NATIVE}/lua51.a" "${OBJ}"/lua-obj/*.o
	echo "${LUA_STAMP}" > "${NATIVE}/lua51.stamp"
fi

echo "== Native glue"
# -msimd128: the GL shim's BGRA conversion uses WebAssembly SIMD (the runtime is built with SIMD too).
"${EMCC}" ${NATIVE_CFLAGS} -msimd128 -c "${SRC_WEB}/native/webra_gl.c" -o "${OBJ}/webra_gl.o"
"${EMCC}" ${NATIVE_CFLAGS} -c "${SRC_WEB}/native/wasm_sjlj_compat.c" -o "${OBJ}/wasm_sjlj_compat.o"
rm -f "${NATIVE}/webra.a"
"${EMAR}" rcs "${NATIVE}/webra.a" "${OBJ}/webra_gl.o" "${OBJ}/wasm_sjlj_compat.o"
for module in SDL2 freetype6 soft_oal; do
	"${EMCC}" ${NATIVE_CFLAGS} "-DWEBRA_STUB_SYMBOL=webra_module_stub_${module}" -c "${SRC_WEB}/native/module_stub.c" -o "${OBJ}/stub_${module}.o"
	rm -f "${NATIVE}/${module}.a"
	"${EMAR}" rcs "${NATIVE}/${module}.a" "${OBJ}/stub_${module}.o"
done

# FreeTypeFont.cs reads FreeType structs through hardcoded 32-bit offsets: check them against
# the FreeType that emscripten links (this also fetches the port into the cache).
"${EMCC}" -sUSE_FREETYPE=1 -fsyntax-only "${SRC_WEB}/native/freetype_layout_check.c"

# Eluant: the exact source of the OpenRA-Eluant package that OpenRA.Game references, by commit.
ELUANT_VERSION="$(sed -n 's/.*PackageReference Include="OpenRA-Eluant" Version="\([^"]*\)".*/\1/p' "${SRC}/OpenRA.Game/OpenRA.Game.csproj")"
case "${ELUANT_VERSION}" in
	1.0.23) ELUANT_COMMIT="cbaaad548d81c0f5739c6a03b10b01e807029e92" ;;
	*) fail "OpenRA.Game references OpenRA-Eluant '${ELUANT_VERSION}': add its source commit to build-web.sh (tag ${ELUANT_VERSION} of https://github.com/OpenRA/Eluant)" ;;
esac
echo "== Eluant ${ELUANT_VERSION} (${ELUANT_COMMIT}) with WebAssembly callback markers"
ELUANT_SRC="${DEPS}/Eluant"
if [ "$(git -C "${ELUANT_SRC}" rev-parse HEAD 2> /dev/null)" != "${ELUANT_COMMIT}" ]; then
	rm -rf "${ELUANT_SRC}"
	git init -q "${ELUANT_SRC}"
	git -C "${ELUANT_SRC}" fetch -q --depth 1 https://github.com/OpenRA/Eluant.git "${ELUANT_COMMIT}"
	git -C "${ELUANT_SRC}" checkout -q --detach FETCH_HEAD
fi
[ "$(git -C "${ELUANT_SRC}" rev-parse HEAD)" = "${ELUANT_COMMIT}" ] || fail "Eluant checkout is not ${ELUANT_COMMIT}"
if [ "${WEBRA_DEV}" = "1" ] && [ -n "${WEBRA_KEEP_ELUANT_EDITS:-}" ]; then
	echo "Keeping local Eluant edits (dev build)"
else
	git -C "${ELUANT_SRC}" checkout -q -- .
	git -C "${ELUANT_SRC}" clean -q -fdx
fi
cp "${SRC_WEB}/native/eluant/MonoPInvokeCallbackAttribute.cs" "${ELUANT_SRC}/Eluant/"
# Eluant declares lua_insert as returning int, but the C function returns void. Native platforms do
# not care; WebAssembly traps on the mismatched call ("function signature mismatch"). The return
# value is never used. Checked by OpenRA.Web/tools/check-pinvoke-signatures.py.
sed -i 's/public static extern int lua_insert(IntPtr L, int index);/public static extern void lua_insert(IntPtr L, int index);/' "${ELUANT_SRC}/Eluant/LuaApi.cs"
grep -q "extern void lua_insert" "${ELUANT_SRC}/Eluant/LuaApi.cs" || fail "failed to patch Eluant lua_insert"
# The Eluant sources sit inside this repo tree: keep OpenRA's Directory.Build.props out of them.
dotnet build "${ELUANT_SRC}/Eluant/Eluant.csproj" -c Release -nologo -v quiet -p:ImportDirectoryBuildProps=false \
	-p:DefineConstants=MONOTOUCH -p:OutputPath="${OBJ}/eluant/"

echo "== Generated sources"
python3 -I "${SRC_WEB}/tools/gen-gl-signatures.py" "${SRC}/OpenRA.Platforms.Default/OpenGL.cs" "${OBJ}/GLSignatures.g.cs"
DEV_BOOL=false
[ "${WEBRA_DEV}" = "1" ] && DEV_BOOL=true
cat > "${OBJ}/WebraBuildInfo.g.cs" << EOF
// Generated by build-web.sh.
namespace OpenRA.Web
{
	static class WebraBuildInfo
	{
		public const string Version = "${VERSION}";
		public const string Commit = "${COMMIT}";
		public const string RuntimeVersion = "${DOTNET_RUNTIME_VERSION}";
		public static readonly bool Dev = ${DEV_BOOL};
	}
}
EOF

echo "== dotnet publish (${CONFIGURATION}, AOT=${WEBRA_AOT})"
RUN_AOT=false
LINK_OPT="-O2"
if [ "${WEBRA_AOT}" = "1" ]; then
	RUN_AOT=true
	# emcc's -O2 link step runs wasm-opt over the whole AOT module, which needs more than 6 GB.
	LINK_OPT="${WEBRA_AOT_LINK_OPT:--O1}"
fi
PUBLISH="${SRC_WEB}/bin/${CONFIGURATION}/publish"
rm -rf "${PUBLISH}"
dotnet publish "${SRC_WEB}/OpenRA.Web.csproj" -c "${CONFIGURATION}" -nologo \
	-p:WebraNativeDir="${NATIVE}/" \
	-p:WebraEluantPath="${OBJ}/eluant/Eluant.dll" \
	-p:WebraGeneratedDir="${OBJ}/" \
	-p:WebraEmCache="${EM_CACHE}/" \
	-p:RunAOTCompilation="${RUN_AOT}" \
	-p:DisableParallelAot="${WEBRA_AOT_SERIAL:-true}" \
	-p:EmccLinkOptimizationFlag="${LINK_OPT}" \
	-p:PublishDir="${PUBLISH}/"

echo "== Checking the build"
FRAMEWORK="${PUBLISH}/wwwroot/_framework"
grep -q "Microsoft.NETCore.App.Runtime.Mono.browser-wasm/${DOTNET_RUNTIME_VERSION}/" "${FRAMEWORK}"/dotnet.native.*.js \
	|| fail "the published runtime is not Microsoft.NETCore.App.Runtime.Mono.browser-wasm ${DOTNET_RUNTIME_VERSION}"

# Every DllImport must call its C function with the exact WebAssembly signature, or it traps.
PINVOKE_TABLE="${SRC_WEB}/obj/${CONFIGURATION}/wasm/for-publish/pinvoke-table.h"
CHECK="python3 -I ${SRC_WEB}/tools/check-pinvoke-signatures.py --table ${PINVOKE_TABLE} --emcc ${EMCC} --allowlist ${SRC_WEB}/tools/pinvoke-allowlist.txt"
LUA_SRC="${DEPS}/lua-${LUA_VERSION}/src"
${CHECK} --module lua51 "\"${LUA_SRC}/lua.h\"" "\"${LUA_SRC}/lauxlib.h\"" "\"${LUA_SRC}/lualib.h\""
${CHECK} --module soft_oal "<AL/al.h>" "<AL/alc.h>"
${CHECK} --module freetype6 --cflag=-sUSE_FREETYPE=1 "<ft2build.h>" "FT_FREETYPE_H"
${CHECK} --module SDL2 --cflag=-sUSE_SDL=2 "<SDL2/SDL.h>" "<SDL2/SDL_vulkan.h>" "<SDL2/SDL_syswm.h>" | grep -v "not declared in the headers"

# Lua's number-to-integer conversions match desktop x64 only while clang lowers them to guarded
# i32/i64.trunc_f64_s (INT_MIN out of range, like cvttsd2si). The saturating forms (nontrapping-fptoint)
# would give INT_MAX instead. SIMD forms (i32x4.trunc_sat_*) are Mono's vector intrinsics, not ours.
NATIVE_WASM="$(ls "${FRAMEWORK}"/dotnet.native.*.wasm)"
SATURATING="$("${EMSDK_TOOLS}/bin/llvm-objdump" -d "${NATIVE_WASM}" | grep -cE '[[:space:]]i(32|64)\.trunc_sat_f(32|64)_[su]' || true)"
[ "${SATURATING}" = "0" ] || fail "${NATIVE_WASM} contains ${SATURATING} saturating float-to-int conversions; Lua would no longer match desktop"

echo "== Staging ${DIST}"
rm -rf "${DIST}"
mkdir -p "${DIST}/data"
cp -a "${PUBLISH}/wwwroot/." "${DIST}/"
# The page icons (OpenRA.Web/icons), the Red Alert icon of packaging/artwork: favicon.svg is ra_scalable.svg without
# the editor's metadata, favicon-32.png and favicon.ico (16, 32, 48) are the PNGs drawn for those sizes, and
# apple-touch-icon.png is a 180 px render on a dark grey tile. favicon.ico sits at the root of the build because
# browsers ask for /favicon.ico on their own; the others are named by index.html. Fixed names: tools/publish.py
# gives them a one-day Cache-Control.
cp "${SRC_WEB}/icons/favicon.svg" "${SRC_WEB}/icons/favicon-32.png" "${SRC_WEB}/icons/favicon.ico" \
	"${SRC_WEB}/icons/apple-touch-icon.png" "${DIST}/"

ENGINE_STAGE="${OBJ}/stage/engine"
rm -rf "${ENGINE_STAGE}"
mkdir -p "${ENGINE_STAGE}/mods"
# AUTHORS is the engine tab of Extras > Credits (ModCredits.EngineCreditsFile).
cp -a "${SRC}/glsl" "${SRC}/global mix database.dat" "${SRC}/AUTHORS" "${ENGINE_STAGE}/"
cp -a "${SRC}/mods/common" "${ENGINE_STAGE}/mods/"
for mod in ${WEBRA_MODS}; do
	cp -a "${SRC}/mods/${mod}" "${ENGINE_STAGE}/mods/"
	[ -d "${SRC}/mods/${mod}-content" ] && cp -a "${SRC}/mods/${mod}-content" "${ENGINE_STAGE}/mods/"
done
[ -d "${SRC}/mods/common-content" ] && cp -a "${SRC}/mods/common-content" "${ENGINE_STAGE}/mods/"
(
	set +o nounset # packaging/functions.sh relies on unset positional parameters
	. "${SRC}/packaging/functions.sh"
	set_engine_version "${VERSION}" "${ENGINE_STAGE}"
	set_mod_version "${VERSION}" "${ENGINE_STAGE}"/mods/*/mod.yaml
)
# The maps the main menu does not need (all but the shellmaps, about 11 MB of already compressed
# .oramap files) go into a second package that the page downloads after the menu is up, so that the
# first visit does not wait for them. Same paths in the browser's file system as before.
MAPS_STAGE="${OBJ}/stage/maps"
rm -rf "${MAPS_STAGE}"
python3 -I "${SRC_WEB}/tools/split-maps.py" "${ENGINE_STAGE}" "${MAPS_STAGE}" ${WEBRA_MODS}
python3 -I "${SRC_WEB}/tools/pack-data.py" "${ENGINE_STAGE}" /openra "${DIST}/data/engine"
MAP_DATA=""
if [ -d "${MAPS_STAGE}" ]; then
	python3 -I "${SRC_WEB}/tools/pack-data.py" "${MAPS_STAGE}" /openra "${DIST}/data/maps"
fi

# The content manifest: what the page downloads (through the relay, from the official mirrors) and
# installs on the first visit, read from the mod's own content installer definitions. The relay
# serves only the packages in this file (deploy/ copies it next to the relay).
FIRST_MOD="$(echo ${WEBRA_MODS} | cut -d ' ' -f 1)"
python3 -I "${SRC_WEB}/tools/content-manifest.py" "${ENGINE_STAGE}" "${FIRST_MOD}" "${DIST}/data/content-packages.json"

if [ -n "${WEBRA_CONTENT_DIR:-}" ]; then
	# Local testing only: this is copyrighted (freeware) game content. Do not publish it.
	# deploy/deploy.sh refuses a build that contains it.
	echo "WARNING: packaging local game content from ${WEBRA_CONTENT_DIR}: this build must not be published" >&2
	python3 -I "${SRC_WEB}/tools/pack-data.py" "${WEBRA_CONTENT_DIR}" /support/Content/ra/v2 "${DIST}/data/content"
fi

WEBRA_PROXY_URL="${WEBRA_PROXY_URL:-ws}"
printf '%s' "${WEBRA_PROXY_URL}" | grep -Eq '^(wss?://[A-Za-z0-9.:/_~%-]+|[A-Za-z0-9._~/-]+)$' \
	|| fail "WEBRA_PROXY_URL must be a plain ws:// or wss:// URL or a path, without query: ${WEBRA_PROXY_URL}"
# Official builds never send game traffic in clear text: wss://, or a path (the page resolves it to
# wss: on an https page and refuses ws: off loopback), or ws:// to loopback for local tests.
if [ "${WEBRA_DEV}" != "1" ]; then
	printf '%s' "${WEBRA_PROXY_URL}" | grep -Eq '^(wss://|ws://(127\.0\.0\.1|localhost|\[::1\])[:/]|[A-Za-z0-9._~/-]+$)' \
		|| fail "official builds need a wss:// relay (or a same-origin path); ws:// is only accepted for loopback: ${WEBRA_PROXY_URL}"
fi
WEBRA_CONTENT_RELAY="${WEBRA_CONTENT_RELAY:-content}"
WEBRA_CONTENT_URL="${WEBRA_CONTENT_URL:-}"
WEBRA_MASTER_RELAY="${WEBRA_MASTER_RELAY:-master}"
[ "${WEBRA_MASTER_RELAY}" = "none" ] && WEBRA_MASTER_RELAY=""
# (printf of an empty value gives grep no line at all, which it reports as no match: empty values are checked apart.)
for v in "${WEBRA_CONTENT_RELAY}" "${WEBRA_CONTENT_URL}"; do
	[ -z "${v}" ] || printf '%s' "${v}" | grep -Eq '^[A-Za-z0-9._~/-]*$' || fail "content paths must be plain paths on the page's own site: ${v}"
done
# The master route may live on the relay's own host (a cross-origin request, which the relay allows with
# -master-cors): an https:// URL, or http:// to loopback in local tests. Official builds never use http off loopback.
[ -z "${WEBRA_MASTER_RELAY}" ] || printf '%s' "${WEBRA_MASTER_RELAY}" | grep -Eq '^(https?://[][A-Za-z0-9.:_~%/-]+|[A-Za-z0-9._~/-]*)$' \
	|| fail "WEBRA_MASTER_RELAY must be a path or a plain https:// URL, without query: ${WEBRA_MASTER_RELAY}"
if [ "${WEBRA_DEV}" != "1" ]; then
	[ -z "${WEBRA_MASTER_RELAY}" ] || printf '%s' "${WEBRA_MASTER_RELAY}" | grep -Eq '^(https://|http://(127\.0\.0\.1|localhost|\[::1\])[:/]|[A-Za-z0-9._~/-]*$)' \
		|| fail "official builds need an https:// master route (or a same-origin path): ${WEBRA_MASTER_RELAY}"
fi
WEBRA_STORAGE_NAME="${WEBRA_STORAGE_NAME:-}"
[ -z "${WEBRA_STORAGE_NAME}" ] || printf '%s' "${WEBRA_STORAGE_NAME}" | grep -Eq '^[A-Za-z0-9:/._-]{0,64}$' || fail "WEBRA_STORAGE_NAME must be a plain name: ${WEBRA_STORAGE_NAME}"

# Content-addressed names for the data bundles (engine, maps), so browsers can cache them forever
# (deploy/webra.caddy: immutable). build.json, which names them, is always revalidated. The name
# covers the index too: a build that only changes paths (or adds an empty file) keeps the same .bin
# bytes, and an index cached under the old name would not match it.
bundle_hash() { (sha256sum < "$1.bin"; sha256sum < "$1.json") | sha256sum | cut -c 1-16; }
ENGINE_HASH="$(bundle_hash "${DIST}/data/engine")"
mv "${DIST}/data/engine.bin" "${DIST}/data/engine.${ENGINE_HASH}.bin"
mv "${DIST}/data/engine.json" "${DIST}/data/engine.${ENGINE_HASH}.json"
if [ -f "${DIST}/data/maps.bin" ]; then
	MAPS_HASH="$(bundle_hash "${DIST}/data/maps")"
	mv "${DIST}/data/maps.bin" "${DIST}/data/maps.${MAPS_HASH}.bin"
	mv "${DIST}/data/maps.json" "${DIST}/data/maps.${MAPS_HASH}.json"
	MAP_DATA="data/maps.${MAPS_HASH}"
fi
FRAMEWORK_BYTES="$(cat "${DIST}"/_framework/*.br | wc -c)"

cat > "${DIST}/data/build.json" << EOF
{"version":"${VERSION}","commit":"${COMMIT}","dev":${DEV_BOOL},"configuration":"${CONFIGURATION}","aot":${RUN_AOT},"runtime":"${DOTNET_RUNTIME_VERSION}","mods":"${WEBRA_MODS}","target":"${WEBRA_TARGET:-}","content":$([ -n "${WEBRA_CONTENT_DIR:-}" ] && echo true || echo false),"proxy":"${WEBRA_PROXY_URL}","contentRelay":"${WEBRA_CONTENT_RELAY}","contentUrl":"${WEBRA_CONTENT_URL}","masterRelay":"${WEBRA_MASTER_RELAY}","storage":"${WEBRA_STORAGE_NAME}","engineData":"data/engine.${ENGINE_HASH}","mapData":"${MAP_DATA}","contentManifest":"data/content-packages.json","frameworkBytes":${FRAMEWORK_BYTES}}
EOF

# The page title (WEBRA_PAGE_TITLE). Only the browser tab's title: the game's own name and window title stay.
# data-keep tells the page to keep it on the tab once the engine runs (webra-page.js keepPageTitle).
if [ -n "${WEBRA_PAGE_TITLE:-}" ]; then
	python3 -I - "${DIST}/index.html" "${WEBRA_PAGE_TITLE}" << 'PY'
import html, sys
path, title = sys.argv[1], sys.argv[2]
page = open(path, encoding="utf-8").read()
anchor = "<title>OpenRA</title>"
assert page.count(anchor) == 1, "index.html: no single <title>OpenRA</title>"
page = page.replace(anchor, "<title data-keep>" + html.escape(title) + "</title>")
open(path, "w", encoding="utf-8").write(page)
PY
	# The SDK's compressed copies of index.html are stale now.
	rm -f "${DIST}/index.html.br" "${DIST}/index.html.gz"
	python3 -I -c 'import gzip, sys; d = open(sys.argv[1], "rb").read(); open(sys.argv[1] + ".gz", "wb").write(gzip.compress(d, 9, mtime=0))' "${DIST}/index.html"
	echo "index.html: title ${WEBRA_PAGE_TITLE}"
fi

# The Content-Security-Policy also as <meta http-equiv> in the page (WEBRA_META_CSP=1): object storage
# cannot send headers, and a CDN rule that is missing or wrong would otherwise leave the page without a
# policy. Same policy as the header (tools/publish.py), minus frame-ancestors, which a meta policy ignores.
# It goes first in <head>: a meta policy only covers what comes after it.
if [ "${WEBRA_META_CSP:-0}" = "1" ]; then
	CSP="$(python3 -I "${SRC_WEB}/tools/publish.py" csp "${DIST}" --meta)"
	python3 -I - "${DIST}/index.html" "${CSP}" << 'PY'
import html, sys
path, csp = sys.argv[1], sys.argv[2]
page = open(path, encoding="utf-8").read()
anchor = '<meta charset="utf-8">'
assert page.count(anchor) == 1, "index.html: no single <meta charset>"
page = page.replace(anchor, anchor + '\n\t<meta http-equiv="Content-Security-Policy" content="' + html.escape(csp, quote=True) + '">')
open(path, "w", encoding="utf-8").write(page)
PY
	# The SDK's compressed copies of index.html are stale now.
	rm -f "${DIST}/index.html.br" "${DIST}/index.html.gz"
	python3 -I -c 'import gzip, sys; d = open(sys.argv[1], "rb").read(); open(sys.argv[1] + ".gz", "wb").write(gzip.compress(d, 9, mtime=0))' "${DIST}/index.html"
	echo "index.html: Content-Security-Policy in <meta>: ${CSP}"
fi

# The start screen (WEBRA_SPLASH): hosting/<name>-splash.html goes into index.html, which then opens on it
# (body data-phase="splash"; webra-splash.js). The build fills in the version and the source link, which names
# the commit the build was made from (the GPL's corresponding source; a dev build of a dirty tree links to the
# commit it started from, and says so here). Its logo
# slot (between <!-- logo --> and <!-- /logo -->) holds a text until hosting/<name>-logo.svg or .png exists:
# then the file is copied as splash-logo.<ext> and shown instead.
if [ -n "${WEBRA_SPLASH:-}" ]; then
	printf '%s' "${WEBRA_SPLASH}" | grep -Eq '^[a-z0-9-]{1,32}$' || fail "WEBRA_SPLASH must be a plain name: ${WEBRA_SPLASH}"
	SPLASH_HTML="${SRC_WEB}/hosting/${WEBRA_SPLASH}-splash.html"
	[ -f "${SPLASH_HTML}" ] || fail "no start screen ${SPLASH_HTML}"
	WEBRA_SOURCE_URL="${WEBRA_SOURCE_URL:-https://github.com/lanhouse-playra/game}"
	printf '%s' "${WEBRA_SOURCE_URL}" | grep -Eq '^https://[A-Za-z0-9.-]+(/[A-Za-z0-9._~/-]*)?$' || fail "WEBRA_SOURCE_URL must be a plain https:// URL: ${WEBRA_SOURCE_URL}"
	SOURCE_COMMIT="${COMMIT%-dirty}"
	printf '%s' "${SOURCE_COMMIT}" | grep -Eq '^[0-9a-f]{40}$' || fail "the start screen links to the build's commit, and this build has none (${COMMIT})"
	SOURCE_LINK="${WEBRA_SOURCE_URL%/}/tree/${SOURCE_COMMIT}"
	[ "${SOURCE_COMMIT}" = "${COMMIT}" ] || echo "WARNING: dirty working tree: the start screen's source link names ${SOURCE_COMMIT}, which lacks the uncommitted changes" >&2
	SPLASH_LOGO=""
	for ext in svg png; do
		if [ -f "${SRC_WEB}/hosting/${WEBRA_SPLASH}-logo.${ext}" ]; then
			SPLASH_LOGO="splash-logo.${ext}"
			cp "${SRC_WEB}/hosting/${WEBRA_SPLASH}-logo.${ext}" "${DIST}/${SPLASH_LOGO}"
			break
		fi
	done
	python3 -I - "${DIST}/index.html" "${SPLASH_HTML}" "${VERSION}" "${SOURCE_LINK}" "${SPLASH_LOGO}" << 'PY'
import html, re, sys
path, fragment_path, version, source, logo = sys.argv[1:]
page = open(path, encoding="utf-8").read()
fragment = open(fragment_path, encoding="utf-8").read()
for anchor in ("\t<!-- splash -->\n", '<body data-phase="loading">'):
    assert page.count(anchor) == 1, f"index.html: no single {anchor!r}"
for token in ("@VERSION@", "@SOURCE_URL@", "<!-- logo -->", "<!-- /logo -->"):
    assert fragment.count(token) == 1, f"{fragment_path}: no single {token}"
fragment = fragment.replace("@VERSION@", html.escape(version)).replace("@SOURCE_URL@", html.escape(source, quote=True))
if logo:
    # The text between the markers names the site: it becomes the image's alternative text.
    slot = re.search(r"<!-- logo -->(.*)<!-- /logo -->", fragment, re.S)
    text = html.escape(html.unescape(re.sub(r"<[^>]*>", "", slot.group(1))).strip(), quote=True)
    fragment = fragment[:slot.start()] + f'<img src="{logo}" alt="{text}">' + fragment[slot.end():]
page = page.replace("\t<!-- splash -->\n", fragment).replace('<body data-phase="loading">', '<body data-phase="splash">')
open(path, "w", encoding="utf-8").write(page)
PY
	rm -f "${DIST}/index.html.br" "${DIST}/index.html.gz"
	python3 -I -c 'import gzip, sys; d = open(sys.argv[1], "rb").read(); open(sys.argv[1] + ".gz", "wb").write(gzip.compress(d, 9, mtime=0))' "${DIST}/index.html"
	echo "index.html: start screen ${SPLASH_HTML}${SPLASH_LOGO:+, logo ${SPLASH_LOGO}}, source ${SOURCE_LINK}"
fi

# Brotli copies of everything compressible, each checked against its file (tools/brotli.mjs): a host
# without content negotiation (R2) serves exactly one encoding per object, the .br copy.
find "${DIST}" -type f \( -name '*.html' -o -name '*.js' -o -name '*.json' -o -name '*.wasm' -o -name '*.dat' -o -name '*.bin' -o -name '*.svg' \) \
	! -name 'content.*' -print0 | sort -z | xargs -0 "${EMSDK_NODE}" "${SRC_WEB}/tools/brotli.mjs" > "${OBJ}/brotli.log"
echo "brotli: $(grep -c '"kept"' "${OBJ}/brotli.log") checked, $(grep -vc '"kept"' "${OBJ}/brotli.log") written"

# What a first visit downloads until the main menu, in the bytes the publish uploads (so after the brotli
# copies), into build.json: the start screen shows the size, and checks the list against the browser's
# cache (tools/publish.py first-visit). build.json changed, so its brotli copy is made again.
python3 -I "${SRC_WEB}/tools/publish.py" first-visit "${DIST}" --encoding "${WEBRA_PUBLISH_ENCODING:-br}"
"${EMSDK_NODE}" "${SRC_WEB}/tools/brotli.mjs" "${DIST}/data/build.json" >> "${OBJ}/brotli.log"

# Precompressed copies of the data for the web server (the SDK already compressed _framework and
# the page's own files). deploy/Caddyfile serves them with "precompressed zstd br gzip".
python3 -I "${SRC_WEB}/tools/precompress.py" "${DIST}/data"

# The publish manifest, next to dist/: every object to upload with its headers, and what the CDN must add
# (tools/publish.py). Not for builds with local content, which must never be published.
PUBLISH_MANIFEST="${WEB}/publish.json"
rm -f "${PUBLISH_MANIFEST}"
if [ -z "${WEBRA_CONTENT_DIR:-}" ]; then
	python3 -I "${SRC_WEB}/tools/publish.py" manifest "${DIST}" "${PUBLISH_MANIFEST}" --encoding "${WEBRA_PUBLISH_ENCODING:-br}"
fi

echo "Built ${VERSION} (${COMMIT}, dev=${DEV_BOOL}, AOT=${RUN_AOT}) into ${DIST}"
du -sh "${DIST}"
