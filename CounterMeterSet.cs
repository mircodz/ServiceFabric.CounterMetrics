using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace ServiceFabric.CounterMetrics;

internal sealed class CounterMeterSet : IDisposable
{
    private readonly Dictionary<string, Meter> _meters = new(StringComparer.Ordinal);

    public CounterMeterSet(Func<string, IEnumerable<Measurement<double>>> observe)
    {
        if (observe is null) throw new ArgumentNullException(nameof(observe));

        var initialized = false;
        try
        {
            var version = typeof(ServiceFabricCounterMetrics).Assembly.GetName().Version?.ToString();
            var registered = new HashSet<string>(StringComparer.Ordinal);

            foreach (var category in CounterCatalog.Categories)
            {
                if (!_meters.TryGetValue(category.MeterName, out var meter))
                {
                    meter = new Meter(category.MeterName, version);
                    _meters.Add(category.MeterName, meter);
                }

                foreach (var spec in category.Counters)
                {
                    if (!registered.Add(spec.Metric))
                        throw new InvalidOperationException($"Duplicate metric name '{spec.Metric}' in catalog.");

                    var metric = spec.Metric;
                    Func<IEnumerable<Measurement<double>>> callback = () => observe(metric);
                    var description = $@"\{category.Name}\{spec.Counter}";

                    _ = spec.Kind == Kind.Cumulative
                        ? (Instrument)meter.CreateObservableCounter(metric, callback, spec.Unit, description)
                        : meter.CreateObservableGauge(metric, callback, spec.Unit, description);
                }
            }

            initialized = true;
        }
        finally
        {
            if (!initialized)
                Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var meter in _meters.Values)
            meter.Dispose();
        _meters.Clear();
    }
}
