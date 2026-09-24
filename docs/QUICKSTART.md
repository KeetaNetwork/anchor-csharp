# Quickstart

## Abstract

This page is the install, build, and first-use path for `KeetaNet.Anchor`. It records the Makefile targets from the repository Makefile. It also states the GitHub Packages gate for the TypeScript harness.

## Purpose

Read this page when you clone the repository or when you install the published packages. After reading you can restore the SDK, build, lint, test, and run two first-use calls.

## Requirements

Match the tip root `README.md` Requirements.

- Install the .NET SDK from [`global.json`](../global.json). The SDK pin is .NET 10. The library targets `net8.0` and `net10.0`.
- Install Rust with the `wasm32-wasip1` target for `make wasm` only (`rustup target add wasm32-wasip1`). NuGet consumers skip Rust. The wasm core ships embedded in the package.
- Install Node.js 20 and a GitHub Packages token for `make test` only. The e2e suite drives the reference TypeScript anchor.

## Install

`PackageId` in `src/KeetaNet.Anchor/KeetaNet.Anchor.csproj` is `KeetaNet.Anchor`. `PackageId` in `src/KeetaNet.Anchor.Extensions.DependencyInjection/KeetaNet.Anchor.Extensions.DependencyInjection.csproj` is `KeetaNet.Anchor.Extensions.DependencyInjection`. Root `nuget.config` uses `https://api.nuget.org/v3/index.json`.

```bash
dotnet add package KeetaNet.Anchor
dotnet add package KeetaNet.Anchor.Extensions.DependencyInjection
```

## Setup and build

The Makefile owns the recipes.

| Target | What you get |
| --- | --- |
| `make developer` | Verify the SDK, restore, build, and run tests |
| `make build` | Build the solution. Build the wasm core first when it is missing |
| `make test` | Run unit and e2e tests with coverage |
| `make do-lint` | Lint with formatting fixes for C#, spelling, and the harness |
| `make pack` | Produce the NuGet packages |
| `make wasm` | Build the P1 wasm core from the pinned crates.io release |
| `make node-harness` | Install and build the TypeScript interop harnesses |

```bash
make developer
```

Use the `Makefile`, not raw `dotnet`, for every task.

## Packages

The unit tests in `tests/KeetaNet.Anchor.Tests` run without GitHub Packages. The e2e suite in `tests/KeetaNet.Anchor.E2eTests` drives the reference TypeScript anchor through `tests/node-harness`.

[CONTRIBUTING](../CONTRIBUTING.md) states that gate.

- Node.js 20.
- Access to the `@keetanetwork` scope on GitHub Packages. `tests/node-harness/.npmrc` routes the scope to `npm.pkg.github.com`. Authenticate with a token that has `read:packages`. Locally use `npm login --registry=https://npm.pkg.github.com`. In CI use `NODE_AUTH_TOKEN`.

`make node-harness` installs and compiles the harness. `make test` runs it.

## Test

```bash
make test
```

```bash
make do-lint
```

## First use

The account path in tip `README.md` Usage and in `tests/KeetaNet.Anchor.Tests/CryptoTests.cs` loads the runtime and derives a signer.

```csharp
using KeetaNet.Anchor;
using KeetaNet.Anchor.Crypto;

using var runtime = WasmRuntime.Load();
string seed = runtime.Accounts.GenerateRandomSeed();
using Account signer = runtime.Accounts.FromSeed(seed, index: 0, algorithm: "ed25519");
```

`WasmRuntime.Load` lives in `src/KeetaNet.Anchor/Interop/WasmRuntime.cs`. `Accounts.FromSeed` lives on `AccountFactory` in `src/KeetaNet.Anchor/Crypto/AccountFactory.cs`. `tests/KeetaNet.Anchor.Tests/CryptoTests.cs` calls `WasmRuntime.Load` and `runtime.Accounts.FromSeed`.

The dependency-injection path in tip `README.md` Usage and in `tests/KeetaNet.Anchor.Tests/DependencyInjectionTests.cs` registers one shared runtime.

```csharp
using Microsoft.Extensions.DependencyInjection;
using KeetaNet.Anchor;

var services = new ServiceCollection();
services.AddKeetaNetAnchor();
using ServiceProvider provider = services.BuildServiceProvider();
WasmRuntime runtime = provider.GetRequiredService<WasmRuntime>();
```

`AddKeetaNetAnchor` lives in `src/KeetaNet.Anchor.Extensions.DependencyInjection/KeetaNetAnchorServiceCollectionExtensions.cs`. That call requires the `KeetaNet.Anchor.Extensions.DependencyInjection` package.
