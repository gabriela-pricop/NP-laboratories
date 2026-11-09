namespace CoffeeShopLab2.Shared;

public static class Menu
{
    public const string Coffee = "coffee";
    public const string Tea = "tea";
    public const string Croissant = "croissant";

    private static readonly Dictionary<string, int> PreparationSeconds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Coffee] = 4,
            [Tea] = 3,
            [Croissant] = 2,
        };

    public static readonly IReadOnlyList<string> Products =
        new[] { Coffee, Tea, Croissant };

    public static bool IsProduct(string name) => PreparationSeconds.ContainsKey(name);

    public static bool TryNormalise(string name, out string product)
    {
        foreach (string known in Products)
        {
            if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
            {
                product = known;
                return true;
            }
        }

        product = string.Empty;
        return false;
    }

    public static int SecondsFor(string product) => PreparationSeconds[product];

    public static int SecondsFor(IEnumerable<OrderItem> items)
    {
        int total = 0;
        foreach (OrderItem item in items)
        {
            total += SecondsFor(item.Product) * item.Quantity;
        }

        return total;
    }
}
