using CoffeeShopLab2.Shared;

namespace CoffeeShopLab2.Server;

public sealed class ProviderRegistry
{
    private readonly object _gate = new();
    private IOrderClient? _provider;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _provider is not null;
            }
        }
    }

    public bool TryRegister(IOrderClient provider)
    {
        lock (_gate)
        {
            if (_provider is not null)
            {
                return false;
            }

            _provider = provider;
            return true;
        }
    }

    public void Unregister(IOrderClient provider)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_provider, provider))
            {
                _provider = null;
            }
        }
    }

    public void Alert(string product, int left)
    {
        IOrderClient? provider;

        lock (_gate)
        {
            provider = _provider;
        }

        if (provider is null)
        {
            Log.Stock($"{product} below {Stock.LowThreshold} ({left} left), no provider connected");
            return;
        }

        Log.Stock($"{product} below {Stock.LowThreshold} ({left} left), alerting provider {provider.Endpoint}");

        _ = provider.NotifyAsync($"{Protocol.Alert} {product} {left}");
    }
}
