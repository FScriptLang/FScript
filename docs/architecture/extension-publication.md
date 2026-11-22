# VS Code extension publication

[FScriptLang/vscode-extension](https://github.com/FScriptLang/vscode-extension)
owns the VS Code client, grammar, client tests, real editor smoke test, VSIX
packaging, and marketplace publication. The publisher and extension identifier
remain `MagnusOpera.fscript`. FScript owns the language server, its tests, the
CLI/debug adapter, and their implementation. No source moves between repos.

## Language-server distribution

Ordinary FScript releases publish a portable framework-dependent .NET server
archive, `fscript-lsp-X.Y.Z-dotnet.zip`, and a checksum/provenance manifest,
`fscript-lsp-X.Y.Z.json`. Both use the language release's numeric version and
canonical `vX.Y.Z` GitHub tag. The archive includes all managed dependencies and
satellite resources; the client acquires .NET separately. `UseAppHost=false`
avoids a platform-specific launcher. The release job verifies product and SDK
assembly identities and runs the server suite against the staged distribution
before archiving. Public assets still wait for the existing release CI and
macOS signing/notarization gates. Managed LSP assemblies do not add a native
macOS executable or unsigned CLI staging archive to the public release.

The manifest records the source commit, required runtime, schema version, and
archive SHA-256. The archive embeds matching `server.json` provenance. The
[distribution specification](../specs/language-server-distribution.md) defines
the contract. An LSP fix produces an ordinary FScript release; publishing a new
VSIX is necessary only when client behavior or compatibility changes.

## Extension acquisition and updates

The VSIX includes client code and a supported server version range/runtime in
`language-server.json`. It contains no FScript binaries and never checks out or
builds FScript during packaging. Runtime acquisition downloads official GitHub
Release assets into versioned global extension storage on the extension host,
including remote development hosts. Downloads verify SHA-256, provenance,
required files, and runtime before an atomic directory rename. Previous versions
are retained for offline use and rollback. An exact user-level version pin
selects a supported release; `fscript.server.path` bypasses acquisition.

Startup uses a healthy supported cached server immediately. Without one it
installs the newest supported stable server. A background startup check offers
newer compatible servers, or an extension update for a newer incompatible
server. Existing installations are never upgraded without acceptance. Update
failures log without interrupting cached language services; an explicit check
reports errors. Successful updates offer a reload. The CLI remains separately
selected for execution, tests, REPL, and debugging.

There is no promise to maintain older protocol implementations. Publish breaking
FScript assets first, then an extension declaring the new supported range.
Older extensions remain on their supported servers. The first acquisition-based
extension requires a FScript release containing these assets (initial minimum
0.95.5); historical releases and tags are not rewritten.

## Extension publication

Extension main pushes and pull requests build a testable VSIX without needing
FScript release assets, a .NET SDK, or cross-repository credentials. Canonical
extension `vX.Y.Z` tags validate the committed version and extension changelog,
then test, package, inspect, release, and publish the same VSIX to both registries.
Marketplace uses GitHub OIDC Trusted Publishing bound to that repository's
`release.yml` and restricted `release` environment, with job-scoped
`id-token: write` and `vsce publish --oidc`. Only Open VSX needs its
`OPEN_VSX_TOKEN` environment secret. FScript does not package a VSIX or receive
marketplace secrets. Product versions, existing artifacts, and identities remain
independent and immutable.

For local development, point `fscript.server.path` and `fscript.cli.path` at
FScript build outputs. Client development and smoke-test instructions live in
the extension repository.
