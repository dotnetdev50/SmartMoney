using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartMoney.Domain.Entities;

namespace SmartMoney.Infrastructure.Persistence.Configurations;

public class ParticipantMetricConfiguration : IEntityTypeConfiguration<ParticipantMetric>
{
    public void Configure(EntityTypeBuilder<ParticipantMetric> builder)
    {
        builder.ToTable("participant_metrics");

        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.Date, x.Participant }).IsUnique();

        builder.Property(x => x.Date)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(x => x.Participant)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
    }
}