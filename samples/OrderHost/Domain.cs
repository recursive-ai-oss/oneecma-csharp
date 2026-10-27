using RecursiveAI.OneEcma;

namespace Shop;

/// <summary>Where an order is in its life. In script it is the member name as a string.</summary>
[JsExport]
public enum OrderStatus
{
    Pending,
    Paid,
    Shipped,
}

/// <summary>One line of an order. Records are copied by value: script passes and receives plain objects.</summary>
[JsExport]
public record LineItem(string Sku, int Quantity, decimal Price);

/// <summary>An order. <c>status</c> and <c>note</c> may be left out from script.</summary>
[JsExport(RejectUnknownProperties = true)]
public record Order(int Id, List<LineItem> LineItems, OrderStatus Status = OrderStatus.Pending, string? Note = null);

/// <summary>The order service script talks to. A class is shared by reference: script holds a handle and calls its methods.</summary>
[JsExport]
public class OrderApi
{
    private readonly Dictionary<int, Order> _orders = [];

    public OrderApi()
    {
        _orders[1] = new Order(1, [new LineItem("pen", 3, 1.5m), new LineItem("ink", 1, 7.25m)]);
        _orders[2] = new Order(2, [new LineItem("book", 2, 12m)], OrderStatus.Paid);
    }

    /// <summary>How many orders there are.</summary>
    public int Count => _orders.Count;

    /// <summary>Looks an order up; throws KeyNotFoundException for an unknown id.</summary>
    public Order GetOrder(int id) => _orders.TryGetValue(id, out Order? o) ? o : throw new KeyNotFoundException($"No order with id {id}");

    /// <summary>Stores an order and returns it.</summary>
    public Order AddOrder(Order order)
    {
        _orders[order.Id] = order;
        return order;
    }

    /// <summary>Tax added to every total, as a fraction (0.2 is 20%). Script can read and change it.</summary>
    public decimal TaxRate { get; set; }

    /// <summary>Sum of quantity times price over the lines, plus tax.</summary>
    public decimal Total(Order order) => order.LineItems.Sum(i => i.Quantity * i.Price) * (1 + TaxRate);

    /// <summary>Fetches an order after a pause, as a remote service would. Script gets a promise.</summary>
    public async Task<Order> FetchOrderAsync(int id)
    {
        await Task.Delay(5);
        return GetOrder(id);
    }

    /// <summary>The orders in a given status, or all of them. Script gets an iterator.</summary>
    public IEnumerable<Order> List(OrderStatus? status = null) => _orders.Values.Where(o => status is null || o.Status == status).OrderBy(o => o.Id);

    /// <summary>Calls <paramref name="visit"/> for every order.</summary>
    public void ForEach(Action<Order> visit)
    {
        foreach (Order o in List())
        {
            visit(o);
        }
    }

    /// <summary>The first order the predicate accepts, or null.</summary>
    public Order? Find(Func<Order, bool> predicate) => List().FirstOrDefault(predicate);

    /// <summary>Awaits a script async callback for every order and adds up the results.</summary>
    public async Task<decimal> SumAsync(Func<Order, Task<decimal>> amount)
    {
        decimal sum = 0;
        foreach (Order o in List())
        {
            sum += await amount(o);
        }

        return sum;
    }

    /// <summary>Two overloads told apart by the kind of the argument.</summary>
    public string Describe(int id) => "order #" + id + " of " + Count;

    /// <summary>Overload for a name.</summary>
    public string Describe(string name) => "order named " + name + " of " + Count;
}
