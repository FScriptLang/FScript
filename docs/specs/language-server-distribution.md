# Language-server distribution

## Release artifacts

Canonical stable FScript `vX.Y.Z` GitHub Releases publish both
`fscript-lsp-X.Y.Z-dotnet.zip` and `fscript-lsp-X.Y.Z.json`. Server and language
versions are identical; independent server versioning is not introduced.
A server-only correction still uses an ordinary FScript release.

The ZIP is a portable, framework-dependent .NET distribution with no apphost.
It contains the server executable assembly, Language, Runtime, CSharpInterop,
Extensibility SDK, dependency/runtime configuration files, all other published
dependencies and resource subdirectories, and `server.json` at its root.
Language, Runtime, CSharpInterop and server assembly versions must match the
released product. Extensibility retains its own published assembly identity.
The staged server must pass the language-server suite, including real provider
loading, before publication.

The external JSON manifest has this shape (values are illustrative):

```json
{
  "schemaVersion": 1,
  "version": "0.95.5",
  "revision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "runtime": "11.0",
  "archive": {
    "name": "fscript-lsp-0.95.5-dotnet.zip",
    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
  }
}
```

`revision` is the full source commit. `runtime` is the major/minor .NET runtime
required by the server's runtime configuration. `sha256` is the lowercase
SHA-256 of the exact ZIP bytes. Embedded `server.json` contains the same first
four fields, without `archive`. Future incompatible manifest schemas must use a
new schema number; schema 1 clients reject them.

## Client selection

The VS Code extension declares a supported canonical numeric server version
range and required runtime. It acquires only official FScript GitHub Release
assets, excluding drafts, pre-releases, noncanonical tags, and releases missing
either artifact. Selection uses semantic version ordering rather than GitHub
release creation order. A release may be published without a new extension.
Breaking server changes require a later extension with a new supported range;
no backwards protocol implementation is required.

A healthy supported cached installation starts without waiting for an update
check. First use installs the newest supported release. An exact version pin
must fall within the supported range and selects that release even when newer
cached versions exist. Unsupported pins fail clearly. A custom server path
bypasses managed acquisition and automatic update checks for that server.

Startup checks releases in the background. Newer supported servers are offered
for user-approved installation. A newer unsupported server offers an extension
update; the client never automatically installs it. A pin suppresses server
upgrade offers. Background failures log and retain the running server; manual
checks report failures. Updates become active after reload or a later activation.

## Installation and execution

The client verifies the manifest version, schema, runtime, source revision, and
ZIP checksum. Extraction accepts only portable relative ordinary files and
directories, rejects traversal, symlinks and duplicate paths, and bounds download
and expanded sizes. It validates the staged distribution before atomically
making its version directory available. Concurrent installation and incomplete
cache entries cannot select a partially extracted server. Failed installs leave
other cached versions intact. Old versions remain available for rollback by pin.

The client acquires the matching .NET runtime and launches the server DLL over
stdio. It stores installations on the extension host, including remote hosts.
LSP updates never install, replace, or change selection of the CLI used for
script execution, tests, REPL, and debugging. Workspace trust continues to
control provider loading and execution independently of server acquisition.
