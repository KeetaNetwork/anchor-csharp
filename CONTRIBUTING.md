# Contributing

## Workflow

1. Install the .NET SDK matching [`global.json`](global.json) and Rust with the `wasm32-wasip1` target (`rustup target add wasm32-wasip1`).
2. Run `make developer` to restore, build, and test. The first build runs `make wasm` to produce the embedded wasm core from the pinned crates.io release.
3. Make changes. Use hard tabs for indentation (spaces only where the format requires them, ex. YAML).
4. Run `make lint` and `make test` before opening a pull request.

Always drive tasks through the `Makefile`, never raw `dotnet`.

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
