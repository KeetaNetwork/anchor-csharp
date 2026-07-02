using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace KeetaNet.Anchor;

/// <summary>
/// A dedicated owner thread with a serialized work queue. The thread-affine
/// wasm state is created, driven, and disposed only on this thread, so callers
/// on any thread are safe by construction.
/// </summary>
internal sealed class WasmDispatcher : IDisposable
{
	private readonly BlockingCollection<Action> _queue = new();
	private readonly Thread _thread;
	private bool _disposed;

	public WasmDispatcher()
	{
		_thread = new Thread(ProcessQueue)
		{
			IsBackground = true,
			Name = "keetanet-anchor-wasm",
		};
		_thread.Start();
	}

	/// <summary>Whether the caller is already on the dispatcher thread.</summary>
	public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

	/// <summary>Run <paramref name="work"/> on the dispatcher thread and block for its result.</summary>
	public TResult Run<TResult>(Func<TResult> work)
	{
		Task<TResult> pending = RunAsync(work, CancellationToken.None);
		return pending.GetAwaiter().GetResult();
	}

	/// <summary>Run <paramref name="work"/> on the dispatcher thread without a result.</summary>
	public void Run(Action work) =>
		Run(() =>
		{
			work();
			return true;
		});

	/// <summary>
	/// Queue <paramref name="work"/> for the dispatcher thread. A token canceled
	/// before the job is dequeued cancels the task without running the work.
	/// </summary>
	[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every failure is marshaled to the awaiting caller through the task.")]
	public Task<TResult> RunAsync<TResult>(Func<TResult> work, CancellationToken cancellationToken)
	{
		Debug.Assert(!IsCurrentThread, "dispatching from the dispatcher thread would deadlock");

		ObjectDisposedException.ThrowIf(_disposed, this);

		var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (cancellationToken.IsCancellationRequested)
		{
			completion.SetCanceled(cancellationToken);
			return completion.Task;
		}

		// The queue token is deliberately None: a canceled job must still be
		// dequeued so it can complete its task as canceled.
		_queue.Add(
			() =>
			{
				if (cancellationToken.IsCancellationRequested)
				{
					completion.SetCanceled(cancellationToken);
					return;
				}

				try
				{
					TResult result = work();
					completion.SetResult(result);
				}
				catch (Exception error)
				{
					completion.SetException(error);
				}
			},
			CancellationToken.None);

		return completion.Task;
	}

	/// <summary>
	/// Queue fire-and-forget <paramref name="work"/>, returning false once the
	/// dispatcher has shut down. Never blocks and never throws, so it is safe
	/// from any thread - including the finalizer thread.
	/// </summary>
	public bool TryPost(Action work)
	{
		if (_disposed)
		{
			return false;
		}

		try
		{
			_queue.Add(work);
			return true;
		}
		catch (InvalidOperationException)
		{
			// Dispose completed or released the queue between the check and the add.
			return false;
		}
	}

	private void ProcessQueue()
	{
		foreach (Action job in _queue.GetConsumingEnumerable())
		{
			job();
		}
	}

	/// <summary>Drain the queue, stop the thread, and join it.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_queue.CompleteAdding();
		_thread.Join();
		_queue.Dispose();
	}
}
