using CoffeeShopLab2.Shared;

namespace CoffeeShopLab2.Server;

public sealed class WorkerPool
{
    private readonly Shop _shop;
    private readonly Task[] _workers;

    public WorkerPool(Shop shop, int size)
    {
        _shop = shop;
        Size = size;
        _workers = new Task[size];
    }

    public int Size { get; }

    public void Start(CancellationToken stopping)
    {
        for (int i = 0; i < Size; i++)
        {
            int id = i + 1;
            _workers[i] = Task.Run(() => RunAsync(id, stopping), CancellationToken.None);
        }

        Log.Server($"{Size} workers started, one per logical processor");
    }

    public Task WhenAllStopped() => Task.WhenAll(_workers);

    private async Task RunAsync(int id, CancellationToken stopping)
    {
        try
        {
            await foreach (Order order in _shop.Queue.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                await PrepareAsync(id, order, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Worker($"worker {id} stopping");
        }
        catch (Exception ex)
        {
            Log.Error("worker", $"worker {id} failed: {ex.Message}");
        }

        Log.Worker($"worker {id} stopped");
    }

    private async Task PrepareAsync(int id, Order order, CancellationToken stopping)
    {
        if (!order.TryStartPreparing())
        {
            Log.Worker($"worker {id} dropped cancelled {order}");
            return;
        }

        Log.Worker($"worker {id} preparing {order} for {order.Client.Endpoint}");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(order.Token, stopping);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(order.PreparationSeconds), linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Worker($"worker {id} stopped preparing {order}");
            return;
        }

        if (!order.TryComplete())
        {
            Log.Worker($"worker {id} finished {order} but its client is gone");
            return;
        }

        Log.Order(order.Client.Endpoint, $"ready {order}");

        await order.Client.NotifyAsync($"{Protocol.Ready} {order.Number}").ConfigureAwait(false);
    }
}
