using CoffeeShopLab2.Shared;

namespace CoffeeShopLab2.Server;

public sealed class Stock
{
    public const int LowThreshold = 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _amounts = new(StringComparer.Ordinal);
    private readonly StockStore? _store;
    private long _version;

    public Stock(StockStore? store = null)
    {
        _store = store;

        var random = new Random();

        foreach (string product in Menu.Products)
        {
            _amounts[product] = random.Next(10, 51);
        }
    }

    public Stock(IReadOnlyDictionary<string, int> initial, StockStore? store = null)
    {
        _store = store;

        foreach (string product in Menu.Products)
        {
            _amounts[product] = initial.TryGetValue(product, out int amount) && amount >= 0
                ? amount
                : 0;
        }
    }

    public Dictionary<string, int> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, int>(_amounts, StringComparer.Ordinal);
        }
    }

    public bool TryTake(
        IReadOnlyList<OrderItem> items,
        out string missingProduct,
        out int missingLeft,
        out List<(string Product, int Left)> fellBelowThreshold)
    {
        missingProduct = string.Empty;
        missingLeft = 0;
        fellBelowThreshold = new List<(string, int)>();

        Dictionary<string, int> snapshot;
        long version;

        lock (_gate)
        {
            foreach (OrderItem item in items)
            {
                int available = _amounts[item.Product];

                if (available < item.Quantity)
                {
                    missingProduct = item.Product;
                    missingLeft = available;
                    return false;
                }
            }

            foreach (OrderItem item in items)
            {
                int before = _amounts[item.Product];
                int after = before - item.Quantity;
                _amounts[item.Product] = after;

                if (before >= LowThreshold && after < LowThreshold)
                {
                    fellBelowThreshold.Add((item.Product, after));
                }
            }

            snapshot = new Dictionary<string, int>(_amounts, StringComparer.Ordinal);
            version = ++_version;
        }

        Persist(snapshot, version);
        return true;
    }

    public void Return(IReadOnlyList<OrderItem> items)
    {
        Dictionary<string, int> snapshot;
        long version;

        lock (_gate)
        {
            foreach (OrderItem item in items)
            {
                _amounts[item.Product] += item.Quantity;
            }

            snapshot = new Dictionary<string, int>(_amounts, StringComparer.Ordinal);
            version = ++_version;
        }

        Persist(snapshot, version);
    }

    public Dictionary<string, int> Add(IReadOnlyList<OrderItem> items)
    {
        Dictionary<string, int> snapshot;
        long version;

        lock (_gate)
        {
            foreach (OrderItem item in items)
            {
                _amounts[item.Product] += item.Quantity;
            }

            snapshot = new Dictionary<string, int>(_amounts, StringComparer.Ordinal);
            version = ++_version;
        }

        Persist(snapshot, version);
        return snapshot;
    }

    public List<(string Product, int Left)> BelowThreshold()
    {
        var low = new List<(string, int)>();

        lock (_gate)
        {
            foreach (string product in Menu.Products)
            {
                int amount = _amounts[product];

                if (amount < LowThreshold)
                {
                    low.Add((product, amount));
                }
            }
        }

        return low;
    }

    public void PersistNow()
    {
        Dictionary<string, int> snapshot;
        long version;

        lock (_gate)
        {
            snapshot = new Dictionary<string, int>(_amounts, StringComparer.Ordinal);
            version = ++_version;
        }

        Persist(snapshot, version);
    }

    private void Persist(IReadOnlyDictionary<string, int> snapshot, long version)
    {
        _store?.Save(snapshot, version);
    }
}
