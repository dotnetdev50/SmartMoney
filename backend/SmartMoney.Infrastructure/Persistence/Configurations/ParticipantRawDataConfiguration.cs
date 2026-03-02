using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartMoney.Domain.Entities;

namespace SmartMoney.Infrastructure.Persistence.Configurations;

public class ParticipantRawDataConfiguration : IEntityTypeConfiguration<ParticipantRawData>
{
    public void Configure(EntityTypeBuilder<ParticipantRawData> builder)
    {
        builder.ToTable("participant_raw_data");

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