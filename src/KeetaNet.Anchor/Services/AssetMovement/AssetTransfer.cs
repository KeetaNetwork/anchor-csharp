using System.Text.Json;

namespace KeetaNet.Anchor;

/// <summary>
/// A simulated transfer: the instruction choices a caller can inspect, plus the
/// fluent step to promote the simulation into a real, managed transfer with the
/// same provider and request.
/// </summary>
public sealed class AssetSimulatedTransfer
{
	private readonly AssetMovementClient _client;
	private readonly AssetProvider _provider;
	private readonly AssetTransferRequest _request;

	internal AssetSimulatedTransfer(
		AssetMovementClient client,
		AssetProvider provider,
		AssetTransferRequest request,
		IReadOnlyList<JsonElement> instructionChoices)
	{
		_client = client;
		_provider = provider;
		_request = request;
		InstructionChoices = instructionChoices;
	}

	/// <summary>The candidate instructions that would complete this transfer.</summary>
	public IReadOnlyList<JsonElement> InstructionChoices { get; }

	/// <summary>
	/// Initiate the simulated transfer with the same provider and request.
	/// A simulation may omit the recipient. Supply <paramref name="recipient"/>
	/// here to complete the destination before initiating.
	/// </summary>
	public Task<AssetTransfer> CreateTransfer(
		object? recipient = null,
		string? depositMessage = null,
		CancellationToken cancellationToken = default)
	{
		AssetTransferDestination to = _request.To with
		{
			Recipient = recipient ?? _request.To.Recipient,
			DepositMessage = depositMessage ?? _request.To.DepositMessage,
		};
		AssetTransferRequest request = _request with { To = to };

		return _client.InitiateTransfer(_provider, request, cancellationToken);
	}
}

/// <summary>
/// An initiated transfer bound to its provider: inspect the instruction
/// choices, poll its status, or execute a chosen pull instruction, without
/// re-threading the provider or id.
/// </summary>
public sealed class AssetTransfer
{
	private readonly AssetMovementClient _client;
	private readonly AssetProvider _provider;

	internal AssetTransfer(
		AssetMovementClient client,
		AssetProvider provider,
		string id,
		IReadOnlyList<JsonElement> instructionChoices)
	{
		_client = client;
		_provider = provider;
		Id = id;
		InstructionChoices = instructionChoices;
	}

	/// <summary>The transfer id assigned by the provider.</summary>
	public string Id { get; }

	/// <summary>The candidate instructions that complete this transfer.</summary>
	public IReadOnlyList<JsonElement> InstructionChoices { get; }

	/// <summary>Read this transfer's current status.</summary>
	public Task<AssetTransferStatus> GetTransferStatus(CancellationToken cancellationToken = default) =>
		_client.GetTransferStatus(_provider, Id, cancellationToken);

	/// <summary>Execute a fiat pull <paramref name="instruction"/> for this transfer.</summary>
	public Task<AssetTransferStatus> ExecuteTransfer(
		AssetPullInstruction instruction,
		CancellationToken cancellationToken = default)
	{
		var request = new AssetExecuteRequest(Id, instruction);
		return _client.ExecuteTransfer(_provider, request, cancellationToken);
	}
}
