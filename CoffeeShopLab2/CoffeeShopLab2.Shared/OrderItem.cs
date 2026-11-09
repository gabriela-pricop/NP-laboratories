namespace CoffeeShopLab2.Shared;

public readonly record struct OrderItem(string Product, int Quantity)
{
    public override string ToString() => $"{Quantity} x {Product}";
}
