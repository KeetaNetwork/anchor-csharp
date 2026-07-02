using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeetaNet.Anchor.E2eTests;

/// <summary>
/// A TypeScript harness entry (<c>tests/node-harness/dist/&lt;entry&gt;.js</c>)
/// driven over the JSON-lines protocol.
/// </summary>
internal sealed class NodeHarness : IDisposable
{
	private readonly Process _process;

	private NodeHarness(Process process) => _process = process;

	/// <summary>Spawn the compiled <paramref name="entry"/> harness and wait for its ready line.</summary>
	public static NodeHarness Spawn(string entry)
	{
		string script = Path.Combine(DistDirectory(), $"{entry}.js");
		if (!File.Exists(script))
		{
			throw new InvalidOperationException($"harness script not found at {script}; run `make node-harness`");
		}

		var start = new ProcessStartInfo("node")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
		};
		start.ArgumentList.Add(script);

		Process process = Process.Start(start) ?? throw new InvalidOperationException("the node harness process could not be started");

		var harness = new NodeHarness(process);
		try
		{
			JsonElement ready = harness.ReadResponse("start");
			string? announced = ready.GetProperty("event").GetString();
			if (announced != "ready")
			{
				throw new InvalidOperationException($"the harness announced `{announced}` instead of `ready`");
			}
		}
		catch
		{
			harness.Dispose();
			throw;
		}

		return harness;
	}

	/// <summary>Send <paramref name="command"/> with optional arguments and return its response.</summary>
	public JsonElement Request(string command, JsonObject? arguments = null)
	{
		JsonObject payload = arguments ?? new JsonObject();
		payload["cmd"] = command;

		_process.StandardInput.WriteLine(payload.ToJsonString());
		_process.StandardInput.Flush();

		return ReadResponse(command);
	}

	/// <summary>Stop the harness and wait for it to exit.</summary>
	public void Shutdown()
	{
		Request("shutdown");
		_process.WaitForExit();
	}

	private JsonElement ReadResponse(string command)
	{
		string line = _process.StandardOutput.ReadLine() ?? throw new InvalidOperationException($"the harness ended before responding to `{command}`");
		using JsonDocument document = JsonDocument.Parse(line);
		if (document.RootElement.TryGetProperty("error", out JsonElement error))
		{
			throw new InvalidOperationException($"harness command `{command}` failed: {error.GetString()}");
		}

		return document.RootElement.Clone();
	}

	/// <summary>The harness dist directory recorded by the project file.</summary>
	private static string DistDirectory()
	{
		AssemblyMetadataAttribute? recorded = typeof(NodeHarness).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(attribute => attribute.Key == "HarnessDist");

		return recorded?.Value ?? throw new InvalidOperationException("the HarnessDist assembly metadata is missing");
	}

	public void Dispose()
	{
		if (!_process.HasExited)
		{
			_process.Kill(entireProcessTree: true);
			_process.WaitForExit();
		}

		_process.Dispose();
	}
}
