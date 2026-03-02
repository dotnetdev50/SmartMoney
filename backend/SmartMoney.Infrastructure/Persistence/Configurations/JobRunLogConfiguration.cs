using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartMoney.Domain.Entities;

namespace SmartMoney.Infrastructure.Persistence.Configurations;

public class JobRunLogConfiguration : IEntityTypeConfiguration<JobRunLog>
{
    public void Configure(EntityTypeBuilder<JobRunLog> builder)
    {
        builder.ToTable("job_run_log");
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.JobName, x.Date });
        builder.Property(x => x.JobName).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Date).HasColumnType("date").IsRequired();
        builder.Property(x => x.Message).HasMaxLength(2000);
    }
}