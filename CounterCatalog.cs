using System;
using System.Collections.Generic;

namespace ServiceFabric.CounterMetrics;

internal enum Kind
{
    /// <summary>Instantaneous value (NumberOfItems*). Emitted as ObservableGauge.</summary>
    Gauge,
    /// <summary>Rate counter; raw value is a cumulative total. Emitted as ObservableCounter, backend computes rate.</summary>
    Cumulative,
    /// <summary>AverageCount64/AverageTimer32; needs two samples. Emitted as ObservableGauge over the sampling window.</summary>
    Average,
}

internal sealed record CounterSpec(string Counter, string Metric, Kind Kind, string Unit);

internal sealed record CategorySpec(string Name, string MeterName, Func<string, ParsedInstance?> Parse, CounterSpec[] Counters);

internal readonly record struct ParsedInstance(Guid? PartitionId, long? ReplicaId, params KeyValuePair<string, object?>[] Tags);

internal static class CounterCatalog
{
    public static readonly CategorySpec[] Categories =
    [
        new("Service Fabric Transactional Replicator", ServiceFabricCounterMetrics.ReplicatorMeterName, InstanceParsers.PartitionReplica,
        [
            new("Begin Txn Operations/sec",        "service_fabric.replicator.txn.begun",              Kind.Cumulative, "{transaction}"),
            new("Txn Operations/sec",              "service_fabric.replicator.txn.operations",         Kind.Cumulative, "{operation}"),
            new("Throttled Operations/sec",        "service_fabric.replicator.throttled_operations",   Kind.Cumulative, "{operation}"),
            new("Avg. Transaction ms/Commit",      "service_fabric.replicator.commit.duration",        Kind.Average,    "ms"),
            new("Avg. Flush Latency (ms)",         "service_fabric.replicator.flush.duration",         Kind.Average,    "ms"),
            new("Avg. Serialization Latency (ms)", "service_fabric.replicator.serialization.duration", Kind.Average,    "ms"),
            new("Log Flush Bytes/sec",             "service_fabric.replicator.log.flushed",            Kind.Cumulative, "By"),
            new("Checkpoint File Write Bytes/sec", "service_fabric.replicator.checkpoint.written",     Kind.Cumulative, "By"),
            new("Copy Disk Transfer Bytes/sec",    "service_fabric.replicator.copy.transferred",       Kind.Cumulative, "By"),
            new("Log Recovery Bytes/sec",          "service_fabric.replicator.log.recovered",          Kind.Cumulative, "By"),
        ]),

        new("Service Fabric TStore", ServiceFabricCounterMetrics.TStoreMeterName, InstanceParsers.TStore,
        [
            new("Item Count",                      "service_fabric.tstore.items",                      Kind.Gauge,      "{item}"),
            new("Disk Size",                       "service_fabric.tstore.disk.size",                  Kind.Gauge,      "By"),
            new("Checkpoint File Write Bytes/sec", "service_fabric.tstore.checkpoint.written",         Kind.Cumulative, "By"),
            new("Copy Disk Transfer Bytes/sec",    "service_fabric.tstore.copy.transferred",           Kind.Cumulative, "By"),
        ]),

        new("Service Fabric Service", ServiceFabricCounterMetrics.RemotingMeterName, InstanceParsers.PartitionReplicaInternal,
        [
            new("# of outstanding requests",                         "service_fabric.remoting.requests.outstanding",             Kind.Gauge,   "{request}"),
            new("Average milliseconds per request",                  "service_fabric.remoting.request.duration",                 Kind.Average, "ms"),
            new("Average milliseconds for request deserialization",  "service_fabric.remoting.request.deserialization.duration", Kind.Average, "ms"),
            new("Average milliseconds for response serialization",   "service_fabric.remoting.response.serialization.duration",  Kind.Average, "ms"),
        ]),

        new("Service Fabric Service Method", ServiceFabricCounterMetrics.RemotingMeterName, InstanceParsers.ServiceMethod,
        [
            new("Invocations/Sec",                     "service_fabric.remoting.method.invocations", Kind.Cumulative, "{invocation}"),
            new("Average milliseconds per invocation", "service_fabric.remoting.method.duration",    Kind.Average,    "ms"),
            new("Exceptions thrown/Sec",               "service_fabric.remoting.method.exceptions",  Kind.Cumulative, "{exception}"),
        ]),

        new("Service Fabric Actor", ServiceFabricCounterMetrics.ActorsMeterName, InstanceParsers.PartitionInternal,
        [
            new("# of outstanding requests",                         "service_fabric.actor.remoting.requests.outstanding",            Kind.Gauge,      "{request}"),
            new("Average milliseconds per request",                  "service_fabric.actor.remoting.request.duration",                Kind.Average,    "ms"),
            new("Average milliseconds for request deserialization",  "service_fabric.actor.remoting.request.deserialization.duration", Kind.Average,    "ms"),
            new("Average milliseconds for response serialization",   "service_fabric.actor.remoting.response.serialization.duration",  Kind.Average,    "ms"),
            new("# of actors waiting for actor lock",                "service_fabric.actor.lock.waiters",                             Kind.Gauge,      "{actor}"),
            new("Average milliseconds per lock wait",                "service_fabric.actor.lock.wait.duration",                       Kind.Average,    "ms"),
            new("Average milliseconds actor lock held",              "service_fabric.actor.lock.held.duration",                       Kind.Average,    "ms"),
            new("Average milliseconds per save state operation",     "service_fabric.actor.state.save.duration",                      Kind.Average,    "ms"),
            new("Average milliseconds per load state operation",     "service_fabric.actor.state.load.duration",                      Kind.Average,    "ms"),
            new("# of actors active",                                "service_fabric.actor.active",                                   Kind.Gauge,      "{actor}"),
            new("Average OnActivateAsync milliseconds",              "service_fabric.actor.activation.duration",                      Kind.Average,    "ms"),
            new("Actor activations/Sec",                             "service_fabric.actor.activations",                              Kind.Cumulative, "{activation}"),
            new("Actor deactivations/Sec",                           "service_fabric.actor.deactivations",                            Kind.Cumulative, "{deactivation}"),
        ]),

        // Instance name carries no partition id -> only emitted in node scope.
        new("Service Fabric Actor Method", ServiceFabricCounterMetrics.ActorsMeterName, InstanceParsers.ActorMethod,
        [
            new("Invocations/Sec",                     "service_fabric.actor.method.invocations", Kind.Cumulative, "{invocation}"),
            new("Average milliseconds per invocation", "service_fabric.actor.method.duration",    Kind.Average,    "ms"),
            new("Exceptions thrown/Sec",               "service_fabric.actor.method.exceptions",  Kind.Cumulative, "{exception}"),
        ]),
    ];
}

internal static class InstanceParsers
{
    private const string PartitionTag = "service_fabric.partition_id";
    private const string ReplicaTag = "service_fabric.replica_id";

    // {PartitionId}:{ReplicaId}
    public static ParsedInstance? PartitionReplica(string instance)
    {
        var p = instance.Split(':');
        if (p.Length < 2 || !Guid.TryParse(p[0], out var pid) || !long.TryParse(p[1], out var rid))
            return null;
        return new ParsedInstance(pid, rid, Tags(pid, rid));
    }

    // {PartitionId}:{ReplicaId}:{StateProviderId}_{Differentiator}
    public static ParsedInstance? TStore(string instance)
    {
        var p = instance.Split([':'], 3);
        if (p.Length < 3 || !Guid.TryParse(p[0], out var pid) || !long.TryParse(p[1], out var rid))
            return null;

        var sp = p[2];
        var cut = sp.LastIndexOf('_');
        if (cut > 0) sp = sp.Substring(0, cut);

        return new ParsedInstance(pid, rid, Tags(pid, rid, new KeyValuePair<string, object?>("service_fabric.state_provider_id", sp)));
    }

    // {PartitionId}_{ReplicaOrInstanceId}_{InternalId}
    public static ParsedInstance? PartitionReplicaInternal(string instance)
    {
        var p = instance.Split('_');
        if (p.Length < 3 || !Guid.TryParse(p[0], out var pid) || !long.TryParse(p[1], out var rid))
            return null;
        return new ParsedInstance(pid, rid, Tags(pid, rid));
    }

    // {Method}_{Interface}_{PartitionId}_{ReplicaOrInstanceId}_{InternalId}
    // Parsed from the right; underscores in the method name are preserved.
    public static ParsedInstance? ServiceMethod(string instance)
    {
        var p = instance.Split('_');
        var n = p.Length;
        if (n < 5 || !Guid.TryParse(p[n - 3], out var pid) || !long.TryParse(p[n - 2], out var rid))
            return null;

        return new ParsedInstance(pid, rid, Tags(pid, rid,
            new KeyValuePair<string, object?>("service_fabric.interface", p[n - 4]),
            new KeyValuePair<string, object?>("service_fabric.method", string.Join("_", p, 0, n - 4))));
    }

    // {PartitionId}_{InternalId}
    public static ParsedInstance? PartitionInternal(string instance)
    {
        var p = instance.Split('_');
        if (p.Length < 2 || !Guid.TryParse(p[0], out var pid))
            return null;
        return new ParsedInstance(pid, null,
            new KeyValuePair<string, object?>(PartitionTag, pid.ToString()));
    }

    // {Method}_{Interface}_{InternalId}
    public static ParsedInstance? ActorMethod(string instance)
    {
        var p = instance.Split('_');
        var n = p.Length;
        if (n < 3)
            return null;
        return new ParsedInstance(null, null,
            new KeyValuePair<string, object?>("service_fabric.interface", p[n - 2]),
            new KeyValuePair<string, object?>("service_fabric.method", string.Join("_", p, 0, n - 2)));
    }

    private static KeyValuePair<string, object?>[] Tags(Guid pid, long rid, params KeyValuePair<string, object?>[] extra)
    {
        var tags = new KeyValuePair<string, object?>[2 + extra.Length];
        tags[0] = new KeyValuePair<string, object?>(PartitionTag, pid.ToString());
        tags[1] = new KeyValuePair<string, object?>(ReplicaTag, rid);
        Array.Copy(extra, 0, tags, 2, extra.Length);
        return tags;
    }
}
