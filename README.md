# TVRandomizer

F# addon for Lethal Company's TVLoader. The host shuffles the shared video playlist at session start and after the final clip finishes naturally. TVLoader still owns video preparation and playback; this addon replaces the old copied player implementation.

## Multiplayer behavior

Every participant needs TVRandomizer, TVLoader **1.1.2**, and LethalNetworkAPI **3.4.2** with their dependencies. Video packs do not need identical paths or filenames: SHA-256 content hashes identify matching files. Identical copies retain occurrence identities, so negotiation preserves the minimum duplicate count available across clients.

- The host sends the actual order, not a seed that depends on each client's random-number implementation.
- Joining clients submit inventories. Playback pauses while inventories are unavailable; a client without the addon leaves negotiation waiting, with a log warning rather than an automatic kick.
- A smaller shared subset restarts the cycle immediately. An unchanged subset resumes existing playback; joining clients receive the host's clip index and power state.
- Disconnects do not interrupt a playing playlist. Expansion waits for its next natural completion. If the intersection was empty, there is no active cycle to finish; a membership change can restore an available playlist, with the TV remaining off.
- No common videos means TV off with a clear log report. The lobby remains usable.
- Normal TV off/on skips retain TVLoader behavior. Skipping past the last clip does not itself reshuffle. A natural final-clip completion does.
- The prepared next clip is invalidated at a restart, so the previous cycle's cached first video cannot leak into the next permutation.

Order and playlist generations are host-authoritative. Playback is **not frame-accurate**, and late joins start at the host's clip rather than seeking to its exact timestamp. A shuffle may legitimately produce the same permutation or repeat a video across the cycle boundary.

Inventories are calculated once at plugin startup, with a maximum of **256 entries** to keep messages below the networking library's recommended size. Larger libraries are rejected, not truncated. Restart the profile after changing files. Initial hashing reads each complete file and can delay startup for large packs.

The sender guard authenticates this addon's channels before LethalNetworkAPI dispatches them. It does not fix that library's general transport or pre-dispatch deserialization behavior.

## Build and checks

```console
nix develop
nix build .#default
nix build .#zip --out-link result-zip
nix flake check
nix run .#update-deps
```

Edit `outputs.nix`, not the generated `flake.nix`. Regenerate the latter with `nix run .#write-flake` when input definitions change.

The modern build SDK is .NET **10.0.401**; the plugin and ordering library target **netstandard2.1**, with **FSharp.Core 8.0.403** packaged alongside them. No proprietary game assemblies are needed for compilation or distributed in the ZIP. Private TVLoader contracts are deliberately pinned and checked against its actual release DLL.

The ZIP contains root-level Thunderstore metadata, a 256x256 icon, licenses, and exactly three plugin assemblies under `BepInEx/plugins/TVRandomizer`. BepInEx, TVLoader, LethalNetworkAPI, OdinSerializer, and Unity assemblies are dependencies, not bundled copies. CI builds and checks the artifact; it does not publish it. The sibling `TVRandomizerUpstream` video pack is unchanged.

## Isolated test profile

Preparation creates a **new** directory, copies pinned loader/dependency/mod files into it, symlinks existing game files, and allocates separate Proton data. It refuses to overwrite a profile and does not install into Steam's game directory.

```console
nix run .#prepare-profile -- \
  "$HOME/.local/share/Steam/steamapps/common/Lethal Company" \
  "$HOME/.local/share/TVRandomizer/test-profile" \
  --videos-dir "/path/to/Television Videos"
```

Launching is a separate, explicit operation. Steam must be running; supply an existing Proton launcher's absolute path and your Steam directory:

```console
nix run .#launch-profile -- \
  "$HOME/.local/share/TVRandomizer/test-profile" \
  "/path/to/Proton/proton" \
  "$HOME/.local/share/Steam"
```

Inspect the profile-local `BepInEx/LogOutput.log`. Keep saves and logs in its separate compatibility directory. The launcher sets the native `winhttp` override required by Doorstop. Successful profile assembly does not prove Proton injection or game compatibility.

## Manual acceptance checklist

The game has **not been launched** for this repair. Compilation, offline tests, metadata checks, and ZIP validation are not a runtime or multiplayer pass.

1. Launch the isolated profile. Verify all dependencies and TVRandomizer load without exceptions, and the first clip agrees with the logged host playlist cycle.
2. Use a small set of visibly distinct, short MP4s. Watch two complete cycles; confirm every negotiated occurrence appears once per cycle and the new first clip is not the old preloaded one.
3. Toggle TV power around a wrap. Confirm a skip alone does not create a new shuffle cycle.
4. Join with a second profile using the same bytes under different filenames. Confirm matching order and host clip identity, without expecting exact timestamps.
5. Join with a smaller overlapping pack. Confirm negotiation pauses, then restarts all clients on the reduced subset. Repeat with simultaneous joins.
6. Try disjoint packs and a client without the addon. Confirm TV off/waiting with useful logs, while the lobby remains usable.
7. Disconnect the restrictive client. Confirm an active cycle is not interrupted and expansion occurs at its next natural end; test recovery from an empty intersection separately.
8. Change levels, leave/rejoin lobbies, and put the host inside the factory. Confirm scene replacement resets player references, stale session messages cannot change the new order, and natural completion still advances.

MIT license. Existing license notices are retained; the copied C# TVLoader implementation is removed.
