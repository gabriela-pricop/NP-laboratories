using System.Net;
using CoffeeShopLab2.Shared;

namespace CoffeeShopLab2.Server;

public static class Program
{
    private const int DefaultPort = 5050;
    private const string DefaultStockFile = "stock.state";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("-h") || args.Contains("--help"))
        {
            Usage();
            return 0;
        }

        int port = DefaultPort;
        bool portFound = false;
        string stockFile = DefaultStockFile;
        bool loopbackOnly = false;
        bool fresh = false;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];

            switch (argument)
            {
                case "--loopback":
                    loopbackOnly = true;
                    continue;

                case "--fresh":
                    fresh = true;
                    continue;

                case "--stock":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--stock needs a file name");
                        return 1;
                    }

                    stockFile = args[++i];
                    continue;
            }

            if (argument.StartsWith('-'))
            {
                Console.Error.WriteLine($"unknown option '{argument}'");
                Usage();
                return 1;
            }

            if (portFound)
            {
                Console.Error.WriteLine($"unexpected argument '{argument}'");
                return 1;
            }

            if (!int.TryParse(argument, out port))
            {
                Console.Error.WriteLine($"'{argument}' is not a port number");
                return 1;
            }

            portFound = true;
        }

        if (port is < 1 or > 65535)
        {
            Console.Error.WriteLine("the port must be between 1 and 65535");
            return 1;
        }

        var store = new StockStore(stockFile);
        Stock stock;

        if (!fresh && store.TryLoad(out Dictionary<string, int> saved))
        {
            stock = new Stock(saved, store);
            Log.Server($"stock restored from {store.FilePath}");
        }
        else
        {
            stock = new Stock(store);
            Log.Server($"new stock, saved to {store.FilePath}");
            stock.PersistNow();
        }

        var shop = new Shop(stock, new ProviderRegistry());
        var workers = new WorkerPool(shop, Environment.ProcessorCount);

        using var stopping = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Server("stop requested");
            stopping.Cancel();
        };

        Log.Server($"stock at opening: {Protocol.FormatStock(stock.Snapshot())}");
        workers.Start(stopping.Token);

        IPAddress address = loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        var host = new ServerHost(new IPEndPoint(address, port), shop);

        await host.RunAsync(stopping.Token).ConfigureAwait(false);

        shop.Close();
        stopping.Cancel();
        await workers.WhenAllStopped().ConfigureAwait(false);

        stock.PersistNow();
        Log.Server($"closed, stock: {Protocol.FormatStock(stock.Snapshot())}");
        return 0;
    }

    private static void Usage()
    {
        Console.WriteLine("usage: CoffeeShopLab2.Server [port] [--loopback] [--stock <file>] [--fresh]");
        Console.WriteLine($"  port            port to listen on (default {DefaultPort})");
        Console.WriteLine("  --loopback      accept connections from this machine only");
        Console.WriteLine($"  --stock <file>  where the stock is saved (default {DefaultStockFile})");
        Console.WriteLine("  --fresh         ignore the saved stock and start from random amounts");
    }
}
