using CallTree.Domain.Calls;
using CallTree.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace CallTree.Infrastructure.Persistence;

public class CallTreeDbContext(DbContextOptions<CallTreeDbContext> options) : DbContext(options)
{
    public DbSet<Call> Calls => Set<Call>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Applies to every DateTimeOffset in the model, nullable ones included. See the converter for
        // why: without it SQLite cannot ORDER BY or range-filter any of our timestamps.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Call>(call =>
        {
            call.HasKey(c => c.Id);
            call.Ignore(c => c.DomainEvents);
            call.Property(c => c.Source).HasConversion<string>();
            call.Property(c => c.SourceClassification).HasConversion<string>();
            call.Property(c => c.Status).HasConversion<string>();
            call.Property(c => c.TerminationReason).HasMaxLength(500);

            call.HasMany(c => c.Legs)
                .WithOne()
                .HasForeignKey("CallId")
                .OnDelete(DeleteBehavior.Cascade);
            call.Navigation(c => c.Legs).UsePropertyAccessMode(PropertyAccessMode.Field);

            call.HasOne(c => c.Recording)
                .WithOne()
                .HasForeignKey<Recording>("CallId")
                .OnDelete(DeleteBehavior.Cascade);

            call.HasIndex(c => c.StartedAt);
            call.HasIndex(c => c.Source);
        });

        modelBuilder.Entity<CallLeg>(leg =>
        {
            leg.HasKey(l => l.Id);
            leg.Property(l => l.Direction).HasConversion<string>();
            leg.Property(l => l.HangupInitiator).HasConversion<string>();
            leg.Property(l => l.RawCallerId).HasMaxLength(256);
            leg.Property(l => l.SipCallId).HasMaxLength(256);
            leg.Property(l => l.RemoteNumber)
                .HasConversion(v => v!.Value, v => PhoneNumber.Parse(v))
                .HasMaxLength(20);
        });

        modelBuilder.Entity<Recording>(recording =>
        {
            recording.HasKey(r => r.Id);
            recording.Property(r => r.ChannelLayout).HasConversion<string>();
            recording.Property(r => r.FilePath).HasMaxLength(1024);
        });
    }
}
