#!/bin/sh
# Runs the numeric conformance corpus (OpenRA.Web/conformance/ConformanceCorpus.cs) on desktop
# CoreCLR with desktop OpenRA's Lua library, and in the browser build served at <url>, and diffs the
# outputs. Lockstep games between browser and desktop players need zero differences. Run it for the
# interpreter and the AOT build, and after any toolchain, Lua or Eluant change.
#
# usage: OpenRA.Web/tools/conformance.sh <url of a served dist/> <output dir>
# env:   PLAYWRIGHT_CORE, CHROME, GPU as for conformance-browser.mjs; NODE (default: node)
set -o errexit || exit $?
set -o nounset

[ $# -eq 2 ] || { echo "usage: $0 <url> <output dir>"; exit 2; }
URL="$1"
OUT="$2"
TOOLS="$(cd "$(dirname "$0")" && pwd)"
WEB="$(dirname "${TOOLS}")"
mkdir -p "${OUT}"

export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
echo "== desktop (CoreCLR)"
(cd "${WEB}" && dotnet build conformance/desktop/OpenRA.Web.Conformance.csproj -c Release -nologo -v quiet)
"${WEB}/conformance/desktop/bin/OpenRA.Web.Conformance" > "${OUT}/desktop.txt"

echo "== browser (${URL})"
"${NODE:-node}" "${TOOLS}/conformance-browser.mjs" "${URL}" "${OUT}/browser.txt"

echo "== diff"
STATUS=0
python3 -I "${TOOLS}/conformance-diff.py" "${OUT}/desktop.txt" "${OUT}/browser.txt" > "${OUT}/diff.txt" || STATUS=$?
cat "${OUT}/diff.txt"
exit "${STATUS}"
