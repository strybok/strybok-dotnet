using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Strybok;

/// <summary>
/// An <see cref="ILinkSource"/> you feed yourself by calling <see cref="Push"/> from the place
/// where your platform hands over URLs (for example <c>Application.OnAppLinkRequestReceived</c>
/// in .NET MAUI). Thread-safe.
/// </summary>
/// <remarks>
/// URLs pushed before the SDK starts are kept: the first one becomes the launch link returned by
/// <see cref="GetInitialLinkAsync"/>, the others are replayed to the subscriber. URLs pushed
/// afterwards go straight to the subscriber.
/// </remarks>
public sealed class ManualLinkSource : ILinkSource
{
    /// <summary>Upper bound of URLs kept while nobody listens; the oldest are dropped first.</summary>
    private const int MaxBuffered = 16;

    private static readonly Uri[] NoLinks = new Uri[0];

    private readonly object _gate = new object();
    private readonly List<Action<Uri>> _handlers = new List<Action<Uri>>();
    private readonly Queue<Uri> _buffered = new Queue<Uri>();
    private bool _initialRead;

    /// <summary>Hands a URL received from the operating system to the SDK.</summary>
    public void Push(Uri uri)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));

        Action<Uri>[] handlers;
        lock (_gate)
        {
            if (_handlers.Count == 0)
            {
                if (_buffered.Count == MaxBuffered) _buffered.Dequeue();
                _buffered.Enqueue(uri);
                return;
            }
            handlers = _handlers.ToArray();
        }

        foreach (var handler in handlers) handler(uri);
    }

    /// <inheritdoc />
    public Task<Uri?> GetInitialLinkAsync(CancellationToken cancellationToken = default)
    {
        Uri? initial = null;
        var rest = NoLinks;
        Action<Uri>[] handlers;
        lock (_gate)
        {
            _initialRead = true;
            if (_buffered.Count > 0) initial = _buffered.Dequeue();
            handlers = _handlers.ToArray();
            if (handlers.Length > 0 && _buffered.Count > 0)
            {
                rest = _buffered.ToArray();
                _buffered.Clear();
            }
        }

        foreach (var uri in rest)
        {
            foreach (var handler in handlers) handler(uri);
        }

        return Task.FromResult(initial);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<Uri> onLink)
    {
        if (onLink is null) throw new ArgumentNullException(nameof(onLink));

        var replay = NoLinks;
        lock (_gate)
        {
            _handlers.Add(onLink);
            // Before the launch link was read the buffer is left alone, so its first entry can
            // still be reported as the launch link.
            if (_initialRead && _buffered.Count > 0)
            {
                replay = _buffered.ToArray();
                _buffered.Clear();
            }
        }

        foreach (var uri in replay) onLink(uri);
        return new Subscription(this, onLink);
    }

    private void Unsubscribe(Action<Uri> handler)
    {
        lock (_gate)
        {
            _handlers.Remove(handler);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private ManualLinkSource? _owner;
        private readonly Action<Uri> _handler;

        public Subscription(ManualLinkSource owner, Action<Uri> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(_handler);
        }
    }
}
