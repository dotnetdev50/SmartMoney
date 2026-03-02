using Microsoft.EntityFrameworkCore;
using SmartMoney.Domain.Entities;

namespace SmartMoney.Infrastructure.Persistence;

public class SmartMoneyDbContext : DbContext
{
    public SmartMoneyDbContext(DbContextOptions<SmartMoneyDbContext> options)
        : base(options)
    {
    }

    public DbSet<ParticipantRawData> ParticipantRawData => Set<ParticipantRawData>();
    public DbSet<ParticipantMetric> ParticipantMetrics => Set<ParticipantMetric>();
    public DbSet<MarketBias> MarketBiases => Set<MarketBias>();
    public DbSet<JobRunLog> JobRunLogs => Set<JobRunLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmartMoneyDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}