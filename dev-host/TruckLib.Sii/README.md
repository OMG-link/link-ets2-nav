# TruckLib.Sii local build

This source snapshot replaces the host's TruckLib.Sii package for both ETS2LA and
LinkEts2Nav. It retains the Sprache parser framework and public loading APIs.

- Upstream: https://github.com/sk-zk/TruckLib.Sii, commit `3386051`.
- Parser changes: `89f28f1` (grammar factoring) and `a67fe57` (lexical scanning)
  on the local `feat/sprache-parser-optimization` branch.
- Only `ParserElements.cs` differs from the upstream library source.
- The included `LICENSE` is the upstream GPL version 2 license.

The project keeps the host's assembly version 0.2.1 and TruckLib.Core 0.3.0
dependency. These build settings avoid changing unrelated host dependencies.
`Directory.Build.targets` replaces TruckLib's package reference with this project;
there is no parser switch or runtime fallback. No installed NuGet package is edited.

To update this snapshot, review and copy the upstream library source files and
license, keep the local project settings, and run the upstream parser tests against
this project before building the host and plugin. Do not copy build outputs.
