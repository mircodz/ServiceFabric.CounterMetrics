using System;
using System.Collections.Concurrent;
using System.Fabric;
using System.Runtime.InteropServices;
using System.Threading;

namespace ServiceFabric.CounterMetrics;

/// <summary>
/// Process-wide bridge from Service Fabric perf counters to System.Diagnostics.Metrics.
/// One Meter per subsystem and one sampler per process regardless of how many replicas register
/// (shared process model safe). No-op on non-Windows.
/// </summary>
public static class ServiceFabricCounterMetrics
{
    public const string ReplicatorMeterName = "ServiceFabric.Counters.Replicator";
    public const string TStoreMeterName = "ServiceFabric.Counters.TStore";
    public const string RemotingMeterName = "ServiceFabric.Counters.Remoting";
    public const string ActorsMeterName = "ServiceFabric.Counters.Actors";

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<(Guid Partition, long Replica), int> Replicas = new();

    private static CounterSampler? _sampler;
    private static CounterMeterSet? _meters;
    private static int _refs;
    private static int _nodeRefs;

    /// <summary>Takes effect when the first handle is acquired.</summary>
    public static TimeSpan SamplingInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Emit counters belonging to this replica/instance only. Call from OnOpenAsync,
    /// dispose from OnCloseAsync/OnAbort.
    /// </summary>
    public static IDisposable Start(ServiceContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        return Acquire((context.PartitionId, context.ReplicaOrInstanceId), node: false);
    }

    /// <summary>
    /// Emit every instance on the node (agent/sidecar deployment). Only mode that
    /// includes "Service Fabric Actor Method", whose instance names carry no partition id.
    /// </summary>
    public static IDisposable StartNodeScope() => Acquire(null, node: true);

    private static IDisposable Acquire((Guid, long)? replica, bool node)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Handle.Noop;

        lock (Gate)
        {
            if (replica is { } r)
                Replicas.AddOrUpdate(r, 1, static (_, c) => c + 1);
            if (node)
                Interlocked.Increment(ref _nodeRefs);

            if (_refs++ == 0)
            {
                CounterSampler? sampler = null;
                var initialized = false;
                try
                {
                    sampler = new CounterSampler(Accept, SamplingInterval);
                    _meters = new CounterMeterSet(sampler.Observe);
                    _sampler = sampler;
                    initialized = true;
                }
                finally
                {
                    if (!initialized)
                    {
                        sampler?.Dispose();
                        Release(replica, node);
                    }
                }
            }
        }

        return new Handle(replica, node);
    }

    private static void Release((Guid, long)? replica, bool node)
    {
        lock (Gate)
        {
            if (replica is { } r &&
                Replicas.AddOrUpdate(r, 0, static (_, c) => c - 1) <= 0)
            {
                Replicas.TryRemove(r, out _);
            }

            if (node)
                Interlocked.Decrement(ref _nodeRefs);

            if (--_refs == 0)
            {
                _meters?.Dispose();   // unregisters instruments first so callbacks stop
                _sampler?.Dispose();
                _meters = null;
                _sampler = null;
            }
        }
    }

    private static bool Accept(ParsedInstance p)
    {
        if (Volatile.Read(ref _nodeRefs) > 0)
            return true;

        if (p.PartitionId is not Guid pid)
            return false;

        if (p.ReplicaId is long rid)
            return Replicas.ContainsKey((pid, rid));

        foreach (var key in Replicas.Keys)
            if (key.Partition == pid)
                return true;

        return false;
    }

    private sealed class Handle : IDisposable
    {
        public static readonly IDisposable Noop = new Handle(null, false, noop: true);

        private readonly (Guid, long)? _replica;
        private readonly bool _node;
        private int _disposed;

        public Handle((Guid, long)? replica, bool node, bool noop = false)
        {
            _replica = replica;
            _node = node;
            _disposed = noop ? 1 : 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Release(_replica, _node);
        }
    }
}
