using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceFabric.CounterMetrics;

/// <summary>
/// Reads each category once per tick via ReadCategory() (single HKEY_PERFORMANCE_DATA query, all
/// counters x all instances), filters to accepted instances, and publishes an immutable snapshot
/// that the observable instrument callbacks return. Callbacks never touch perflib.
/// </summary>
#if NET5_0_OR_GREATER
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
internal sealed class CounterSampler : IDisposable
{
    private static readonly IEnumerable<Measurement<double>> Empty = [];

    private readonly Func<ParsedInstance, bool> _accept;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, CounterSample> _previous = new(StringComparer.Ordinal); // sampler thread only
    private readonly Task _loop;

    private volatile Dictionary<string, List<Measurement<double>>> _latest = new(StringComparer.Ordinal);

    public CounterSampler(Func<ParsedInstance, bool> accept, TimeSpan interval)
    {
        _accept = accept;
        _interval = interval;
        _loop = Task.Run(RunAsync);
    }

    public IEnumerable<Measurement<double>> Observe(string metric)
        => _latest.TryGetValue(metric, out var list) ? list : Empty;

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { Sample(); }
            catch (Exception) { /* never take the host down; TODO: EventSource diagnostics */ }

            try { await Task.Delay(_interval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Sample()
    {
        var next = new Dictionary<string, List<Measurement<double>>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var category in CounterCatalog.Categories)
        {
            InstanceDataCollectionCollection data;
            try
            {
                data = new PerformanceCounterCategory(category.Name).ReadCategory();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            {
                continue; // not installed, not registered yet, or no read access
            }

            var parsed = new Dictionary<string, ParsedInstance?>(StringComparer.Ordinal);

            foreach (var spec in category.Counters)
            {
                var instances = data[spec.Counter]; // lookup is case-insensitive
                if (instances is null)
                    continue;

                foreach (InstanceData d in instances.Values)
                {
                    if (!parsed.TryGetValue(d.InstanceName, out var p))
                        parsed[d.InstanceName] = p = category.Parse(d.InstanceName);

                    if (p is not { } inst || !_accept(inst))
                        continue;

                    if (!TryCompute(category.Name, spec, d, seen, out var value))
                        continue;

                    if (!next.TryGetValue(spec.Metric, out var list))
                        next[spec.Metric] = list = [];

                    list.Add(new Measurement<double>(value, inst.Tags));
                }
            }
        }

        // Drop state for instances that went away (replica closed, collection removed).
        foreach (var stale in _previous.Keys.Where(k => !seen.Contains(k)).ToList())
            _previous.Remove(stale);

        _latest = next;
    }

    private bool TryCompute(string category, CounterSpec spec, InstanceData d, HashSet<string> seen, out double value)
    {
        var sample = d.Sample;
        value = 0;

        switch (spec.Kind)
        {
            case Kind.Cumulative:
                value = sample.RawValue;
                return true;

            case Kind.Gauge:
                value = CounterSample.Calculate(sample);
                return true;

            case Kind.Average:
                var key = category + "\0" + spec.Counter + "\0" + d.InstanceName;
                seen.Add(key);

                var hasPrev = _previous.TryGetValue(key, out var prev);
                _previous[key] = sample;

                // No prior sample, no events in window, or counter reset -> no point.
                if (!hasPrev || sample.BaseValue <= prev.BaseValue)
                    return false;

                value = CounterSample.Calculate(prev, sample);

                // AverageTimer32 is normalized to seconds by Calculate; SF names these in ms.
                if (sample.CounterType == PerformanceCounterType.AverageTimer32)
                    value *= 1000.0;

                return true;

            default:
                return false;
        }
    }
}
