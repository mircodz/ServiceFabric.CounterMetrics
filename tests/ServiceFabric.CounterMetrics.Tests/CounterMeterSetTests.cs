using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace ServiceFabric.CounterMetrics.Tests;

public sealed class CounterMeterSetTests
{
    [Fact]
    public void RegistersEachInstrumentUnderItsSubsystemMeter()
    {
        var instruments = new List<Instrument>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (IsServiceFabricMeter(instrument))
                instruments.Add(instrument);
        };
        listener.Start();
        using var meters = new CounterMeterSet(_ => Array.Empty<Measurement<double>>());

        var expectedCounts = new Dictionary<string, int>
        {
            ["ServiceFabric.Counters.Replicator"] = 10,
            ["ServiceFabric.Counters.TStore"] = 4,
            ["ServiceFabric.Counters.Remoting"] = 7,
            ["ServiceFabric.Counters.Actors"] = 16,
        };
        Assert.Equal(expectedCounts.Count, instruments.Select(i => i.Meter).Distinct().Count());
        foreach (var expected in expectedCounts)
            Assert.Equal(expected.Value, instruments.Count(i => i.Meter.Name == expected.Key));

        var version = typeof(ServiceFabricCounterMetrics).Assembly.GetName().Version?.ToString();
        foreach (var category in CounterCatalog.Categories)
        foreach (var spec in category.Counters)
        {
            var instrument = Assert.Single(instruments, i => i.Name == spec.Metric);
            Assert.Equal(category.MeterName, instrument.Meter.Name);
            Assert.Equal(version, instrument.Meter.Version);
            Assert.Equal(spec.Unit, instrument.Unit);
            Assert.Equal($@"\{category.Name}\{spec.Counter}", instrument.Description);
            Assert.StartsWith("service_fabric.", instrument.Name);
            if (spec.Kind == Kind.Cumulative)
                Assert.IsType<ObservableCounter<double>>(instrument);
            else
                Assert.IsType<ObservableGauge<double>>(instrument);
        }
    }

    [Theory]
    [InlineData("service_fabric.tstore.checkpoint.written")]
    [InlineData("service_fabric.tstore.copy.transferred")]
    public void TStoreThroughputUsesGaugesInBytesPerSecond(string metricName)
    {
        var instruments = new List<Instrument>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Name == metricName)
                instruments.Add(instrument);
        };
        listener.Start();
        using var meters = new CounterMeterSet(_ => Array.Empty<Measurement<double>>());

        var gauge = Assert.IsType<ObservableGauge<double>>(Assert.Single(instruments));
        Assert.Equal(ServiceFabricCounterMetrics.TStoreMeterName, gauge.Meter.Name);
        Assert.Equal("By/s", gauge.Unit);
    }

    [Theory]
    [InlineData(ServiceFabricCounterMetrics.ReplicatorMeterName, "service_fabric.replicator.", 10)]
    [InlineData(ServiceFabricCounterMetrics.TStoreMeterName, "service_fabric.tstore.", 4)]
    [InlineData(ServiceFabricCounterMetrics.RemotingMeterName, "service_fabric.remoting.", 7)]
    [InlineData(ServiceFabricCounterMetrics.ActorsMeterName, "service_fabric.actor.", 16)]
    public void CanSubscribeToOneSubsystem(string meterName, string metricPrefix, int expectedCount)
    {
        var expectedValues = CounterCatalog.Categories.SelectMany(c => c.Counters)
            .Select((spec, index) => (spec.Metric, Value: (double)index))
            .ToDictionary(x => x.Metric, x => x.Value);
        var observed = new Dictionary<string, double>();
        var calls = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == meterName)
                currentListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            Assert.Equal(meterName, instrument.Meter.Name);
            Assert.StartsWith(metricPrefix, instrument.Name);
            Assert.Equal(
                new KeyValuePair<string, object?>("service_fabric.partition_id", "partition"),
                Assert.Single(tags.ToArray()));
            observed.Add(instrument.Name, value);
        });
        listener.Start();
        using var meters = new CounterMeterSet(metric =>
        {
            calls++;
            return new[]
            {
                new Measurement<double>(expectedValues[metric],
                    new KeyValuePair<string, object?>("service_fabric.partition_id", "partition")),
            };
        });

        listener.RecordObservableInstruments();

        Assert.Equal(expectedCount, calls);
        Assert.Equal(expectedCount, observed.Count);
        foreach (var measurement in observed)
            Assert.Equal(expectedValues[measurement.Key], measurement.Value);
    }

    [Fact]
    public void DisposesAllSubsystemMetersAndCanBeRestarted()
    {
        var completed = new List<Instrument>();
        var measurements = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (IsServiceFabricMeter(instrument))
                currentListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => measurements++);
        listener.MeasurementsCompleted = (instrument, _) => completed.Add(instrument);
        listener.Start();

        var expectedCount = CounterCatalog.Categories.Sum(c => c.Counters.Length);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            completed.Clear();
            measurements = 0;
            using var meters = new CounterMeterSet(_ => new[] { new Measurement<double>(42) });
            listener.RecordObservableInstruments();
            Assert.Equal(expectedCount, measurements);

            meters.Dispose();
            Assert.Equal(expectedCount, completed.Count);
            Assert.Equal(4, completed.Select(i => i.Meter).Distinct().Count());
            listener.RecordObservableInstruments();
            Assert.Equal(expectedCount, measurements);
        }
    }

    [Fact]
    public void RegistrationsShareMetersUntilTheLastHandleIsDisposed()
    {
        var published = new List<Instrument>();
        var completed = new List<Instrument>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (IsServiceFabricMeter(instrument))
            {
                published.Add(instrument);
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.MeasurementsCompleted = (instrument, _) => completed.Add(instrument);
        listener.Start();

        using var first = ServiceFabricCounterMetrics.StartNodeScope();
        using var second = ServiceFabricCounterMetrics.StartNodeScope();
        var expectedCount = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? CounterCatalog.Categories.Sum(c => c.Counters.Length)
            : 0;
        Assert.Equal(expectedCount, published.Count);

        first.Dispose();
        Assert.Empty(completed);

        second.Dispose();
        Assert.Equal(expectedCount, completed.Count);
    }

    [Fact]
    public void FailedRegistrationDisposesAlreadyCreatedMeters()
    {
        using var failingListener = new MeterListener();
        failingListener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == ServiceFabricCounterMetrics.RemotingMeterName)
                throw new InvalidOperationException("Registration failed.");
        };
        failingListener.Start();

        Assert.Throws<InvalidOperationException>(
            () => new CounterMeterSet(_ => Array.Empty<Measurement<double>>()));

        var remaining = new List<Instrument>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (IsServiceFabricMeter(instrument))
                remaining.Add(instrument);
        };
        listener.Start();
        Assert.Empty(remaining);
    }

    [Theory]
    [InlineData("Service Fabric Transactional Replicator", "{0}:123", "counter_instance,partition_id,replica_id")]
    [InlineData("Service Fabric TStore", "{0}:123:456_789_urn:MyDictionary/dataStore", "counter_instance,partition_id,replica_id,state_provider_id")]
    [InlineData("Service Fabric Service", "{0}_123_456", "counter_instance,partition_id,replica_id")]
    [InlineData("Service Fabric Service Method", "iservice.method_2_{0}_123_456", "counter_instance,method,method_id,partition_id,replica_id")]
    [InlineData("Service Fabric Actor", "{0}_456", "counter_instance,partition_id")]
    [InlineData("Service Fabric Actor Method", "iactor.method_2_{0}_456", "counter_instance,method,method_id,partition_id")]
    public void InstanceTagsUseServiceFabricPrefix(string categoryName, string instanceFormat, string expectedKeys)
    {
        var category = Assert.Single(CounterCatalog.Categories, c => c.Name == categoryName);
        var instanceName = string.Format(instanceFormat, "2740af29-78aa-44bc-a20b-7e60fb783264");
        var parsed = category.Parse(instanceName);

        Assert.True(parsed.HasValue);
        Assert.Equal(
            expectedKeys.Split(',').Select(key => "service_fabric." + key).OrderBy(key => key),
            parsed.Value.Tags.Select(tag => tag.Key).OrderBy(key => key));
    }

    private static bool IsServiceFabricMeter(Instrument instrument)
        => instrument.Meter.Name.StartsWith("ServiceFabric.Counters.", StringComparison.Ordinal);
}
