using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartMoney.Domain.Entities;

namespace SmartMoney.Infrastructure.Persistence.Configurations;

public class MarketBiasConfiguration : IEntityTypeConfiguration<MarketBias>
{
    public void Configure(EntityTypeBuilder<MarketBias> builder)
    {
        builder.ToTable("market_bias");

        builder.HasKey(x => x.Id);

        builder.HasIndex(x => x.Date).IsUnique();

        builder.Property(x => x.Date)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(x => x.Regime)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
    }
}