using CoffeeShopLab2.Shared;

namespace CoffeeShopLab2.Server;

public enum OrderState
{
    Waiting,
    Preparing,
    Ready,
    Cancelled,
}

public interface IOrderClient
{
    string Endpoint { get; }

    Task<bool> NotifyAsync(string line);
}

public sealed class Order
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private OrderState _state = OrderState.Waiting;

    public Order(int number, IReadOnlyList<OrderItem> items, IOrderClient client)
    {
        Number = number;
        Items = items;
        Client = client;
        PreparationSeconds = Menu.SecondsFor(items);
    }

    public int Number { get; }

    public IReadOnlyList<OrderItem> Items { get; }

    public IOrderClient Client { get; }

    public int PreparationSeconds { get; }

    public CancellationToken Token => _cancellation.Token;

    public OrderState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool TryStartPreparing()
    {
        lock (_gate)
        {
            if (_state != OrderState.Waiting)
            {
                return false;
            }

            _state = OrderState.Preparing;
            return true;
        }
    }

    public bool TryComplete()
    {
        lock (_gate)
        {
            if (_state != OrderState.Preparing)
            {
                return false;
            }

            _state = OrderState.Ready;
            return true;
        }
    }

    public bool TryCancel()
    {
        lock (_gate)
        {
            if (_state is OrderState.Ready or OrderState.Cancelled)
            {
                return false;
            }

            _state = OrderState.Cancelled;
        }

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return true;
        }

        return true;
    }

    public override string ToString() =>
        $"#{Number} [{string.Join(", ", Items)}] {PreparationSeconds}s";
}
