# Advanced Concurrency Patterns

Advanced .NET-native concurrency patterns for pipelines, UI event composition, and stateful worker orchestration.

## Contents

- [TPL Dataflow (Complex Stream Processing)](#tpl-dataflow-complex-stream-processing)
- [Reactive Extensions (UI and Event Composition)](#reactive-extensions-ui-and-event-composition)
- [State Ownership with Hosted Workers + Channels](#state-ownership-with-hosted-workers--channels)
- [Prefer Async Local Functions](#prefer-async-local-functions)

## TPL Dataflow (Complex Stream Processing)

**Use for:** backpressure, batching, throttling, multi-stage pipelines.

```csharp
using System.Threading.Tasks.Dataflow;

var options = new ExecutionDataflowBlockOptions
{
    MaxDegreeOfParallelism = Environment.ProcessorCount,
    BoundedCapacity = 256
};

var transform = new TransformBlock<Order, ProcessedOrder>(
    async order => await ProcessOrderAsync(order),
    options);

var writer = new ActionBlock<ProcessedOrder>(
    async processed => await PersistAsync(processed),
    new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 4, BoundedCapacity = 256 });

transform.LinkTo(writer, new DataflowLinkOptions { PropagateCompletion = true });

foreach (var order in orders)
{
    await transform.SendAsync(order);
}

transform.Complete();
await writer.Completion;
```

**Why Dataflow:** bounded capacity gives natural backpressure and predictable memory use.

## Reactive Extensions (UI and Event Composition)

**Use for:** debounce/throttle/combine semantics in interactive or event-driven clients.

```csharp
using System.Reactive.Linq;

var searchResults = searchTextChanged
    .Throttle(TimeSpan.FromMilliseconds(250))
    .DistinctUntilChanged()
    .Where(text => text.Length >= 2)
    .SelectMany(text => SearchAsync(text).ToObservable());
```

## State Ownership with Hosted Workers + Channels

**Use for:** stateful sequential processing without lock-heavy designs.

```csharp
public sealed class StatefulWorker : BackgroundService
{
    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(
        new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = false });

    private readonly Dictionary<string, State> _state = new();

    public ValueTask EnqueueAsync(Command command, CancellationToken ct)
        => _commands.Writer.WriteAsync(command, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var command in _commands.Reader.ReadAllAsync(stoppingToken))
        {
            Handle(command); // single-reader ownership of mutable state
        }
    }

    private void Handle(Command command)
    {
        // mutate _state safely in one place
    }
}
```

## Prefer Async Local Functions

Named async local functions are easier to debug and reason about than deeply nested async lambdas.

```csharp
private void Handle(StartCommand cmd)
{
    async Task<Result> ExecuteAsync()
    {
        var data = await LoadAsync(cmd.Id);
        return await ProcessAsync(data);
    }

    _ = ExecuteAsync();
}
```
