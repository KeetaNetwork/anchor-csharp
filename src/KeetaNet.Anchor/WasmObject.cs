using System.Diagnostics.CodeAnalysis;

namespace KeetaNet.Anchor;

/// <summary>
/// A wrapper over an object living inside the wasm core, identified by a
/// handle the core issued. Disposing releases the handle. A finalizer
/// backstop releases it for a forgotten <see cref="Dispose()"/> by queueing
/// the free onto the runtime's dispatcher, which is safe from any thread.
/// </summary>
public abstract class WasmObject : IDisposable
{
	private readonly WasmRuntime _runtime;
	private readonly int _handle;
	private bool _disposed;

	private protected WasmObject(WasmRuntime runtime, int handle)
	{
		_runtime = runtime;
		_handle = handle;
		runtime.CountHandleAdopted();
	}

	/// <summary>The runtime hosting the wrapped object.</summary>
	internal WasmRuntime Runtime => _runtime;

	/// <summary>The core-module handle backing this object.</summary>
	/// <exception cref="ObjectDisposedException">The wrapper has been disposed.</exception>
	internal int Handle
	{
		get
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			return _handle;
		}
	}

	/// <summary>
	/// Release the wrapped object's core handle. Queued onto the dispatcher without
	/// blocking, so it may run from <see cref="Dispose()"/> or from the finalizer.
	/// </summary>
	private protected abstract void Release(WasmRuntime runtime, int handle);

	/// <summary>Release the core-module handle.</summary>
	public void Dispose()
	{
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}

	/// <summary>Backstop for a forgotten <see cref="Dispose()"/>.</summary>
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP023:Don't use reference types in finalizer context",
		Justification = "Releasing only enqueues onto the dispatcher queue; the runtime and dispatcher have no finalizers of their own, and a free racing runtime teardown degrades to a no-op.")]
	~WasmObject() => Dispose(disposing: false);

	/// <summary>
	/// Both paths release identically: freeing only enqueues onto the
	/// dispatcher, which is finalizer-safe.
	/// </summary>
	[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP023:Don't use reference types in finalizer context",
		Justification = "Releasing only enqueues onto the dispatcher queue; the runtime and dispatcher have no finalizers of their own, and a free racing runtime teardown degrades to a no-op.")]
	protected virtual void Dispose(bool disposing)
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_runtime.CountHandleReleased();
		Release(_runtime, _handle);
	}
}
