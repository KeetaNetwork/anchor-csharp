# anchor-csharp

C# SDK for the KeetaNet anchor. The client logic runs inside a sandboxed WebAssembly core (`keetanetwork_anchor_client_wasi.wasm`, built from [anchor-rs](https://github.com/KeetaNetwork/anchor-rs)) hosted in-process by [Wasmtime](https://github.com/bytecodealliance/wasmtime-dotnet).

## Packages

| Package | Description |
| --- | --- |
| `KeetaNet.Anchor` | KYC and asset-movement clients, crypto, certificates, and containers over the anchor wasm core. |
| `KeetaNet.Anchor.Extensions.DependencyInjection` | `Microsoft.Extensions.DependencyInjection` integration: registers the shared runtime singleton. |

## Requirements

- .NET SDK per [`global.json`](global.json) (.NET 10 - the library targets `net8.0` and `net10.0`).
- Rust with the `wasm32-wasip1` target, for `make wasm` only (`rustup target add wasm32-wasip1`). NuGet consumers do not need Rust: the wasm core ships embedded in the package.
- Node.js 20 and a GitHub Packages token, for `make test` only (the e2e suite drives the reference TypeScript anchor; see [CONTRIBUTING](CONTRIBUTING.md)).

## Quick Start

```sh
make developer   # verify SDK, restore, build, run tests
make help        # list all targets
```

Use the `Makefile`, not raw `dotnet`, for every task:

| Command | Purpose |
| --- | --- |
| `make build` | Build the solution (builds the wasm core first if missing) |
| `make test` | Run all tests with coverage: unit + e2e against the live TypeScript anchor |
| `make node-harness` | Install + build the TypeScript interop harnesses |
| `make do-lint` | Lint with formatting fixes (C#, spelling, harness) |
| `make do-lint-ci` | Lint for CI (check only, no fixes) |
| `make pack` | Produce the NuGet package |
| `make wasm` | Build the P1 wasm core from the pinned crates.io release |

## Usage

### The Runtime

Everything starts from a `WasmRuntime`, which loads the embedded wasm core and owns the dispatcher thread all calls serialize onto. Create one per application and dispose it last: every object below is created from its factories and borrows it.

The runtime is thread-safe. Non-network operations (crypto, certificates, containers) dispatch synchronously. Networked client operations are `async`, accept a `CancellationToken`, and honor it before dispatch and during host HTTP and sleeps.

```csharp
using KeetaNet.Anchor;
using KeetaNet.Anchor.Crypto;

using var runtime = WasmRuntime.Load();
```

The runtime exposes one factory per domain - `Accounts`, `Certificates`, `KycCertificates`, `Containers`, `Sharables` - plus `CreateKycClient` and `CreateAssetMovementClient` for the networked clients. Every handle-backed object they create implements `IDisposable`. Wrap them in `using` and dispose them before the runtime.

In an application using `Microsoft.Extensions.DependencyInjection`, register the runtime once instead of threading it around; the container owns its disposal:

```csharp
// Program.cs - requires the KeetaNet.Anchor.Extensions.DependencyInjection package.
builder.Services.AddKeetaNetAnchor();

// Any service: inject the singleton and create what you need per use.
public sealed class Onboarding(WasmRuntime runtime)
{
	public string NewSignerPublicKeyString()
	{
		string seed = runtime.Accounts.GenerateRandomSeed();
		using Account signer = runtime.Accounts.FromSeed(seed, index: 0, algorithm: "ed25519");
		return signer.PublicKeyString;
	}
}
```

### Accounts

An `Account` is a signer derived from a seed, private key, or BIP39 passphrase, or a read-only account built from a public-key string or raw public key. Key material never leaves the wasm core. Supported algorithms: `ed25519`, `ecdsa_secp256k1`, `ecdsa_secp256r1`.

```csharp
// Derive a signer from a fresh seed.
string seed = runtime.Accounts.GenerateRandomSeed();
using Account signer = runtime.Accounts.FromSeed(seed, index: 0, algorithm: "ed25519");

Console.WriteLine(signer.PublicKeyString);   // keeta_...
Console.WriteLine(signer.PublicKeyAndType); // type-prefixed hex

// A watch-only view of the same identity, from the transport key.
using Account watchOnly = runtime.Accounts.FromPublicKeyAndType(signer.PublicKeyAndType);

// Sign and verify.
byte[] message = "attest this"u8.ToArray();
byte[] signature = signer.Sign(message);
bool verified = signer.Verify(message, signature);

// Asymmetric encryption to the account's key.
byte[] ciphertext = signer.Encrypt("for your eyes"u8.ToArray());
byte[] plaintext = signer.Decrypt(ciphertext);

// A read-only account verifies and encrypts but cannot sign or decrypt.
using Account watcher = runtime.Accounts.FromPublicKeyString(signer.PublicKeyString);
```

### KYC Verification

`KycClient` discovers providers from on-chain service metadata (read through a node API), then drives a verification end to end. Requests are signed by the bound account. Discovery, signing, retries, and polling all run inside the core.

Provider results use the pending-or-ready shape: `Ready` carries the value, otherwise `RetryAfterMs` says when to ask again.

```csharp
using KycClient kyc = runtime.CreateKycClient(nodeUrl, root, signer);

string[] countries = { "US" };
IReadOnlyList<KycProvider> providers = await kyc.GetProviders(countries, cancellationToken);
KycProvider provider = providers[0];

// Start a verification. The user completes it in a browser at WebUrl.
VerificationOutcome created = await kyc.StartVerification(provider, countries, cancellationToken: cancellationToken);
Verification verification = created.Ready!;
Console.WriteLine(verification.WebUrl);
Console.WriteLine(verification.ExpectedCost.Token);

// Poll the provider's decision.
StatusOutcome status = await kyc.GetVerificationStatus(provider, verification.Id, cancellationToken);

// Fetch the issued chain once ready; a pending fetch reports RetryAfterMs instead.
CertificatesOutcome outcome = await kyc.GetCertificates(provider, verification.Id, cancellationToken);
Certificates issued = outcome.Ready!;
string leafPem = issued.Results[0].Value;
```

### Reading KYC Certificates

A `KycCertificate` is an issued leaf: a base X.509 certificate plus KYC attributes, some plain and some encrypted to the subject. Verify it against the provider's CA, then read attributes with the subject account.

```csharp
using KycCertificate leaf = runtime.KycCertificates.Parse(leafPem);
using Certificate providerCa = kyc.GetCA(provider);

bool trusted = leaf.Verify(
	trustedRoots: new[] { providerCa },
	intermediates: Array.Empty<Certificate>(),
	moment: DateTimeOffset.UtcNow);

// List what the leaf carries.
foreach (string name in leaf.GetAttributeNames())
{
	Console.WriteLine(name);
}

// GetAttributeBuffer returns the undecoded semantic bytes. GetAttribute wraps
// them in a typed box; pick the accessor matching the attribute's shape.
// Sensitive attributes decrypt with the subject account; plain ones use the
// overloads without an account.
byte[] rawEmail = leaf.GetAttributeBuffer("email", subject);
string fullName = leaf.GetAttribute("fullName", subject).AsText();
DateTimeOffset birthDate = leaf.GetAttribute("dateOfBirth", subject).AsTimestamp();
JsonElement address = leaf.GetAttribute("address", subject).AsJson();
```

### Selective Disclosure with Proofs

A holder can attest to one sensitive attribute without revealing the private key. `GetProof` decrypts the attribute and produces an `AttributeProof`. Anyone holding the leaf validates it with only the subject's public key.

```csharp
// Holder: decrypt and prove one attribute.
AttributeProof proof = leaf.GetProof("email", subject);

// Verifier: a read-only subject account suffices.
using Account subjectPublic = runtime.Accounts.FromPublicKeyString(subjectPublicKeyString);
bool attested = leaf.ValidateProof("email", subjectPublic, proof);
```

### Issuing KYC Certificates

`KycCertificateBuilder` issues a signed leaf directly: set the subject (sensitive attributes encrypt to its key), the issuer (signs), a validity window, and the attributes. A read-only subject account suffices; only the issuer needs a private key.

```csharp
var validFrom = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
var validTo = validFrom.AddYears(1);

using KycCertificate issued = runtime.KycCertificates.Builder()
	.Subject(subject)
	.Issuer(issuer)
	.IssuerName("Example CA")
	.Serial(42)
	.Validity(validFrom, validTo)
	.SetAttribute("fullName", sensitive: true, "Jane Doe")
	.SetAttribute("dateOfBirth", sensitive: true, new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero))
	.Build();

string pem = issued.ToPem();
```

### Sharable Attribute Bundles

`SharableCertificateAttributes` re-packages a chosen subset of a leaf's attributes for a third party: the subject proves or copies each named attribute, seals the bundle, and grants recipients. The recipient opens it without the subject's key and reads only the disclosed attributes.

```csharp
// Subject: disclose two attributes and grant the recipient.
using SharableCertificateAttributes bundle = runtime.Sharables.FromCertificate(
	leaf, subject, names: new[] { "email", "fullName" });
bundle.GrantAccess(new[] { recipient });
string envelope = bundle.ToPem();

// Recipient: open with their own account and read the disclosed values.
using SharableCertificateAttributes opened =
	runtime.Sharables.FromPem(envelope, new[] { recipient });

IReadOnlyList<string> disclosed = opened.GetAttributeNames();
byte[]? email = opened.GetAttributeBuffer("email"); // null when not disclosed
using KycCertificate embeddedLeaf = opened.GetCertificate();
```

### Encrypted Containers

`EncryptedContainer` is a hybrid-encrypted, optionally signed blob: sealed to a set of principal accounts, with a detached signature over the compressed payload. Use it to move arbitrary bytes between accounts.

```csharp
byte[] payload = "the payload"u8.ToArray();

// Seal to the recipient and sign as the sender.
using EncryptedContainer container = runtime.Containers.FromPlaintext(
	payload, principals: new[] { recipient }, signer: signer);
byte[] blob = container.GetEncoded();

// Recipient: open, verify, and identify the signer.
using EncryptedContainer opened = runtime.Containers.FromEncrypted(blob, new[] { recipient });
byte[] received = opened.GetPlaintext();
bool signatureValid = opened.VerifySignature();
byte[]? signerKey = opened.GetSigningAccount(); // type-prefixed public key, null when unsigned
```

### Asset Movement

`AssetMovementClient` discovers asset-movement providers and drives transfers, persistent forwarding, and KYC sharing. Simulated and initiated transfers return fluent objects bound to their provider and id.

```csharp
using AssetMovementClient assets = runtime.CreateAssetMovementClient(nodeUrl, root, signer);

// Discovery: all providers, by id, by signer account, or by transfer shape.
IReadOnlyList<AssetProvider> providers = await assets.GetProviders(cancellationToken);
var search = new AssetProviderSearch(Asset: asset, From: "chain:evm:100", To: "chain:keeta:100");
IReadOnlyList<AssetProvider> capable = await assets.GetProvidersForTransfer(search, cancellationToken);
AssetProvider provider = capable[0];

// Check the signer's readiness before transacting.
AssetAccountStatus account = await assets.GetAccountStatus(provider, cancellationToken);

// Push transfer: simulate first, then promote the simulation.
var request = new AssetTransferRequest(
	Asset: asset,
	From: new AssetTransferSource("chain:evm:100"),
	To: new AssetTransferDestination("chain:keeta:100", recipientAddress),
	Value: "100");
AssetSimulatedTransfer simulated = await assets.SimulateTransfer(provider, request, cancellationToken);
AssetTransfer transfer = await simulated.CreateTransfer(cancellationToken: cancellationToken);
AssetTransferStatus status = await transfer.GetTransferStatus(cancellationToken);

// Pull transfer (fiat rails): initiate, pick an instruction, execute.
AssetTransfer pull = await assets.InitiateTransfer(provider, pullRequest, cancellationToken);
var instruction = new AssetPullInstruction("ACH_DEBIT", pull.InstructionChoices[0].GetProperty("pullFrom"));
AssetTransferStatus executed = await pull.ExecuteTransfer(instruction, cancellationToken);

// Share KYC attributes; a pending outcome polls its promise URL inside the core.
AssetShareKycOutcome shared = await assets.ShareKycAttributesAndWait(
	provider,
	new AssetShareKycRequest(exportedAttributes),
	pollInterval: TimeSpan.FromSeconds(1),
	timeout: TimeSpan.FromMinutes(2),
	cancellationToken: cancellationToken);
```

Persistent forwarding follows the same pattern: `InitiatePersistentForwardingTemplate` / `CreatePersistentForwardingTemplate` / `CreatePersistentForwardingAddress` create, the `List*` methods page, and the `Deactivate*` methods retire.

### Errors

Every failure surfaced from the core throws `KeetaException`: a stable machine-readable `Code` plus a human-readable message. Argument misuse throws the usual .NET exception types (`ArgumentException`, `InvalidOperationException`, `ObjectDisposedException`).

```csharp
try
{
	await assets.InitiateTransfer(provider, request, cancellationToken);
}
catch (KeetaException error)
{
	Console.WriteLine($"{error.Code}: {error.Message}");
}
```

## WASM Core

The `make wasm` command downloads the pinned [`keetanetwork-anchor-client-wasi`](https://crates.io/crates/keetanetwork-anchor-client-wasi) release (published from [anchor-rs](https://github.com/KeetaNetwork/anchor-rs)) from crates.io, verifies its sha256 checksum, builds it for `wasm32-wasip1`, and places the artifact where the library embeds it as a resource. The version and checksum pins live in [`scripts/build-wasm.sh`](scripts/build-wasm.sh).

## FFI Safety Model

Interop is Wasmtime-hosted WebAssembly, not native P/Invoke. Guest memory is a sandboxed linear buffer. The host and guest share the guest's allocator, so there is no allocator-mismatch or double-free hazard, and no managed memory is pinned across the boundary.

### SDK Rules:

- **Thread-safe by construction.** A `WasmRuntime` owns one Wasmtime `Store`, which cannot be used from more than one thread. The runtime confines it to a dedicated dispatcher thread and serializes every call onto it, so any thread may use the SDK: offline operations dispatch synchronously, networked client operations are `async` and accept a `CancellationToken`.
- **Deterministic disposal.** Handle-backed types (`Account`, `Certificate`, `KycCertificate`, containers, the clients) implement `IDisposable` and release their wasm handle on `Dispose`. Wrap them in `using`.

## License

See [LICENSE](LICENSE).
