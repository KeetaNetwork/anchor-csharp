# anchor-csharp

C# SDK for the KeetaNet anchor. The client logic runs inside a sandboxed WebAssembly core (`keetanetwork_anchor_client_wasi.wasm`, built from [anchor-rs](https://github.com/KeetaNetwork/anchor-rs)) hosted in-process by [Wasmtime](https://github.com/bytecodealliance/wasmtime-dotnet).

## Packages

| Package | Description |
| --- | --- |
| `KeetaNet.Anchor` | KYC and asset-movement clients, crypto, certificates, and containers over the anchor wasm core. |

## Requirements

- .NET SDK per [`global.json`](global.json) (.NET 10 - the library targets `net8.0` and `net10.0`).
- Rust with the `wasm32-wasip1` target, for `make wasm` only (`rustup target add wasm32-wasip1`). NuGet consumers do not need Rust: the wasm core ships embedded in the package.
- Node.js 20 and a GitHub Packages token, for `make test` only (the e2e suite drives the reference TypeScript anchor; see [CONTRIBUTING](CONTRIBUTING.md)).

## Quick start

```sh
make developer   # verify SDK, restore, build, run tests
make help        # list all targets
```

Use the `Makefile`, not raw `dotnet`, for every task:

| Command | Purpose |
| --- | --- |
| `make build` | Build the solution (builds the wasm core first if missing) |
| `make test` | Run all tests: unit + e2e against the live TypeScript anchor |
| `make node-harness` | Install + build the TypeScript interop harnesses |
| `make coverage` | Run unit tests with code coverage |
| `make lint` | Verify formatting, spelling, and the harness lint |
| `make format` | Apply formatting fixes |
| `make pack` | Produce the NuGet package |
| `make wasm` | Build the P1 wasm core from the pinned crates.io release |

## Usage

### The runtime

Everything starts from a `WasmRuntime`, which loads the embedded wasm core and owns the dispatcher thread all calls serialize onto. Create one per application and dispose it last: every object below borrows it.

The runtime is thread-safe. Non-network operations (crypto, certificates, containers) dispatch synchronously. Networked client operations are `async`, accept a `CancellationToken`, and honor it before dispatch and during host HTTP and sleeps.

```csharp
using KeetaNet.Anchor;
using KeetaNet.Anchor.Crypto;

using var runtime = WasmRuntime.Load();
```

Every handle-backed type (`Account`, certificates, containers, clients) implements `IDisposable`. Wrap them in `using` and dispose them before the runtime.

### Accounts

An `Account` is a signer derived from a seed, private key, or BIP39 passphrase, or a read-only account built from an address or public key. Key material never leaves the wasm core. Supported algorithms: `ed25519`, `ecdsa_secp256k1`, `ecdsa_secp256r1`.

```csharp
// Derive a signer from a fresh seed.
string seed = Account.GenerateSeed(runtime);
using Account signer = Account.FromSeed(runtime, seed, index: 0, algorithm: "ed25519");

Console.WriteLine(signer.Address);   // keeta_...
Console.WriteLine(signer.PublicKey); // type-prefixed hex

// Sign and verify.
byte[] message = "attest this"u8.ToArray();
byte[] signature = signer.Sign(message);
bool verified = signer.Verify(message, signature);

// Asymmetric encryption to the account's key.
byte[] ciphertext = signer.Encrypt("for your eyes"u8.ToArray());
byte[] plaintext = signer.Decrypt(ciphertext);

// A read-only account verifies and encrypts but cannot sign or decrypt.
using Account watcher = Account.FromAddress(runtime, signer.Address);
```

### KYC verification flow

`KycClient` discovers providers from on-chain service metadata (read through a node API), then drives a verification end to end. Requests are signed by the bound account. Discovery, signing, retries, and polling all run inside the core.

Provider results use the pending-or-ready shape: `Ready` carries the value, otherwise `RetryAfterMs` says when to ask again.

```csharp
using KycClient kyc = KycClient.WithAccount(runtime, nodeUrl, root, signer);

string[] countries = { "US" };
IReadOnlyList<KycProvider> providers = await kyc.ProvidersAsync(countries, cancellationToken);
KycProvider provider = providers[0];

// Start a verification. The user completes it in a browser at WebUrl.
VerificationOutcome created = await kyc.CreateVerificationAsync(provider, countries, cancellationToken: cancellationToken);
Verification verification = created.Ready!;
Console.WriteLine(verification.WebUrl);
Console.WriteLine(verification.ExpectedCost.Token);

// Poll the provider's decision.
StatusOutcome status = await kyc.GetVerificationStatusAsync(provider, verification.Id, cancellationToken);

// Fetch the issued chain once ready; a pending fetch reports RetryAfterMs instead.
CertificatesOutcome outcome = await kyc.GetCertificatesAsync(provider, verification.Id, cancellationToken);
Certificates issued = outcome.Ready!;
string leafPem = issued.Results[0].Value;
```

### Reading KYC certificates

A `KycCertificate` is an issued leaf: a base X.509 certificate plus KYC attributes, some plain and some encrypted to the subject. Verify it against the provider's CA, then read attributes with the subject account.

```csharp
using KycCertificate leaf = KycCertificate.Parse(runtime, leafPem);
using Certificate providerCa = kyc.ProviderCertificate(provider);

bool trusted = leaf.Verify(
	trustedRoots: new[] { providerCa },
	intermediates: Array.Empty<Certificate>(),
	moment: DateTimeOffset.UtcNow);

// List what the leaf carries.
foreach (KycAttribute attribute in leaf.Attributes())
{
	Console.WriteLine($"{attribute.Name} (sensitive: {attribute.Sensitive})");
}

// Scalars and dates decode as text; structured attributes decode as JSON.
// Sensitive attributes decrypt with the subject account; plain ones use the
// overloads without an account.
string fullName = leaf.GetText("fullName", subject);
string birthDate = leaf.GetText("dateOfBirth", subject); // ISO-8601 timestamp
JsonElement address = leaf.GetJson("address", subject);
```

### Selective disclosure with proofs

A holder can attest to one sensitive attribute without revealing the private key. `Prove` decrypts the attribute and produces an `AttributeProof`. Anyone holding the leaf validates it with only the subject's public key.

```csharp
// Holder: decrypt and prove one attribute.
AttributeProof proof = leaf.Prove("email", subject);

// Verifier: a read-only subject account suffices.
using Account subjectPublic = Account.FromAddress(runtime, subjectAddress);
bool attested = leaf.ValidateProof("email", subjectPublic, proof);
```

### Issuing KYC certificates

`KycCertificateBuilder` issues a signed leaf directly: set the subject (sensitive attributes encrypt to its key), the issuer (signs), a validity window, and the attributes. A read-only subject account suffices; only the issuer needs a private key.

```csharp
var validFrom = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
var validTo = validFrom.AddYears(1);

using KycCertificate issued = KycCertificate.Builder(runtime)
	.Subject(subject)
	.Issuer(issuer)
	.IssuerName("Example CA")
	.Serial(42)
	.Validity(validFrom, validTo)
	.SetAttribute("fullName", sensitive: true, "Jane Doe")
	.SetAttribute("dateOfBirth", sensitive: true, new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero))
	.Issue();

string pem = issued.Pem();
```

### Sharable attribute bundles

`SharableCertificateAttributes` re-packages a chosen subset of a leaf's attributes for a third party: the subject proves or copies each named attribute, seals the bundle, and grants recipients. The recipient opens it without the subject's key and reads only the disclosed attributes.

```csharp
// Subject: disclose two attributes and grant the recipient.
using SharableCertificateAttributes bundle = SharableCertificateAttributes.FromCertificate(
	runtime, leaf, subject, names: new[] { "email", "fullName" });
bundle.GrantAccess(new[] { recipient });
string envelope = bundle.ToPem();

// Recipient: open with their own account and read the disclosed values.
using SharableCertificateAttributes opened =
	SharableCertificateAttributes.FromPem(runtime, envelope, new[] { recipient });

IReadOnlyList<string> disclosed = opened.AttributeNames();
byte[]? email = opened.AttributeBuffer("email"); // null when not disclosed
using KycCertificate embeddedLeaf = opened.LeafCertificate();
```

### Encrypted containers

`EncryptedContainer` is a hybrid-encrypted, optionally signed blob: sealed to a set of principal accounts, with a detached signature over the compressed payload. Use it to move arbitrary bytes between accounts.

```csharp
byte[] payload = "the payload"u8.ToArray();

// Seal to the recipient and sign as the sender.
using EncryptedContainer container = EncryptedContainer.FromPlaintext(
	runtime, payload, principals: new[] { recipient }, signer: signer);
byte[] blob = container.Encoded();

// Recipient: open, verify, and identify the signer.
using EncryptedContainer opened = EncryptedContainer.FromEncrypted(runtime, blob, new[] { recipient });
byte[] received = opened.Plaintext();
bool signatureValid = opened.VerifySignature();
byte[]? signerKey = opened.SigningAccount(); // type-prefixed public key, null when unsigned
```

### Asset movement

`AssetMovementClient` discovers asset-movement providers and drives transfers, persistent forwarding, and KYC sharing. Simulated and initiated transfers return fluent objects bound to their provider and id.

```csharp
using AssetMovementClient assets = AssetMovementClient.WithAccount(runtime, nodeUrl, root, signer);

// Discovery: all providers, by id, by signer account, or by transfer shape.
IReadOnlyList<AssetProvider> providers = await assets.ProvidersAsync(cancellationToken);
var search = new AssetProviderSearch(Asset: asset, From: "chain:evm:100", To: "chain:keeta:100");
IReadOnlyList<AssetProvider> capable = await assets.GetProvidersForTransferAsync(search, cancellationToken);
AssetProvider provider = capable[0];

// Check the signer's readiness before transacting.
AssetAccountStatus account = await assets.AccountStatusAsync(provider, cancellationToken);

// Push transfer: simulate first, then promote the simulation.
var request = new AssetTransferRequest(
	Asset: asset,
	From: new AssetTransferSource("chain:evm:100"),
	To: new AssetTransferDestination("chain:keeta:100", recipientAddress),
	Value: "100");
AssetSimulatedTransfer simulated = await assets.SimulateTransferAsync(provider, request, cancellationToken);
AssetTransfer transfer = await simulated.CreateTransferAsync(cancellationToken: cancellationToken);
AssetTransferStatus status = await transfer.GetStatusAsync(cancellationToken);

// Pull transfer (fiat rails): initiate, pick an instruction, execute.
AssetTransfer pull = await assets.InitiateTransferAsync(provider, pullRequest, cancellationToken);
var instruction = new AssetPullInstruction("ACH_DEBIT", pull.InstructionChoices[0].GetProperty("pullFrom"));
AssetTransferStatus executed = await pull.ExecuteAsync(instruction, cancellationToken);

// Share KYC attributes; a pending outcome polls its promise URL inside the core.
AssetShareKycOutcome shared = await assets.ShareKycAndWaitAsync(
	provider,
	new AssetShareKycRequest(exportedAttributes),
	pollInterval: TimeSpan.FromSeconds(1),
	timeout: TimeSpan.FromMinutes(2),
	cancellationToken: cancellationToken);
```

Persistent forwarding follows the same pattern: `InitiateForwardingTemplateAsync` / `CreateForwardingTemplateAsync` / `CreateForwardingAddressAsync` create, the `List*Async` methods page, and the `Deactivate*Async` methods retire.

### Errors

Every failure surfaced from the core throws `KeetaException`: a stable machine-readable `Code` plus a human-readable message. Argument misuse throws the usual .NET exception types (`ArgumentException`, `InvalidOperationException`, `ObjectDisposedException`).

```csharp
try
{
	await assets.InitiateTransferAsync(provider, request, cancellationToken);
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
- **Deterministic disposal.** Handle-backed types (`Account`, `Certificate`, `KycCertificate`, containers, the clients) implement `IDisposable` and release their wasm handle on `Dispose`. Wrap them in `using`. A finalizer backstop reclaims forgotten handles by enqueueing the free onto the dispatcher, but deterministic disposal remains the contract.

## License

See [LICENSE](LICENSE).
