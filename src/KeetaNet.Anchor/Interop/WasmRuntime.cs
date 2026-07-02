using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wasmtime;

namespace KeetaNet.Anchor;

/// <summary>
/// Loads the P1 <c>wasm32-wasip1</c> core module and satisfies its host imports
/// with .NET HTTP and timers. The anchor logic runs inside the module; this type
/// owns only the wasm engine, memory marshaling, and the I/O shim.
/// </summary>
/// <remarks>
/// A runtime, and every client built over it, is confined to the thread that
/// created it: the underlying <c>Wasmtime.Store</c> is thread-affine and the
/// fetch/take exchange buffers one in-flight response. Calls from any other
/// thread throw <see cref="KeetaException"/> (code <c>THREAD</c>). To use the
/// SDK from multiple threads, create one runtime per thread or serialize all
/// access externally.
/// </remarks>
public sealed partial class WasmRuntime : IDisposable
{
	private const string HostModule = "keeta:anchor/host";
	private const string MemoryExport = "memory";

	private readonly Engine _engine;
	private readonly Module _module;
	private readonly Linker _linker;
	private readonly Store _store;
	private readonly Instance _instance;
	private readonly Memory _memory;
	private readonly HttpClient _http = new();

	private readonly Func<int, int> _alloc;
	private readonly Action<int, int> _dealloc;
	private readonly Func<int, int> _bytesPtr;
	private readonly Func<int, int> _bytesLen;
	private readonly Action<int> _bytesFree;
	private readonly Func<int> _lastErrorCode;
	private readonly Func<int> _lastErrorMessage;

	private readonly int _ownerThread = Environment.CurrentManagedThreadId;

	private byte[] _pending = Array.Empty<byte>();
	private bool _disposed;

	private WasmRuntime(Func<Engine, Module> loadModule)
	{
		_engine = new Engine();
		_module = loadModule(_engine);
		_linker = new Linker(_engine);
		_store = new Store(_engine);

		_linker.DefineWasi();
		_store.SetWasiConfiguration(new WasiConfiguration()
			.WithInheritedStandardOutput()
			.WithInheritedStandardError());

		DefineHostImports();

		_instance = _linker.Instantiate(_store, _module);
		_instance.GetAction("_initialize")?.Invoke();

		Memory? memory = _instance.GetMemory(MemoryExport);
		_memory = memory ?? throw new KeetaException("WASM", "module exports no memory");
		_alloc = Required("keeta_alloc", _instance.GetFunction<int, int>("keeta_alloc"));
		_dealloc = Required("keeta_dealloc", _instance.GetAction<int, int>("keeta_dealloc"));
		_bytesPtr = Required("keeta_bytes_ptr", _instance.GetFunction<int, int>("keeta_bytes_ptr"));
		_bytesLen = Required("keeta_bytes_len", _instance.GetFunction<int, int>("keeta_bytes_len"));
		_bytesFree = Required("keeta_bytes_free", _instance.GetAction<int>("keeta_bytes_free"));
		_lastErrorCode = Required("keeta_last_error_code", _instance.GetFunction<int>("keeta_last_error_code"));
		_lastErrorMessage =
			Required("keeta_last_error_message", _instance.GetFunction<int>("keeta_last_error_message"));
	}

	/// <summary>Load the core module embedded in this assembly.</summary>
	public static WasmRuntime Load() =>
		new(engine =>
		{
			byte[] core = EmbeddedCore();
			return Module.FromBytes(engine, "core", core);
		});

	/// <summary>Load the core module from a filesystem path.</summary>
	public static WasmRuntime Load(string wasmPath) =>
		new(engine => Module.FromFile(engine, wasmPath));

	private const string EmbeddedCoreResource = "KeetaNet.Anchor.keetanetwork_anchor_client_wasi.wasm";

	private static byte[] EmbeddedCore()
	{
		using Stream resource = typeof(WasmRuntime).Assembly.GetManifestResourceStream(EmbeddedCoreResource)
			?? throw new KeetaException("WASM", $"embedded core module `{EmbeddedCoreResource}` not found");
		using var payload = new MemoryStream();

		resource.CopyTo(payload);

		return payload.ToArray();
	}

	internal byte[] KycProviders(int handle, string countriesJson) =>
		WithHandleAndText("keeta_kyc_providers", handle, countriesJson);

	internal byte[] KycCreateVerification(int handle, string providerJson, string countriesJson, string redirect)
	{
		using var arguments = new ArgumentScope(this);
		Argument provider = arguments.Write(providerJson);
		Argument countries = arguments.Write(countriesJson);
		Argument target = arguments.Write(redirect);

		int result = Invoke<int, int, int, int, int, int, int, int>("keeta_kyc_create_verification", handle, provider.Pointer, provider.Length, countries.Pointer, countries.Length, target.Pointer, target.Length);
		return TakeBytes(result);
	}

	internal byte[] KycGetCertificates(int handle, string providerJson, string id) =>
		WithProviderAndArg("keeta_kyc_get_certificates", handle, providerJson, id);

	internal byte[] KycGetVerificationStatus(int handle, string providerJson, string id) =>
		WithProviderAndArg("keeta_kyc_get_verification_status", handle, providerJson, id);

	internal void KycFree(int handle) => Free("keeta_kyc_free", handle);

	private void DefineHostImports()
	{
		_linker.DefineFunction(HostModule, "keeta_anchor_host_fetch", (CallerFunc<int, int, int>)HostFetch);
		_linker.DefineFunction(HostModule, "keeta_anchor_host_take", (CallerAction<int>)HostTake);
		_linker.DefineFunction(HostModule, "keeta_anchor_host_sleep", (Action<long>)HostSleep);
	}

	/// <summary>Perform the buffered request and return the response byte length.</summary>
	private int HostFetch(Caller caller, int requestPtr, int requestLen)
	{
		Memory memory = caller.GetMemory(MemoryExport)!;
		Span<byte> source = memory.GetSpan((uint)requestPtr, requestLen);
		byte[] request = source.ToArray();

		_pending = PerformHttp(request);
		return _pending.Length;
	}

	/// <summary>Copy the buffered response into guest memory and release it.</summary>
	private void HostTake(Caller caller, int responsePtr)
	{
		Memory memory = caller.GetMemory(MemoryExport)!;
		Span<byte> destination = memory.GetSpan((uint)responsePtr, _pending.Length);
		_pending.AsSpan().CopyTo(destination);
		_pending = Array.Empty<byte>();
	}

	private static void HostSleep(long millis)
	{
		if (millis > 0)
		{
			Thread.Sleep((int)Math.Min(millis, int.MaxValue));
		}
	}

	/// <summary>Run one HTTP request, projecting the result (or failure) to response JSON.</summary>
	private byte[] PerformHttp(byte[] requestJson)
	{
		try
		{
			HostRequest request = JsonSerializer.Deserialize<HostRequest>(requestJson, KeetaJson.Options)
				?? throw new KeetaException("HOST", "empty host request");

			var method = new HttpMethod(request.Method);
			using var message = new HttpRequestMessage(method, request.Url);
			if (request.Body is not null)
			{
				byte[] requestBody = Convert.FromBase64String(request.Body);
				message.Content = new ByteArrayContent(requestBody);
				message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			}

			message.Headers.Accept.ParseAdd("application/json");

			using HttpResponseMessage response = _http.Send(message);
			byte[] body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

			string? retryAfter = null;
			if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values))
			{
				retryAfter = values.FirstOrDefault();
			}

			string encodedBody = Convert.ToBase64String(body);
			var payload = new HostResponse
			{
				Status = (ushort)(int)response.StatusCode,
				Body = encodedBody,
				RetryAfter = retryAfter,
			};

			return JsonSerializer.SerializeToUtf8Bytes(payload, KeetaJson.Options);
		}
		catch (Exception error)
		{
			var payload = new HostResponse { Error = error.Message };
			return JsonSerializer.SerializeToUtf8Bytes(payload, KeetaJson.Options);
		}
	}

	/// <summary>Copy a UTF-8 string into a fresh guest buffer.</summary>
	private Argument Write(string value, List<Argument> owned) =>
		WriteBytes(Encoding.UTF8.GetBytes(value), owned);

	/// <summary>Drive an export taking a client handle and one UTF-8 argument.</summary>
	private byte[] WithHandleAndText(string export, int handle, string value)
	{
		using var arguments = new ArgumentScope(this);
		Argument argument = arguments.Write(value);

		int result = Invoke<int, int, int, int>(export, handle, argument.Pointer, argument.Length);
		return TakeBytes(result);
	}

	/// <summary>Drive an export taking a client handle, a provider, and one argument.</summary>
	private byte[] WithProviderAndArg(string export, int handle, string providerJson, string argument)
	{
		using var arguments = new ArgumentScope(this);
		Argument provider = arguments.Write(providerJson);
		Argument value = arguments.Write(argument);

		int result = Invoke<int, int, int, int, int, int>(export, handle, provider.Pointer, provider.Length, value.Pointer, value.Length);
		return TakeBytes(result);
	}

	/// <summary>Build a networked client bound to a node URL, a metadata root, and a signer.</summary>
	private int ClientWithAccount(string export, string nodeUrl, string root, int accountHandle)
	{
		using var arguments = new ArgumentScope(this);
		Argument node = arguments.Write(nodeUrl);
		Argument anchor = arguments.Write(root);

		int result = Invoke<int, int, int, int, int, int>(export, node.Pointer, node.Length, anchor.Pointer, anchor.Length, accountHandle);
		return TakeHandle(result);
	}

	/// <summary>Parse a binary payload resolved with a principal handle set, returning an object handle.</summary>
	private int ParseBytesWithPrincipals(string export, byte[] data, int[] principals)
	{
		using var arguments = new ArgumentScope(this);
		Argument payload = arguments.WriteBytes(data);
		Argument keys = arguments.WriteHandles(principals);

		int result = Invoke<int, int, int, int, int>(export, payload.Pointer, payload.Length, keys.Pointer, keys.Length);
		return TakeHandle(result);
	}

	/// <summary>Grant a principal handle set access to a sealed object.</summary>
	private void GrantAccess(string export, int handle, int[] principals)
	{
		using var arguments = new ArgumentScope(this);
		Argument keys = arguments.WriteHandles(principals);

		int result = Invoke<int, int, int, int>(export, handle, keys.Pointer, keys.Length);
		TakeFlag(result);
	}

	/// <summary>Revoke the principal identified by a type-prefixed public key.</summary>
	private void RevokeAccess(string export, int handle, byte[] publicKey)
	{
		using var arguments = new ArgumentScope(this);
		Argument key = arguments.WriteBytes(publicKey);

		int result = Invoke<int, int, int, int>(export, handle, key.Pointer, key.Length);
		TakeFlag(result);
	}

	/// <summary>
	/// Resolve a bytes-handle result: <c>0</c> signals failure (raise the pending
	/// last error), otherwise copy the bytes out and release the handle.
	/// </summary>
	private byte[] TakeBytes(int handle)
	{
		if (handle == 0)
		{
			throw LastError();
		}

		return ReadAndFreeBytes(handle);
	}

	/// <summary>
	/// Resolve an object-handle result: <c>0</c> signals failure (raise the
	/// pending last error), otherwise return the live handle.
	/// </summary>
	private int TakeHandle(int handle)
	{
		if (handle == 0)
		{
			throw LastError();
		}

		return handle;
	}

	/// <summary>
	/// Resolve a tri-state predicate result (<c>1</c>/<c>0</c>/<c>-1</c>): a
	/// negative value signals failure (raise the pending last error).
	/// </summary>
	private bool TakeFlag(int result)
	{
		if (result < 0)
		{
			throw LastError();
		}

		return result != 0;
	}

	/// <summary>Copy a bytes handle's payload into a managed array and free it.</summary>
	private byte[] ReadAndFreeBytes(int handle)
	{
		try
		{
			int pointer = _bytesPtr(handle);
			int length = _bytesLen(handle);
			if (length == 0)
			{
				return Array.Empty<byte>();
			}

			return _memory.GetSpan(pointer, length).ToArray();
		}
		finally
		{
			_bytesFree(handle);
		}
	}

	/// <summary>Build an exception from the module's pending <c>code</c>/<c>message</c>.</summary>
	private KeetaException LastError()
	{
		string code = ReadErrorPart(_lastErrorCode());
		string message = ReadErrorPart(_lastErrorMessage());
		if (code.Length == 0)
		{
			code = "UNKNOWN";
		}

		if (message.Length == 0)
		{
			message = "operation failed";
		}

		return new KeetaException(code, message);
	}

	/// <summary>Read one optional last-error part (an empty string when absent).</summary>
	private string ReadErrorPart(int handle)
	{
		if (handle == 0)
		{
			return string.Empty;
		}

		return Encoding.UTF8.GetString(ReadAndFreeBytes(handle));
	}

	private void FreeAll(List<Argument> owned)
	{
		foreach (Argument argument in owned)
		{
			_dealloc(argument.Pointer, argument.Length);
		}
	}

	private static T Required<T>(string name, T? value) where T : class =>
		value ?? throw new KeetaException("WASM", $"module export `{name}` not found");

	/// <summary>Whether this runtime has been disposed.</summary>
	internal bool IsDisposed => _disposed;

	/// <summary>
	/// Reject a call from a thread other than the creator (the store is
	/// thread-affine and the response buffer is single-flight), or one made
	/// after disposal.
	/// </summary>
	private void EnsureUsable()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (Environment.CurrentManagedThreadId != _ownerThread)
		{
			throw new KeetaException(
				"THREAD",
				"the runtime is confined to the thread that created it; create one runtime per thread");
		}
	}

	/// <summary>Dispose the wasm engine, store, and HTTP shim.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_http.Dispose();
		_store.Dispose();
		_linker.Dispose();
		_module.Dispose();
		_engine.Dispose();
	}

	/// <summary>A guest buffer the host owns until it frees it.</summary>
	private readonly record struct Argument(int Pointer, int Length);

	/// <summary>
	/// The guest buffers one call owns, freed together when the scope closes.
	/// Replaces the per-call <c>try/finally FreeAll</c> scaffold with a
	/// <c>using</c> statement.
	/// </summary>
	private sealed class ArgumentScope : IDisposable
	{
		private readonly WasmRuntime _runtime;
		private readonly List<Argument> _owned = new();

		public ArgumentScope(WasmRuntime runtime) => _runtime = runtime;

		public Argument Write(string value) => _runtime.Write(value, _owned);

		public Argument WriteBytes(byte[] value) => _runtime.WriteBytes(value, _owned);

		public Argument WriteHandles(int[] handles) => _runtime.WriteHandles(handles, _owned);

		public void Dispose() => _runtime.FreeAll(_owned);
	}

	private sealed class HostRequest
	{
		public string Method { get; set; } = "GET";
		public string Url { get; set; } = "";
		public string? Body { get; set; }
	}

	private sealed class HostResponse
	{
		public ushort Status { get; set; }
		public string Body { get; set; } = "";
		public string? RetryAfter { get; set; }
		public string? Error { get; set; }
	}
}
