namespace CoffeeShopLab2.Server;

public static class Log
{
    private static readonly object Gate = new();
    private static readonly DateTime Start = DateTime.Now;

    public static void Line(string category, string message)
    {
        string stamp = (DateTime.Now - Start).ToString(@"hh\:mm\:ss\.fff");

        lock (Gate)
        {
            Console.WriteLine($"{stamp}  {category,-9}  {message}");
        }
    }

    public static void Connection(string endpoint, string message) =>
        Line("conn", $"{endpoint}  {message}");

    public static void Order(string endpoint, string message) =>
        Line("order", $"{endpoint}  {message}");

    public static void Delivery(string endpoint, string message) =>
        Line("delivery", $"{endpoint}  {message}");

    public static void Stock(string message) =>
        Line("stock", message);

    public static void Worker(string message) =>
        Line("worker", message);

    public static void Error(string endpoint, string message) =>
        Line("error", $"{endpoint}  {message}");

    public static void Server(string message) =>
        Line("server", message);
}
