// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowRunConcurrencyGate.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Core;

/// <summary>
/// A process-local concurrency gate shared by multiple workflow run managers. A limit of zero
/// means unlimited. The limit may be changed while runs are active; active work is never
/// interrupted, but no additional work is admitted until the active count falls below the new
/// limit.
/// </summary>
public sealed class WorkflowRunConcurrencyGate
{
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _limit;
    private int _active;

    public WorkflowRunConcurrencyGate(int limit = 0)
    {
        ValidateLimit(limit);
        _limit = limit;
    }

    public int Limit
    {
        get
        {
            lock (_gate)
                return _limit;
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
                return _active;
        }
    }

    public void SetLimit(int limit)
    {
        ValidateLimit(limit);
        lock (_gate)
        {
            _limit = limit;
            PumpWaitersLocked();
        }
    }

    internal ValueTask<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<Lease>(cancellationToken);

            if (CanEnterLocked())
            {
                _active++;
                return ValueTask.FromResult(new Lease(this));
            }

            waiter = new Waiter(this, cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);
        }

        return AwaitWaiterAsync(waiter);
    }

    private async ValueTask<Lease> AwaitWaiterAsync(Waiter waiter)
    {
        using var cancellationRegistration = waiter.CancellationToken.Register(
            static state => ((Waiter)state!).Cancel(), waiter);

        try
        {
            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_active <= 0)
                throw new InvalidOperationException(
                    "Workflow run concurrency lease was released more than once.");

            _active--;
            PumpWaitersLocked();
        }
    }

    private void PumpWaitersLocked()
    {
        while (_waiters.First is { } first)
        {
            var waiter = first.Value;
            if (waiter.CancellationToken.IsCancellationRequested)
            {
                _waiters.RemoveFirst();
                waiter.Node = null;
                waiter.Completion.TrySetCanceled(waiter.CancellationToken);
                continue;
            }

            if (!CanEnterLocked())
                break;

            _waiters.RemoveFirst();
            waiter.Node = null;
            _active++;
            waiter.Completion.TrySetResult(new Lease(this));
        }
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node?.List != _waiters)
                return;

            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            waiter.Completion.TrySetCanceled(waiter.CancellationToken);
        }
    }

    private bool CanEnterLocked() => _limit == 0 || _active < _limit;

    private static void ValidateLimit(int limit)
    {
        if (limit < 0)
            throw new ArgumentOutOfRangeException(nameof(limit),
                "Concurrency limit must be zero (unlimited) or a positive number.");
    }

    internal sealed class Lease : IDisposable
    {
        private WorkflowRunConcurrencyGate? _owner;

        public Lease(WorkflowRunConcurrencyGate owner) => _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }

    private sealed class Waiter
    {
        public Waiter(WorkflowRunConcurrencyGate owner, CancellationToken cancellationToken)
        {
            Owner = owner;
            CancellationToken = cancellationToken;
            Completion = new TaskCompletionSource<Lease>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public WorkflowRunConcurrencyGate Owner { get; }
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource<Lease> Completion { get; }
        public LinkedListNode<Waiter>? Node { get; set; }

        public void Cancel() => Owner.CancelWaiter(this);
    }
}
