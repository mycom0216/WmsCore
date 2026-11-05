using Microsoft.EntityFrameworkCore;
using WmsCore.Api.Domain;

namespace WmsCore.Api.Data;

public class WmsDbContext(DbContextOptions<WmsDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<StockMovement> Movements => Set<StockMovement>();
    public DbSet<InboundReceipt> Receipts => Set<InboundReceipt>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<AppUser> Users => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.Property(x => x.Sku).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Sku).IsUnique();
        });
        b.Entity<Location>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
        });
        b.Entity<StockItem>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.LocationId }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_StockItem_Quantity", "Quantity >= 0")); // 최후의 안전망: 음수 재고 불가
        });
        b.Entity<StockMovement>(e =>
        {
            e.Property(x => x.Reference).HasMaxLength(60).IsRequired();
            e.HasIndex(x => new { x.ProductId, x.CreatedAt });
        });
        b.Entity<InboundReceipt>(e =>
        {
            e.Property(x => x.ReferenceNo).HasMaxLength(60).IsRequired();
            e.HasIndex(x => x.ReferenceNo).IsUnique();
        });
        b.Entity<Order>(e =>
        {
            e.Property(x => x.OrderNo).HasMaxLength(60).IsRequired();
            e.HasIndex(x => x.OrderNo).IsUnique();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId);
        });
        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.UserName).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.UserName).IsUnique();
        });
    }
}
