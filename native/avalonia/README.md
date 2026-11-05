# Avalonia.Native accessibility lifetime fix

The macOS build uses Avalonia 12.1.4 managed assemblies with a rebuilt, ABI-compatible
native library. The official 12.1.4 and 12.1.5 native libraries retain detached controls
after accessibility traversal. Rebuilding the native bridge fixes the leak without
disabling accessibility or adopting the wider 12.2 API changes.

Source: AvaloniaUI/Avalonia, tag `12.1.4`, commit
`2d79ba55fae1b1e71c69f93a668080c9029e6a31`.
The API source archive has SHA-256
`7ef31cc7819ecbf4a1195bece4e5fe1117032a59ee0f47c0c1782ea618804a39`.

`accessibility-lifetime.patch` is the native ownership fix from
[upstream PR #22492](https://github.com/AvaloniaUI/Avalonia/pull/22492).
That PR was superseded by the broader [#22519](https://github.com/AvaloniaUI/Avalonia/pull/22519),
which is intended for 12.2 and changes code generation across the backend. This local
patch retains the 12.1.4 interface and fixes the four native accessibility files:

- Adopt returned COM references in `ComPtr` without an extra `AddRef`.
- Make the node's reference to its AppKit owner weak, so removed elements can die.
- Keep nodes alive through their managed peers and release their owned reference on disposal.
- Recreate a released element behind its existing node when a live control reappears.

The build script downloads and verifies the source archive, applies the checked-in
patch, generates headers with the upstream pinned MicroCom 0.11.6 generator, and
compiles the upstream source list for arm64 and x86_64. It preserves the upstream
ARC exception for `noarc.mm` and links the same system frameworks. Only Xcode Command
Line Tools, Python 3.12+ and the existing .NET SDK are needed. Source and compiler
artifacts stay in `.tools/`; the NuGet cache is never modified. The compiled library
is cached by source, patch, build script and toolchain identity.

The application project copies the patched library after both build and publish.
Cross-compiling Windows skips this target. Keep the Avalonia managed version and
native source version together when updating dependencies. `UsePatchedAvaloniaNative=false`
is for isolated regression comparisons only, using a fresh output directory.

```sh
python3 scripts/qa-memory-macos.py --output-directory dist/memory-stock --stock
# Expected failure: all five detached views remain alive.
python3 scripts/qa-memory-macos.py --output-directory dist/memory-fixed
# Expected success: zero detached views remain alive; snapshot checks pass.
python3 scripts/qa-memory-macos.py --output-directory dist/memory-soak --seconds 1800
```

QA walks only its own accessibility tree. It never connects to Herdr or Clash,
loads user settings, or registers login items. Forced GC is confined to the
detached-view check; the soak uses ordinary GC and the actual main-window controls,
rotates 500 log entries, and cycles visible/minimized/hidden states. Reports include
the process's physical footprint (including compressed memory), managed memory and
reachable accessibility-node count every five seconds.

The Avalonia source and patch are MIT licensed; see [LICENSE.md](LICENSE.md).
