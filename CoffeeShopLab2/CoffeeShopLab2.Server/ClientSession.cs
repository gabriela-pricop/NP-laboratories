using System.Collections.Concurrent;
using CoffeeShopLab2.Shared;

namespace CoffeeShopLab2.Server;

public sealed class ClientSession : IOrderClient
{
    private readonly LineSocket _socket;
    private readonly Shop _shop;
    private readonly ConcurrentDictionary<int, Order> _orders = new();

    private volatile bool _isProvider;

    public ClientSession(LineSocket socket, Shop shop)
    {
        _socket = socket;
        _shop = shop;
    }

    public string Endpoint => _socket.Endpoint;

    public Task<bool> NotifyAsync(string line) => _socket.SendLineAsync(line);

    public async Task RunAsync(CancellationToken stopping)
    {
        Log.Connection(Endpoint, "connected");

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                LineResult result = await _socket.ReceiveLineAsync(stopping).ConfigureAwait(false);

                if (result.Kind == LineKind.Closed)
                {
                    break;
                }

                if (result.Kind == LineKind.TooLong)
                {
                    Log.Error(Endpoint, $"message longer than {Protocol.MaxLineLength} bytes, dropped");

                    await SendAsync($"{Protocol.Error} message too long (max {Protocol.MaxLineLength} bytes)")
                        .ConfigureAwait(false);

                    continue;
                }

                bool keepGoing = await HandleAsync(result.Text).ConfigureAwait(false);

                if (!keepGoing)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Connection(Endpoint, "closed by the server");
        }
        catch (Exception ex)
        {
            Log.Error(Endpoint, $"session failed: {ex.Message}");
        }
        finally
        {
            Cleanup();
        }
    }

    private async Task<bool> HandleAsync(string line)
    {
        string[] tokens = line.Split(
            new[] { ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return true;
        }

        string command = tokens[0].ToUpperInvariant();

        switch (command)
        {
            case Protocol.Hello:
                return await HandleHelloAsync(tokens).ConfigureAwait(false);

            case Protocol.MenuCmd:
                await SendAsync($"{Protocol.MenuCmd} {Protocol.FormatStock(_shop.Stock.Snapshot())}")
                    .ConfigureAwait(false);
                return true;

            case Protocol.Order:
                return await HandleOrderAsync(tokens).ConfigureAwait(false);

            case Protocol.Deliver:
                return await HandleDeliverAsync(tokens).ConfigureAwait(false);

            case Protocol.Ping:
                await SendAsync(Protocol.Pong).ConfigureAwait(false);
                return true;

            case Protocol.Quit:
                await SendAsync(Protocol.Bye).ConfigureAwait(false);
                return false;

            default:
                Log.Error(Endpoint, $"unknown command '{Protocol.Sanitise(tokens[0])}'");

                await SendAsync($"{Protocol.Error} unknown command '{Protocol.Sanitise(tokens[0])}'")
                    .ConfigureAwait(false);

                return true;
        }
    }

    private async Task<bool> HandleHelloAsync(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2)
        {
            await SendAsync($"{Protocol.Error} HELLO needs a role: {Protocol.RoleClient} or {Protocol.RoleProvider}")
                .ConfigureAwait(false);

            return true;
        }

        string role = tokens[1].ToUpperInvariant();

        if (role == Protocol.RoleClient)
        {
            if (_isProvider)
            {
                _shop.Provider.Unregister(this);
                _isProvider = false;
            }

            Log.Connection(Endpoint, "role: client");
            await SendAsync($"{Protocol.Welcome} {Protocol.RoleClient}").ConfigureAwait(false);
            return true;
        }

        if (role == Protocol.RoleProvider)
        {
            if (_isProvider)
            {
                await SendAsync($"{Protocol.Welcome} {Protocol.RoleProvider}").ConfigureAwait(false);
                return true;
            }

            if (!_shop.Provider.TryRegister(this))
            {
                Log.Error(Endpoint, "refused as provider, another provider is connected");
                await SendAsync($"{Protocol.Error} a provider is already connected").ConfigureAwait(false);
                return true;
            }

            _isProvider = true;
            Log.Connection(Endpoint, "role: provider");
            await SendAsync($"{Protocol.Welcome} {Protocol.RoleProvider}").ConfigureAwait(false);

            foreach ((string product, int left) in _shop.Stock.BelowThreshold())
            {
                await SendAsync($"{Protocol.Alert} {product} {left}").ConfigureAwait(false);
            }

            return true;
        }

        await SendAsync($"{Protocol.Error} unknown role '{Protocol.Sanitise(tokens[1])}'").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleOrderAsync(IReadOnlyList<string> tokens)
    {
        if (!Protocol.TryParseItems(tokens, 1, out List<OrderItem> items, out string error))
        {
            Log.Error(Endpoint, $"bad order: {error}");
            await SendAsync($"{Protocol.Error} {error}").ConfigureAwait(false);
            return true;
        }

        ForgetFinishedOrders();

        OrderResult result = _shop.PlaceOrder(items, this);

        if (!result.Accepted)
        {
            if (result.MissingProduct.Length == 0)
            {
                await SendAsync($"{Protocol.Error} the shop is closing").ConfigureAwait(false);
                return false;
            }

            Log.Order(Endpoint, $"refused [{string.Join(", ", items)}], only {result.MissingLeft} {result.MissingProduct} left");

            await SendAsync($"{Protocol.Refused} {result.MissingProduct} {result.MissingLeft}")
                .ConfigureAwait(false);

            return true;
        }

        Order order = result.Order!;
        _orders[order.Number] = order;

        Log.Order(Endpoint, $"accepted {order}");

        await SendAsync($"{Protocol.Accepted} {order.Number} {order.PreparationSeconds}")
            .ConfigureAwait(false);

        return true;
    }

    private async Task<bool> HandleDeliverAsync(IReadOnlyList<string> tokens)
    {
        if (!_isProvider)
        {
            Log.Error(Endpoint, "a client tried to deliver");
            await SendAsync($"{Protocol.Error} only the provider can deliver").ConfigureAwait(false);
            return true;
        }

        if (!Protocol.TryParseItems(tokens, 1, out List<OrderItem> items, out string error))
        {
            Log.Error(Endpoint, $"bad delivery: {error}");
            await SendAsync($"{Protocol.Error} {error}").ConfigureAwait(false);
            return true;
        }

        Dictionary<string, int> stock = _shop.Deliver(items);

        Log.Delivery(Endpoint, $"delivered [{string.Join(", ", items)}], stock now {Protocol.FormatStock(stock)}");
        await SendAsync($"{Protocol.Delivered} {Protocol.FormatStock(stock)}").ConfigureAwait(false);
        return true;
    }

    private Task<bool> SendAsync(string line) => _socket.SendLineAsync(line);

    private void ForgetFinishedOrders()
    {
        foreach (KeyValuePair<int, Order> entry in _orders)
        {
            if (entry.Value.State is OrderState.Ready or OrderState.Cancelled)
            {
                _orders.TryRemove(entry.Key, out _);
            }
        }
    }

    private void Cleanup()
    {
        if (_isProvider)
        {
            _shop.Provider.Unregister(this);
            _isProvider = false;
            Log.Connection(Endpoint, "provider gone, the shop goes on with the stock it has");
        }

        int cancelled = 0;

        foreach (Order order in _orders.Values)
        {
            if (_shop.Cancel(order))
            {
                cancelled++;
            }
        }

        _orders.Clear();

        Log.Connection(Endpoint, cancelled == 0
            ? "disconnected"
            : $"disconnected, {cancelled} order(s) cancelled");

        _socket.Dispose();
    }
}
