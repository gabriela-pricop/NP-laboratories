using System.Threading.Channels;
using CoffeeShopLab2.Shared;

namespace CoffeeShopLab2.Server;

public readonly record struct OrderResult(
    bool Accepted,
    Order? Order,
    string MissingProduct,
    int MissingLeft);

public sealed class Shop
{
    private readonly Channel<Order> _queue;
    private int _lastOrderNumber;

    public Shop(Stock stock, ProviderRegistry provider)
    {
        Stock = stock;
        Provider = provider;

        _queue = Channel.CreateUnbounded<Order>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public Stock Stock { get; }

    public ProviderRegistry Provider { get; }

    public ChannelReader<Order> Queue => _queue.Reader;

    public OrderResult PlaceOrder(IReadOnlyList<OrderItem> items, IOrderClient client)
    {
        bool taken = Stock.TryTake(
            items,
            out string missingProduct,
            out int missingLeft,
            out List<(string Product, int Left)> fellBelow);

        if (!taken)
        {
            return new OrderResult(false, null, missingProduct, missingLeft);
        }

        var order = new Order(Interlocked.Increment(ref _lastOrderNumber), items, client);

        if (!_queue.Writer.TryWrite(order))
        {
            Stock.Return(items);
            return new OrderResult(false, null, string.Empty, 0);
        }

        foreach ((string product, int left) in fellBelow)
        {
            Provider.Alert(product, left);
        }

        return new OrderResult(true, order, string.Empty, 0);
    }

    public Dictionary<string, int> Deliver(IReadOnlyList<OrderItem> items) => Stock.Add(items);

    public bool Cancel(Order order)
    {
        if (!order.TryCancel())
        {
            return false;
        }

        Stock.Return(order.Items);
        Log.Order(order.Client.Endpoint, $"cancelled {order}, products returned to the stock");
        return true;
    }

    public void Close() => _queue.Writer.TryComplete();
}
