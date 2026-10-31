# webRA: OpenRA in the browser

This is stock [OpenRA](https://www.openra.net) (the Red Alert mod, release `playtest-20261026`) compiled to
WebAssembly so that it runs in a web browser, with nothing to install. Browser players join the regular OpenRA
multiplayer servers from the normal server list and play in the same matches as desktop players: same version, same
mod data, lockstep-compatible.

Play it at **https://play.ra.lanhouse.app/**, hosted by [Lanhouse](https://lanhouse.app).

It is meant for the OpenRA community, not to split it: there is no separate server list or matchmaking, and a browser
player is just one more player in the same lobbies.

## Credit

webRA was built and funded by Morris Ruschel for lanhouse.app. It was developed with AI assistance: the code was
written with Claude Code (by Anthropic) under his direction, and the commits carry `Co-Authored-By` lines that say so.

All credit for the game engine and the Red Alert mod belongs to the OpenRA developers and contributors
([openra.net](https://www.openra.net), [github.com/OpenRA/OpenRA](https://github.com/OpenRA/OpenRA)). This repository
is their engine with the changes a browser needs. webRA is not affiliated with or endorsed by the OpenRA team.

This port is not an upstream contribution. OpenRA's repository does not accept AI-generated contributions (see its
[AGENTS.md](https://github.com/OpenRA/OpenRA/blob/bleed/AGENTS.md)), and we respect that: the port lives here, as a
separate fork. Please report problems with the browser version here, not to the OpenRA team.

## How it works

- **Same game.** Browser-specific code is behind `OperatingSystem.IsBrowser()` or lives in `OpenRA.Web/`. An official
  build refuses to build unless the mod data equals the upstream release and every changed upstream file is listed in
  `OpenRA.Web/upstream-patches.txt`, so servers see exactly the version they expect.
- **Runtime.** .NET 10 `browser-wasm` with Mono AOT; SDL2, FreeType and OpenAL from emscripten, Lua 5.1 compiled
  with it; rendering through emscripten's SDL2 on WebGL 2. The toolchain is pinned, because the runtime is part of the simulation's
  determinism: a numeric conformance corpus (`OpenRA.Web/tools/conformance.sh`) compares desktop and browser runtimes,
  and the page refuses to start on a runtime that was not validated.
- **Network.** Browsers cannot open TCP connections, so the game connects through a WebSocket-to-TCP relay
  ([lanhouse-playra/server](https://github.com/lanhouse-playra/server), folder `proxy/`). The relay does not change
  the game protocol; it only connects to servers on the OpenRA master server list (and our own), and it also carries
  the master server's version check and news, which browsers cannot fetch directly.
- **Real player addresses on our servers.** The relay can tell our own dedicated servers each browser player's real
  IP address with the PROXY protocol, through a small server patch on this branch: the setting
  `Server.TrustedProxies` (empty by default, so a server without it behaves exactly as before). Other servers see the
  relay's address.
- **Game content.** The freeware Red Alert files (the same quick-install package the desktop installer downloads) are
  downloaded on the first visit, checked against the mod's SHA1 and stored in the browser. They are never part of
  this repository or of a build. play.ra.lanhouse.app serves its own verified copy, so first visits do not load the
  community mirrors.
- **Saved data.** Settings, replays, downloaded maps and screenshots are kept in the browser's IndexedDB and survive
  reloads; replays and screenshots are also saved to the browser's Downloads.

## Branches and tags

`playra-main` is the only branch: it is what runs on play.ra.lanhouse.app. The work was done in phases, each reviewed
on its own; each phase is kept as a tag:

| Tag | Phase |
|---|---|
| `playra-phase-0-boot` | OpenRA booting in the browser, reproducible official builds, pinned toolchain, conformance corpus |
| `playra-phase-1-net` | multiplayer: game connections through the WebSocket relay, ticking in hidden tabs |
| `playra-phase-2-perf` | in-match performance (frame pacing, GL shim, maps downloaded after the menu); no simulation change |
| `playra-phase-3-play` | a link that installs and remembers: content install, saved settings and replays, packaging for hosting |
| `playra-trusted-proxies` | `Server.TrustedProxies`: real client addresses from trusted relays with the PROXY protocol |
| `playra-release-candidate` | the phases merged: the first release candidate |

After the release candidate, `playra-main` added the Lanhouse hosting target (start screen, page title, game content
from its own site), the browser settings pass and the downloads of replays and screenshots.

## Building

On Linux x64, with:

- the .NET SDK **10.0.403** exactly (`OpenRA.Web/global.json`, no roll-forward), `dotnet` on `PATH`;
- the `wasm-tools` workload (`dotnet workload install wasm-tools`): the build checks for workload manifest
  **10.0.114**, the Mono `browser-wasm` runtime pack **10.0.14** and the emscripten **3.1.56** that the workload
  ships (no separate emsdk is used);
- `python3`, `curl`, `git` and `patch`.

Lua 5.1.5, the OpenRA Eluant sources and emscripten's SDL2 and FreeType ports are downloaded into `OpenRA.Web/obj/`
on the first run and verified.

```sh
WEBRA_TARGET=lanhouse WEBRA_AOT=1 ./build-web.sh   # the build that runs on play.ra.lanhouse.app
./build-web.sh serve                               # serve OpenRA.Web/dist/ on http://127.0.0.1:8090/
```

An official build (the default) needs a clean git checkout based on the tag `playtest-20261026` and compiles a
`git archive` of `HEAD`, never the working tree. `WEBRA_DEV=1` builds the working tree with a development version
that no public server accepts. `WEBRA_TARGET=<name>` reads `OpenRA.Web/hosting/<name>.env` (relay address, content
source, page title, start screen); copy `lanhouse.env` to host your own. The header of `build-web.sh` documents every
variable. AOT compiles one assembly at a time by default, which fits in about 6 GB of memory; an AOT build takes
around 20 minutes.

## What does not work in the browser

- Local games: skirmish, missions and creating a server (a browser cannot host one). Join a server from Multiplayer.
- The map editor.
- Linking an OpenRA forum account.
- Some display and system options: video mode, window size, VSync, display selection, lock mouse to window,
  UPnP/NAT-PMP, Discord Rich Presence, disc detection, auto-save. They are shown disabled with "Not available in the
  browser version".

## License

GPL-3.0, like OpenRA (see [COPYING](COPYING)). This repository is the corresponding source of the site. The Red Alert
game content is freeware from EA and is not included.

---

## Original OpenRA README

Everything below is OpenRA's own README, unchanged.

# OpenRA

A Libre/Free Real Time Strategy game engine supporting early Westwood classics.

* Website: [https://www.openra.net](https://www.openra.net)
* Chat: [#openra on Libera](ircs://irc.libera.chat:6697/openra) ([web](https://web.libera.chat/#openra)) or [Discord](https://discord.openra.net) ![Discord Badge](https://discordapp.com/api/guilds/153649279762694144/widget.png)
* Repository: [https://github.com/OpenRA/OpenRA](https://github.com/OpenRA/OpenRA) ![Continuous Integration](https://github.com/OpenRA/OpenRA/workflows/Continuous%20Integration/badge.svg)

Please read the [FAQ](https://github.com/OpenRA/OpenRA/wiki/FAQ) in our [Wiki](https://github.com/OpenRA/OpenRA/wiki) and report problems at [https://github.com/OpenRA/OpenRA/issues](https://github.com/OpenRA/OpenRA/issues).

Join the [Forum](https://forum.openra.net/) for discussion.

## Play

Distributed mods include a reimagining of

* Command & Conquer: Red Alert
* Command & Conquer: Tiberian Dawn
* Dune 2000

EA has not endorsed and does not support this product.

Check our [Playing the Game](https://github.com/OpenRA/OpenRA/wiki/Playing-the-game) Guide to win multiplayer matches.

## Contribute

* Please read [INSTALL.md](https://github.com/OpenRA/OpenRA/blob/bleed/INSTALL.md) and [Compiling](https://github.com/OpenRA/OpenRA/wiki/Compiling) on how to set up an OpenRA development environment.
* See [Hacking](https://github.com/OpenRA/OpenRA/wiki/Hacking) for a (now very outdated) overview of the engine.
* Read and follow our [Code of Conduct](https://github.com/OpenRA/OpenRA/blob/bleed/CODE_OF_CONDUCT.md).
* To get your patches merged, please adhere to the [Contributing](https://github.com/OpenRA/OpenRA/blob/bleed/CONTRIBUTING.md) guidelines.

## Mapping

* We offer a [Mapping](https://github.com/OpenRA/OpenRA/wiki/Mapping) Tutorial as you can change gameplay drastically with custom rules.
* For scripted mission have a look at the [Lua API](https://docs.openra.net/en/release/lua/).
* If you want to share your maps with the community, upload them at the [OpenRA Resource Center](https://resource.openra.net).

## Modding

* Download a copy of the [OpenRA Mod SDK](https://github.com/OpenRA/OpenRAModSDK) to start your own mod.
* Check the [Modding Guide](https://github.com/OpenRA/OpenRA/wiki/Modding-Guide) to create your own classic RTS.
* There exists an auto-generated [Trait documentation](https://docs.openra.net/en/latest/release/traits/) to get started with yaml files.
* Some hints on how to create new OpenRA compatible [Pixelart](https://github.com/OpenRA/OpenRA/wiki/Pixelart).
* Upload total conversions at [our Mod DB profile](https://www.moddb.com/games/openra/mods).

## Support

* Sponsor a [mirror server](https://github.com/OpenRA/OpenRAWebsiteV3/tree/master/packages) if you have some bandwidth to spare.
* You can immediately set up a [Dedicated](https://github.com/OpenRA/OpenRA/wiki/Dedicated-Server) Game Server.

## License
Copyright (c) OpenRA Developers and Contributors
This file is part of OpenRA, which is free software. It is made
available to you under the terms of the GNU General Public License
as published by the Free Software Foundation, either version 3 of
the License, or (at your option) any later version. For more
information, see [COPYING](https://github.com/OpenRA/OpenRA/blob/bleed/COPYING).

# Sponsors
Free code signing on Windows provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).
