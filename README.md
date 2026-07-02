# anchor-csharp

C# SDK for the KeetaNet anchor. The client logic runs inside a sandboxed WebAssembly core (`keetanetwork_anchor_client_wasi.wasm`) hosted in-process by [Wasmtime](https://github.com/bytecodealliance/wasmtime-dotnet); this repository is the managed surface over it.

## Packages

| Package | Description |
| --- | --- |
| `KeetaNet.Anchor` | KYC and asset-movement clients, crypto, certificates, and containers over the anchor wasm core. |

## Requirements

- .NET SDK per [`global.json`](global.json) (.NET 10; the library targets `net8.0` and `net10.0`).
- Rust with the `wasm32-wasip1` target, for `make wasm` only (`rustup target add wasm32-wasip1`). NuGet consumers do not need Rust: the wasm core ships embedded in the package.

## Quick start

```sh
make developer   # verify SDK, restore, build, run tests
make help        # list all targets
```

Use the `Makefile`, not raw `dotnet`, for every task:

| Command | Purpose |
| --- | --- |
| `make build` | Build the solution (builds the wasm core first if missing) |
| `make test` | Run tests (xUnit v3 on Microsoft.Testing.Platform) |
| `make coverage` | Run tests with code coverage |
| `make lint` | Verify formatting and spelling |
| `make format` | Apply formatting fixes |
| `make pack` | Produce the NuGet package |
| `make wasm` | Build the P1 wasm core from the pinned crates.io release |

## WASM Core

The `make wasm` command downloads the pinned `keetanetwork-anchor-client-wasi` release from crates.io, verifies its sha256 checksum, builds it for `wasm32-wasip1`, and places the artifact where the library embeds it as a resource. The version and checksum pins live in [`scripts/build-wasm.sh`](scripts/build-wasm.sh).

## FFI Safety Model

Interop is Wasmtime-hosted WebAssembly, not native P/Invoke. Guest memory is a sandboxed linear buffer. The host and guest share the guest's allocator, so there is no allocator-mismatch or double-free hazard, and no managed memory is pinned across the boundary.

### SDK Rules:

- **Thread-safe by construction.** A `WasmRuntime` owns one Wasmtime `Store`, which cannot be used from more than one thread. The runtime confines it to a dedicated dispatcher thread and serializes every call onto it, so any thread may use the SDK: offline operations dispatch synchronously, networked client operations are `async` and accept a `CancellationToken`.
- **Deterministic disposal.** Handle-backed types (`Account`, `Certificate`, `KycCertificate`, containers, the clients) implement `IDisposable` and release their wasm handle on `Dispose`. Wrap them in `using`. A finalizer backstop reclaims forgotten handles by enqueueing the free onto the dispatcher, but deterministic disposal remains the contract.

## License

See [LICENSE](LICENSE).
