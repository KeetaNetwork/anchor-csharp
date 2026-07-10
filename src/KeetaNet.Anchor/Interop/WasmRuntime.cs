using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wasmtime;

namespace KeetaNet.Anchor;

/// <summary>
/// Loads the P1 <c>wasm32-wasip1</c> core module and satisfies its host imports
/// with .NET HTTP and timers. The anchor logic runs inside the module.
/// </summary>
/// <remarks>
/// A runtime is thread-safe by construction: the thread-affine
/// <c>Wasmtime.Store</c> lives on a dedicated dispatcher thread and every
/// <c>keeta_*</c> call is serialized onto it. Local operations dispatch
/// synchronously while networked client operations are <c>async</c> and honor a
/// <see cref="CancellationToken"/> before dispatch and during host HTTP and
/// sleeps (the guest owns control flow between those points).
/// </remarks>
public sealed partial class WasmRuntime : IDisposable
{
	private const string HostModule = "keeta:anchor/host";
	private const string MemoryExport = "memory";

	private readonly WasmDispatcher _dispatcher;
	private readonly HttpClient _http = new();

	private readonly Engine _engine;
	private readonly Module _module;
	private readonly Linker _linker;
	private readonly Store _store;
	private readonly Instance _instance;
	private readonly Memory _memory;

	private readonly Func<int, int> _alloc;
	private readonly Action<int, int> _dealloc;
	private readonly Func<int, int> _bytesPtr;
	private readonly Func<int, int> _bytesLen;
	private readonly Action<int> _bytesFree;
	private readonly Func<int> _lastErrorCode;
	private readonly Func<int> _lastErrorMessage;

	// Dispatcher-thread-confined: the single-flight host response buffer and
	// the token of the operation currently driving the guest.
	private byte[] _pending = Array.Empty<byte>();
	private CancellationToken _activeCancellation = CancellationToken.None;

	// Debug-only leak accounting. See CountHandleAdopted/CountHandleReleased.
	private int _outstandingHandles;

	private bool _disposed;

	private WasmRuntime(Func<Engine, Module> loadModule)
	{
		Accounts = new Crypto.AccountFactory(this);
		Certificates = new Crypto.CertificateFactory(this);
		KycCertificates = new Crypto.KycCertificateFactory(this);
		Containers = new Crypto.EncryptedContainerFactory(this);
		Sharables = new Crypto.SharableCertificateAttributesFactory(this);

		_dispatcher = new WasmDispatcher();
		try
		{
			WasmState state = _dispatcher.Run(() => CreateState(loadModule));
			_engine = state.Engine;
			_module = state.Module;
			_linker = state.Linker;
			_store = state.Store;
			_instance = state.Instance;
			_memory = state.Memory;
			_alloc = state.Alloc;
			_dealloc = state.Dealloc;
			_bytesPtr = state.BytesPtr;
			_bytesLen = state.BytesLen;
			_bytesFree = state.BytesFree;
			_lastErrorCode = state.LastErrorCode;
			_lastErrorMessage = state.LastErrorMessage;
		}
		catch
		{
			_dispatcher.Dispose();
			_http.Dispose();
			throw;
		}
	}

	/// <summary>Build every piece of thread-affine wasm state on the dispatcher thread.</summary>
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created",
		Justification = "Ownership transfers through WasmState to the runtime's fields; Dispose releases them on the dispatcher thread, and the catch releases them on construction failure.")]
	private WasmState CreateState(Func<Engine, Module> loadModule)
	{
		var engine = new Engine();
		Module? module = null;
		Linker? linker = null;
		Store? store = null;
		try
		{
			module = loadModule(engine);
			linker = new Linker(engine);
			store = new Store(engine);

			linker.DefineWasi();
			store.SetWasiConfiguration(new WasiConfiguration()
				.WithInheritedStandardOutput()
				.WithInheritedStandardError());

			linker.DefineFunction(HostModule, "keeta_anchor_host_fetch", (CallerFunc<int, int, int>)HostFetch);
			linker.DefineFunction(HostModule, "keeta_anchor_host_take", (CallerAction<int>)HostTake);
			linker.DefineFunction(HostModule, "keeta_anchor_host_sleep", (Action<long>)HostSleep);

			Instance instance = linker.Instantiate(store, module);
			instance.GetAction("_initialize")?.Invoke();

			Memory? memory = instance.GetMemory(MemoryExport);
			if (memory is null)
			{
				throw new KeetaException("WASM", "module exports no memory");
			}

			return new WasmState(
				engine,
				module,
				linker,
				store,
				instance,
				memory,
				Required("keeta_alloc", instance.GetFunction<int, int>("keeta_alloc")),
				Required("keeta_dealloc", instance.GetAction<int, int>("keeta_dealloc")),
				Required("keeta_bytes_ptr", instance.GetFunction<int, int>("keeta_bytes_ptr")),
				Required("keeta_bytes_len", instance.GetFunction<int, int>("keeta_bytes_len")),
				Required("keeta_bytes_free", instance.GetAction<int>("keeta_bytes_free")),
				Required("keeta_last_error_code", instance.GetFunction<int>("keeta_last_error_code")),
				Required("keeta_last_error_message", instance.GetFunction<int>("keeta_last_error_message")));
		}
		catch
		{
			store?.Dispose();
			linker?.Dispose();
			module?.Dispose();
			engine.Dispose();
			throw;
		}
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

	/// <summary>Run one local operation on the dispatcher thread, blocking for its result.</summary>
	private TResult Run<TResult>(Func<TResult> work) => _dispatcher.Run(work);

	/// <summary>Run one local operation on the dispatcher thread without a result.</summary>
	private void Run(Action work) => _dispatcher.Run(work);

	/// <summary>
	/// Queue one networked operation onto the dispatcher, flowing
	/// <paramref name="cancellationToken"/> into the host imports.
	/// </summary>
	private Task<TResult> RunAsync<TResult>(Func<TResult> work, CancellationToken cancellationToken) =>
		_dispatcher.RunAsync(
			() =>
			{
				_activeCancellation = cancellationToken;
				try
				{
					return work();
				}
				catch (Exception error) when (cancellationToken.IsCancellationRequested)
				{
					throw new OperationCanceledException("the operation was canceled", error, cancellationToken);
				}
				finally
				{
					_activeCancellation = CancellationToken.None;
				}
			},
			cancellationToken);

	internal Task<byte[]> KycProviders(int handle, string countriesJson, CancellationToken cancellationToken) =>
		RunAsync(() => WithHandleAndText("keeta_kyc_providers", handle, countriesJson), cancellationToken);

	internal Task<byte[]> KycCreateVerification(
		int handle,
		string providerJson,
		string countriesJson,
		string redirect,
		CancellationToken cancellationToken) =>
		RunAsync(
			() =>
			{
				using var arguments = new ArgumentScope(this);
				Argument provider = arguments.Write(providerJson);
				Argument countries = arguments.Write(countriesJson);
				Argument target = arguments.Write(redirect);
				int result = Invoke<int, int, int, int, int, int, int, int>(
					"keeta_kyc_create_verification",
					handle,
					provider.Pointer, provider.Length,
					countries.Pointer, countries.Length,
					target.Pointer, target.Length);

				return TakeBytes(result);
			},
			cancellationToken);

	internal Task<byte[]> KycGetCertificates(int handle, string providerJson, string id, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_kyc_get_certificates", handle, providerJson, id), cancellationToken);

	internal Task<byte[]> KycGetVerificationStatus(int handle, string providerJson, string id, CancellationToken cancellationToken) =>
		RunAsync(() => WithProviderAndArg("keeta_kyc_get_verification_status", handle, providerJson, id), cancellationToken);

	internal void KycFree(int handle) => RunFree("keeta_kyc_free", handle);

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

	/// <summary>
	/// Sleep on the guest's behalf, waking early when the active operation cancels.
	/// </summary>
	private void HostSleep(long millis)
	{
		if (millis <= 0)
		{
			return;
		}

		int bounded = (int)Math.Min(millis, int.MaxValue);
		CancellationToken token = _activeCancellation;
		if (token.CanBeCanceled)
		{
			token.WaitHandle.WaitOne(bounded);
			return;
		}

		Thread.Sleep(bounded);
	}

	/// <summary>
	/// Run one HTTP request, projecting the result (or any failure, including
	/// cancellation) to response JSON - the guest must always receive a
	/// response, never an unwinding exception.
	/// </summary>
	private byte[] PerformHttp(byte[] requestJson)
	{
		try
		{
			_activeCancellation.ThrowIfCancellationRequested();

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

			using HttpResponseMessage response = _http.Send(message, _activeCancellation);
			byte[] body = response.Content.ReadAsByteArrayAsync(_activeCancellation).GetAwaiter().GetResult();

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
		catch (OperationCanceledException) when (_activeCancellation.IsCancellationRequested)
		{
			// A terminal status stops the retry/backoff loop at once
			var canceled = new HostResponse { Status = 400 };
			return JsonSerializer.SerializeToUtf8Bytes(canceled, KeetaJson.Options);
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

	/// <summary>Drive a handle-only export that yields a bytes payload.</summary>
	private byte[] BytesOf(string export, int handle) =>
		Run(() =>
		{
			int result = Invoke<int, int>(export, handle);
			return TakeBytes(result);
		});

	/// <summary>Drive a handle-only export that yields UTF-8 text.</summary>
	private string TextOf(string export, int handle) =>
		Run(() =>
		{
			int result = Invoke<int, int>(export, handle);
			return Text(result);
		});

	/// <summary>Drive a handle-only export that yields a tri-state predicate.</summary>
	private bool FlagOf(string export, int handle) =>
		Run(() =>
		{
			int result = Invoke<int, int>(export, handle);
			return TakeFlag(result);
		});

	/// <summary>Drive a handle-only export that yields a new object handle.</summary>
	private int HandleOf(string export, int handle) =>
		Run(() =>
		{
			int result = Invoke<int, int>(export, handle);
			return TakeHandle(result);
		});

	/// <summary>Drive a handle-only export that yields a raw 64-bit value.</summary>
	private long LongOf(string export, int handle) => Run(() => InvokeLong(export, handle));

	/// <summary>
	/// Drive an export taking an object handle and one binary argument, yielding
	/// a bytes payload. Unlike <see cref="WithHandleAndText"/>, every caller is a
	/// synchronous local operation, so this dispatches itself.
	/// </summary>
	private byte[] WithHandleAndBytes(string export, int handle, byte[] value) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument argument = arguments.WriteBytes(value);

			int result = Invoke<int, int, int, int>(export, handle, argument.Pointer, argument.Length);
			return TakeBytes(result);
		});

	/// <summary>
	/// Queue a guest handle release onto the dispatcher without blocking.
	/// Safe from any thread, including the finalizer thread.
	/// </summary>
	private void RunFree(string export, int handle)
	{
		if (_disposed)
		{
			return;
		}

		_dispatcher.TryPost(() => FreeQuietly(export, handle));
	}

	/// <summary>
	/// Release one guest handle, swallowing failures: a free racing runtime
	/// teardown (a finalizer-enqueued job draining after the store is gone)
	/// is best-effort by design.
	/// </summary>
	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "A queued free has no caller to observe a failure, and an unhandled exception would kill the dispatcher thread.")]
	private void FreeQuietly(string export, int handle)
	{
		if (_disposed)
		{
			return;
		}

		try
		{
			Free(export, handle);
		}
		catch (Exception)
		{
			// Best-effort: the runtime is tearing down and reclaims the memory.
		}
	}

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
	private int ClientWithAccount(string export, string nodeUrl, string root, int accountHandle) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument node = arguments.Write(nodeUrl);
			Argument anchor = arguments.Write(root);

			int result = Invoke<int, int, int, int, int, int>(export, node.Pointer, node.Length, anchor.Pointer, anchor.Length, accountHandle);
			return TakeHandle(result);
		});

	/// <summary>Parse a binary payload resolved with a principal handle set, returning an object handle.</summary>
	private int ParseBytesWithPrincipals(string export, byte[] data, int[] principals) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument payload = arguments.WriteBytes(data);
			Argument keys = arguments.WriteHandles(principals);

			int result = Invoke<int, int, int, int, int>(export, payload.Pointer, payload.Length, keys.Pointer, keys.Length);
			return TakeHandle(result);
		});

	/// <summary>Grant a principal handle set access to a sealed object.</summary>
	private void GrantAccess(string export, int handle, int[] principals) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument keys = arguments.WriteHandles(principals);

			int result = Invoke<int, int, int, int>(export, handle, keys.Pointer, keys.Length);
			TakeFlag(result);
		});

	/// <summary>Revoke the principal identified by a type-prefixed public key.</summary>
	private void RevokeAccess(string export, int handle, byte[] publicKey) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument key = arguments.WriteBytes(publicKey);

			int result = Invoke<int, int, int, int>(export, handle, key.Pointer, key.Length);
			TakeFlag(result);
		});

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
	/// Resolve an object-handle result: <c>0</c> signals failure, otherwise
	/// return the live handle.
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
	/// Resolve a tri-state predicate result: a negative value signals failure.
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

	/// <summary>
	/// Build an exception from the module's pending <c>code</c>/<c>message</c>.
	/// An asset-movement blocker code carries the blocker as JSON in the
	/// message, surfaced typed as a <see cref="KeetaBlockerException"/>.
	/// </summary>
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

		return KeetaBlockerException.TryDecode(code, message) ?? new KeetaException(code, message);
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
	/// The wrapper handles adopted but not yet released. Only debug builds
	/// count, so release builds always read zero.
	/// </summary>
	internal int OutstandingHandles => Volatile.Read(ref _outstandingHandles);

	/// <summary>Debug-only: count a wrapper adopting a core handle.</summary>
	[Conditional("DEBUG")]
	internal void CountHandleAdopted() => Interlocked.Increment(ref _outstandingHandles);

	/// <summary>Debug-only: count a wrapper releasing its core handle.</summary>
	[Conditional("DEBUG")]
	internal void CountHandleReleased() => Interlocked.Decrement(ref _outstandingHandles);

	/// <summary>Debug-only: flag wrappers leaked past runtime disposal.</summary>
	[Conditional("DEBUG")]
	private void WarnIfHandlesOutstanding()
	{
		int outstanding = OutstandingHandles;
		if (outstanding > 0)
		{
			Debug.WriteLine($"KeetaNet.Anchor: the runtime was disposed with {outstanding} undisposed wrapper handle(s); dispose every wrapper before its runtime");
		}
	}

	/// <summary>Reject a call made after disposal. Assert dispatcher-thread confinement.</summary>
	private void EnsureUsable()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Debug.Assert(_dispatcher.IsCurrentThread, "wasm state must only be touched on the dispatcher thread");
	}

	/// <summary>Dispose the wasm state on the dispatcher thread, then the dispatcher and HTTP shim.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		WarnIfHandlesOutstanding();
		_disposed = true;
		_dispatcher.Run(() =>
		{
			_store.Dispose();
			_linker.Dispose();
			_module.Dispose();
			_engine.Dispose();
		});
		_dispatcher.Dispose();
		_http.Dispose();
	}

	/// <summary>A guest buffer the host owns until it frees it.</summary>
	private readonly record struct Argument(int Pointer, int Length);

	/// <summary>
	/// The guest buffers one call owns, freed together when the scope closes.
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

	/// <summary>The thread-affine wasm state, built and owned on the dispatcher thread.</summary>
	private sealed record WasmState(
		Engine Engine,
		Module Module,
		Linker Linker,
		Store Store,
		Instance Instance,
		Memory Memory,
		Func<int, int> Alloc,
		Action<int, int> Dealloc,
		Func<int, int> BytesPtr,
		Func<int, int> BytesLen,
		Action<int> BytesFree,
		Func<int> LastErrorCode,
		Func<int> LastErrorMessage);

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
