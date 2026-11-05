namespace WmsCore.Api.Domain;

public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
}

public class Location
{
    public int Id { get; set; }
    /// <summary>창고 위치 코드. 예: A-01-03 (구역-랙-단)</summary>
    public string Code { get; set; } = "";
}

/// <summary>상품 x 위치별 현재고. (ProductId, LocationId)는 유일.</summary>
public class StockItem
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public int Quantity { get; set; }
}

public enum MovementType { Inbound = 1, Allocate = 2, Release = 3 }

/// <summary>재고 수불 원장. 재고가 왜 이 값인지 추적할 수 있게 모든 증감을 남긴다.</summary>
public class StockMovement
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public int LocationId { get; set; }
    public MovementType Type { get; set; }
    /// <summary>항상 양수. 방향은 Type으로 판단.</summary>
    public int Quantity { get; set; }
    public string Reference { get; set; } = "";
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>입고 전표. ReferenceNo 유일 제약으로 같은 입고가 두 번 반영되는 것을 막는다(멱등성).</summary>
public class InboundReceipt
{
    public int Id { get; set; }
    public string ReferenceNo { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
}

public enum OrderStatus { Created = 1, Shipped = 2, Cancelled = 3 }

public class Order
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

public class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int LocationId { get; set; }
    public int Quantity { get; set; }
}

public class AppUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Operator;
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
}
