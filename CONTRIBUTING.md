# Contributing

## Workflow

1. Install the .NET SDK matching [`global.json`](global.json) and Rust with the `wasm32-wasip1` target (`rustup target add wasm32-wasip1`).
2. Run `make developer` to restore, build, and test. The first build runs `make wasm` to produce the embedded wasm core from the pinned crates.io release.
3. Make changes. Use hard tabs for indentation (spaces only where the format requires them, ex. YAML).
4. Run `make do-lint` and `make test` before opening a pull request.

Always drive tasks through the `Makefile`, never raw `dotnet`.

## Testing

`make test` runs both suites: the unit tests (`tests/KeetaNet.Anchor.Tests`) and the e2e tests (`tests/KeetaNet.Anchor.E2eTests`), which drive the reference TypeScript anchor through the harness in `tests/node-harness`.

The e2e suite needs:

- Node.js 20.
- Access to the `@keetanetwork` scope on GitHub Packages. `tests/node-harness/.npmrc` routes the scope to `npm.pkg.github.com`; authenticate with a token that has `read:packages` (locally via `npm login --registry=https://npm.pkg.github.com`, in CI via `NODE_AUTH_TOKEN`).

`make node-harness` installs and compiles the harness; `make test` runs it automatically.

To run a subset, use the xUnit v3 filter flags (Microsoft.Testing.Platform syntax after `--`, not VSTest `--filter`):

```sh
# One class
dotnet test tests/KeetaNet.Anchor.E2eTests/KeetaNet.Anchor.E2eTests.csproj -c Release \
    -- --filter-class "*KycInteropTests"

# One method (fully qualified; '*' wildcards allowed)
dotnet test tests/KeetaNet.Anchor.E2eTests/KeetaNet.Anchor.E2eTests.csproj -c Release \
    -- --filter-method "*CsharpReadsAndProvesTheTypescriptIssuedLeaf"
```

Tests quarantined for known upstream divergences are marked `Skip` with the reason inline; do not re-enable them without the upstream fix.

## Conventions

- Public API changes require a documentation comment on every new public member (`GenerateDocumentationFile` is on).
- Integration tests use the public API only. No mocks, not ever.
- Do not introduce native P/Invoke (`DllImport`/`SafeHandle`/`GCHandle`) or finalizers that touch the Wasmtime `Store`. See the FFI safety model in the [README](README.md).

## Releases

Releases are cut from a signed `releases/vX.Y.Z` tag:

```sh
make release vX.Y.Z
```

Pushing the tag triggers the publish and release workflows.
