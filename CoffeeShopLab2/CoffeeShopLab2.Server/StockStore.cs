using CoffeeShopLab2.Shared;

namespace CoffeeShopLab2.Server;

public sealed class StockStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private long _lastWrittenVersion;

    public StockStore(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
    }

    public string FilePath => _path;

    public bool TryLoad(out Dictionary<string, int> amounts)
    {
        amounts = new Dictionary<string, int>(StringComparer.Ordinal);

        if (!File.Exists(_path))
        {
            return false;
        }

        try
        {
            foreach (string raw in File.ReadAllLines(_path))
            {
                string entry = raw.Trim();

                if (entry.Length == 0 || entry.StartsWith('#'))
                {
                    continue;
                }

                int separator = entry.IndexOf('=');

                if (separator <= 0)
                {
                    continue;
                }

                string name = entry[..separator].Trim();
                string value = entry[(separator + 1)..].Trim();

                if (Menu.TryNormalise(name, out string product)
                    && int.TryParse(value, out int amount)
                    && amount >= 0)
                {
                    amounts[product] = amount;
                }
            }
        }
        catch (IOException ex)
        {
            Log.Error("stock", $"cannot read {_path}: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("stock", $"cannot read {_path}: {ex.Message}");
            return false;
        }

        foreach (string product in Menu.Products)
        {
            if (!amounts.ContainsKey(product))
            {
                return false;
            }
        }

        return true;
    }

    public void Save(IReadOnlyDictionary<string, int> amounts, long version)
    {
        lock (_gate)
        {
            if (version <= _lastWrittenVersion)
            {
                return;
            }

            _lastWrittenVersion = version;

            string temporary = _path + ".tmp";

            try
            {
                var lines = new List<string>(Menu.Products.Count);

                foreach (string product in Menu.Products)
                {
                    lines.Add($"{product}={amounts[product]}");
                }

                File.WriteAllLines(temporary, lines);
                File.Move(temporary, _path, overwrite: true);
            }
            catch (IOException ex)
            {
                Log.Error("stock", $"cannot write {_path}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("stock", $"cannot write {_path}: {ex.Message}");
            }
        }
    }
}
