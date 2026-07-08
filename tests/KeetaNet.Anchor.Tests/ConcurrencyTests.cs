using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using KeetaNet.Anchor.Crypto;
using Xunit;

namespace KeetaNet.Anchor.Tests;

/// <summary>
/// One runtime, many threads: calls from arbitrary thread-pool threads must
/// serialize onto the dispatcher and produce the same results as sequential
/// use, and networked operations must honor cancellation.
/// </summary>
public sealed class ConcurrencyTests
{
	private static readonly string[] Countries = { "US" };

	[Fact]
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using",
		Justification = "Task.WhenAll completes every task before the using scope ends.")]
	public async Task ParallelSignAndVerifyFromThreadPoolThreadsStayConsistent()
	{
		using var runtime = WasmRuntime.Load();
		using Account account = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		string expectedAddress = account.PublicKeyString;

		Task<bool>[] work = Enumerable.Range(0, 32)
			.Select(index => Task.Run(() => SignRoundTrips(account, expectedAddress, index)))
			.ToArray();

		bool[] outcomes = await Task.WhenAll(work);
		Assert.All(outcomes, outcome => Assert.True(outcome));
	}

	private static bool SignRoundTrips(Account account, string expectedAddress, int index)
	{
		byte[] message = BitConverter.GetBytes(index);
		byte[] signature = account.Sign(message);
		bool valid = account.Verify(message, signature);
		bool addressStable = account.PublicKeyString == expectedAddress;

		return valid && addressStable;
	}

	[Fact]
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using",
		Justification = "Task.WhenAll completes every task before the using scope ends.")]
	public async Task ParallelAccountLifecyclesYieldIndependentResults()
	{
		using var runtime = WasmRuntime.Load();

		Task<string>[] work = Enumerable.Range(0, 16)
			.Select(index => Task.Run(() => DeriveAddress(runtime, (uint)index)))
			.ToArray();

		string[] addresses = await Task.WhenAll(work);
		Assert.Equal(16, addresses.Distinct().Count());
	}

	private static string DeriveAddress(WasmRuntime runtime, uint index)
	{
		using Account account = runtime.Accounts.FromSeed(TestSeeds.Subject, index, TestSeeds.DefaultAlgorithm);
		return account.PublicKeyString;
	}

	[Fact]
	public async Task PreCanceledTokenCancelsWithoutDispatching()
	{
		using var runtime = WasmRuntime.Load();
		using Account account = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using KycClient client = runtime.CreateKycClient(TestSeeds.NonRoutableAnchor, account.PublicKeyString, account);

		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => client.GetProviders(Countries, cancellation.Token));
	}

	[Fact]
	public async Task CancellationDuringHostHttpSurfacesAsCanceled()
	{
		// A listener that never accepts: the connection completes in the backlog
		// and the HTTP response never arrives, so only cancellation can end the call.
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		int port = ((IPEndPoint)listener.LocalEndpoint).Port;

		using var runtime = WasmRuntime.Load();
		using Account account = runtime.Accounts.FromSeed(TestSeeds.Subject, 0, TestSeeds.DefaultAlgorithm);
		using KycClient client = runtime.CreateKycClient($"http://127.0.0.1:{port}", account.PublicKeyString, account);

		using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => client.GetProviders(Countries, cancellation.Token));
	}
}
