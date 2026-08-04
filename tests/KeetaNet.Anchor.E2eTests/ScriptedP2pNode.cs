using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// A test-controlled WebSocket endpoint for adversarial P2P cases. It hands each
/// accepted connection to the test, which scripts the protocol.
/// </summary>
internal sealed class ScriptedP2pNode : IAsyncDisposable
{
	private readonly WebApplication _app;
	private readonly Channel<ScriptedP2pConnection> _connections =
		Channel.CreateUnbounded<ScriptedP2pConnection>();

	private ScriptedP2pNode(WebApplication app)
	{
		_app = app;
	}

	/// <summary>The advertised ws:// endpoint.</summary>
	public string WsUrl { get; private set; } = "";

	public static async Task<ScriptedP2pNode> Start()
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls("http://127.0.0.1:0");

		WebApplication app = builder.Build();
		var node = new ScriptedP2pNode(app);

		app.UseWebSockets();
		app.Run(async context =>
		{
			if (!context.WebSockets.IsWebSocketRequest)
			{
				context.Response.StatusCode = StatusCodes.Status400BadRequest;
				return;
			}

			using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
			var connection = new ScriptedP2pConnection(socket);
			try
			{
				await connection.ReadGreeting();
				await node._connections.Writer.WriteAsync(connection);
				await connection.Completion;
			}
			catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException)
			{
				// The client dropped the connection. The handler just ends.
			}
		});

		await app.StartAsync();
		string url = app.Urls.First();
		node.WsUrl = url.Replace("http://", "ws://", StringComparison.Ordinal);
		return node;
	}

	/// <summary>Waits for the next client connection, greeting already read.</summary>
	public async Task<ScriptedP2pConnection> NextConnection(CancellationToken cancellationToken)
	{
		return await _connections.Reader.ReadAsync(cancellationToken);
	}

	public async ValueTask DisposeAsync()
	{
		while (_connections.Reader.TryRead(out ScriptedP2pConnection? connection))
		{
			connection.Complete();
		}

		await _app.StopAsync();
		await _app.DisposeAsync();
	}
}

/// <summary>
/// One accepted P2P connection. The test sends protocol messages through it
/// and completes it when done, which lets the server handler end.
/// </summary>
internal sealed class ScriptedP2pConnection
{
	private readonly WebSocket _socket;
	private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal ScriptedP2pConnection(WebSocket socket)
	{
		_socket = socket;
	}

	/// <summary>The client's parsed greeting message.</summary>
	public JsonElement Greeting { get; private set; }

	/// <summary>Held open until the test calls <see cref="Complete"/>.</summary>
	internal Task Completion => _done.Task;

	/// <summary>Reads the first (greeting) text message from the client.</summary>
	internal async Task ReadGreeting()
	{
		byte[] buffer = new byte[64 * 1024];
		using var message = new MemoryStream();
		WebSocketReceiveResult result;
		do
		{
			result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
			message.Write(buffer, 0, result.Count);
		}
		while (!result.EndOfMessage);

		using JsonDocument document = JsonDocument.Parse(message.ToArray());
		Greeting = document.RootElement.Clone();
	}

	/// <summary>Sends one text message to the client.</summary>
	public async Task Send(string json)
	{
		await _socket.SendAsync(
			Encoding.UTF8.GetBytes(json),
			WebSocketMessageType.Text,
			endOfMessage: true,
			CancellationToken.None);
	}

	/// <summary>Releases the server handler, which closes the connection.</summary>
	public void Complete()
	{
		_done.TrySetResult();
	}
}
