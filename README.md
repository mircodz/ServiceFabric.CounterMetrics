# ServiceFabric.CounterMetrics

Emits Service Fabric Reliable Services and Actors performance counters as `System.Diagnostics.Metrics` instruments.

## Installation

```sh
dotnet add package ServiceFabric.CounterMetrics
```

## Usage

Register the meters once at host startup, alongside your metrics exporter:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using ServiceFabric.CounterMetrics;

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter(ServiceFabricCounterMetrics.ReplicatorMeterName)
    .AddMeter(ServiceFabricCounterMetrics.TStoreMeterName)
    .AddMeter(ServiceFabricCounterMetrics.RemotingMeterName)
    .AddMeter(ServiceFabricCounterMetrics.ActorsMeterName)
    .Build();
```

In a stateful service, collect for the replica's lifetime:

```csharp
using System;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Runtime;
using ServiceFabric.CounterMetrics;

sealed class MyService : StatefulService
{
    private IDisposable? _counters;

    public MyService(StatefulServiceContext context) : base(context) { }

    protected override async Task OnOpenAsync(ReplicaOpenMode openMode, CancellationToken cancellationToken)
    {
        await base.OnOpenAsync(openMode, cancellationToken);
        _counters = ServiceFabricCounterMetrics.Start(Context);
    }

    protected override Task OnCloseAsync(CancellationToken cancellationToken)
    {
        _counters?.Dispose();
        return base.OnCloseAsync(cancellationToken);
    }

    protected override void OnAbort()
    {
        _counters?.Dispose();
        base.OnAbort();
    }
}
```
