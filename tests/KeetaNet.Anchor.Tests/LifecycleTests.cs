using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// Wrapper and runtime lifecycles: disposed wrappers refuse use, disposal is
/// idempotent in any order, and the finalizer backstop reclaims a handle whose
/// <c>Dispose</c> was forgotten.
/// </summary>
public sealed class LifecycleTests
{
	private static readonly string[] Countries = { "US" };

	[Fact]
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP016:Don't use disposed instance",
		Justification = "Use after dispose is exactly the behavior under test; it must throw ObjectDisposedException.")]
	public void DisposedWrapperRefusesUse()
	{
		using var runtime = WasmRuntime.Load();
		Account account = Account.FromSeed(runtime, TestSeeds.Subject, 0, "ed25519");

		account.Dispose();

		Assert.Throws<ObjectDisposedException>(() => account.Address);
		Assert.Throws<ObjectDisposedException>(() => account.Sign(new byte[] { 1 }));
	}

	[Fact]
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP016:Don't use disposed instance",
		Justification = "Double dispose is exactly the behavior under test; Dispose must be idempotent.")]
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using",
		Justification = "Explicit Dispose calls are the behavior under test.")]
	public void DoubleDisposeIsIdempotent()
	{
		var runtime = WasmRuntime.Load();
		Account account = Account.FromSeed(runtime, TestSeeds.Subject, 0, "ed25519");

		account.Dispose();
		account.Dispose();
		runtime.Dispose();
		runtime.Dispose();
	}

	[Fact]
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP016:Don't use disposed instance",
		Justification = "Use after runtime disposal is exactly the behavior under test.")]
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using",
		Justification = "The runtime must be disposed before its wrapper to exercise the ordering.")]
	public void WrapperOutlivingRuntimeStaysSafe()
	{
		var runtime = WasmRuntime.Load();
		using Account account = Account.FromSeed(runtime, TestSeeds.Subject, 0, "ed25519");

		runtime.Dispose();

		// Use refuses cleanly, and the wrapper's own dispose (via using) no-ops.
		Assert.Throws<ObjectDisposedException>(() => account.Address);
	}

	[Fact]
	public async Task ClientOutlivingRuntimeRefusesDispatch()
	{
		var runtime = WasmRuntime.Load();
		using Account account = Account.FromSeed(runtime, TestSeeds.Subject, 0, "ed25519");
		using KycClient client = KycClient.WithAccount(runtime, "http://127.0.0.1:1", account.Address, account);

		runtime.Dispose();

		await Assert.ThrowsAsync<ObjectDisposedException>(
			() => client.ProvidersAsync(Countries, TestContext.Current.CancellationToken));
	}

	[Fact]
	public void FinalizerBackstopReclaimsForgottenHandle()
	{
		using var runtime = WasmRuntime.Load();

		LeakAccount(runtime);
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

#if DEBUG
		Assert.Equal(0, runtime.OutstandingHandles);
#endif

		// The queued backstop free must leave the dispatcher healthy.
		using Account survivor = Account.FromSeed(runtime, TestSeeds.Subject, 1, "ed25519");
		Assert.StartsWith("keeta_", survivor.Address, StringComparison.Ordinal);
	}

#if DEBUG
	[Fact]
	public void LeakCounterFlagsForgottenDispose()
	{
		using var runtime = WasmRuntime.Load();
		Account tracked = Account.FromSeed(runtime, TestSeeds.Subject, 0, "ed25519");

		Assert.Equal(1, runtime.OutstandingHandles);

		tracked.Dispose();
		Assert.Equal(0, runtime.OutstandingHandles);
	}
#endif

	/// <summary>
	/// Create an account and drop it undisposed, in a non-inlined frame so the
	/// JIT cannot keep the reference alive past the collection below.
	/// </summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created",
		Justification = "Deliberately leaked to exercise the finalizer backstop.")]
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP004:Don't ignore created IDisposable",
		Justification = "Deliberately leaked to exercise the finalizer backstop.")]
	private static void LeakAccount(WasmRuntime runtime)
	{
		Account leaked = Account.FromSeed(runtime, TestSeeds.Subject, 0, "ed25519");
		Assert.StartsWith("keeta_", leaked.Address, StringComparison.Ordinal);
	}
}
