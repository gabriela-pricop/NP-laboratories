using System.Text;

namespace CoffeeShopLab2.Shared;

public static class Protocol
{
    public const byte Terminator = (byte)'\n';

    public const int MaxLineLength = 1024;

    public const int MaxQuantityPerProduct = 1000;

    public const string Hello = "HELLO";
    public const string MenuCmd = "MENU";
    public const string Order = "ORDER";
    public const string Deliver = "DELIVER";
    public const string Ping = "PING";
    public const string Quit = "QUIT";

    public const string RoleClient = "CLIENT";
    public const string RoleProvider = "PROVIDER";

    public const string Welcome = "WELCOME";
    public const string Accepted = "ACCEPTED";
    public const string Refused = "REFUSED";
    public const string Ready = "READY";
    public const string Delivered = "DELIVERED";
    public const string Alert = "ALERT";
    public const string Pong = "PONG";
    public const string Bye = "BYE";
    public const string Error = "ERROR";

    public static string FormatStock(IReadOnlyDictionary<string, int> stock)
    {
        return string.Join(' ', Menu.Products.Select(product => $"{product}={stock[product]}"));
    }

    public static bool TryParseItems(
        IReadOnlyList<string> tokens,
        int start,
        out List<OrderItem> items,
        out string error)
    {
        items = new List<OrderItem>();
        error = string.Empty;

        int count = tokens.Count - start;

        if (count <= 0)
        {
            error = "expected at least one product and quantity";
            return false;
        }

        if (count % 2 != 0)
        {
            error = "every product needs a quantity";
            return false;
        }

        var merged = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = start; i < tokens.Count; i += 2)
        {
            if (!Menu.TryNormalise(tokens[i], out string product))
            {
                error = $"unknown product '{Sanitise(tokens[i])}'";
                return false;
            }

            if (!int.TryParse(tokens[i + 1], out int quantity) || quantity <= 0)
            {
                error = $"quantity for '{product}' must be a positive whole number";
                return false;
            }

            if (quantity > MaxQuantityPerProduct)
            {
                error = $"quantity for '{product}' is too large (max {MaxQuantityPerProduct})";
                return false;
            }

            merged[product] = merged.TryGetValue(product, out int already)
                ? already + quantity
                : quantity;

            if (merged[product] > MaxQuantityPerProduct)
            {
                error = $"quantity for '{product}' is too large (max {MaxQuantityPerProduct})";
                return false;
            }
        }

        foreach (string product in Menu.Products)
        {
            if (merged.TryGetValue(product, out int quantity))
            {
                items.Add(new OrderItem(product, quantity));
            }
        }

        return true;
    }

    public static string Sanitise(string text)
    {
        const int limit = 40;
        var builder = new StringBuilder(Math.Min(text.Length, limit));

        foreach (char c in text)
        {
            if (builder.Length == limit)
            {
                builder.Append('.');
                builder.Append('.');
                builder.Append('.');
                break;
            }

            builder.Append(char.IsControl(c) ? '?' : c);
        }

        return builder.ToString();
    }
}
