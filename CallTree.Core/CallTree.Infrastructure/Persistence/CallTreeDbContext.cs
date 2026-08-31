using CallTree.Domain.Calls;
using CallTree.Domain.Messages;
using CallTree.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace CallTree.Infrastructure.Persistence;

public class CallTreeDbContext(DbContextOptions<CallTreeDbContext> options) : DbContext(options)
{
    public DbSet<Call> Calls => Set<Call>();

    public DbSet<Message> Messages => Set<Message>();

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
            // Every Id in this model is assigned client-side (Guid.NewGuid() in the domain constructor),
            // never by the database. Without this, EF's default heuristic for a Guid key with a non-default
            // value treats an entity discovered via navigation fix-up (e.g. a Recording attached to an
            // already-tracked Call outside the initial AddAsync graph) as pre-existing and emits an UPDATE
            // instead of an INSERT - which affects 0 rows and throws DbUpdateConcurrencyException.
            call.Property(c => c.Id).ValueGeneratedNever();
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
            leg.Property(l => l.Id).ValueGeneratedNever();
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
            recording.Property(r => r.Id).ValueGeneratedNever();
            recording.Property(r => r.ChannelLayout).HasConversion<string>();
            recording.Property(r => r.FilePath).HasMaxLength(1024);
            recording.Property(r => r.Name).HasMaxLength(RecordingName.MaxLength);
        });

        modelBuilder.Entity<Message>(message =>
        {
            message.HasKey(m => m.Id);
            message.Property(m => m.Id).ValueGeneratedNever();
            message.Ignore(m => m.DomainEvents);
            message.Property(m => m.Source).HasConversion<string>();
            message.Property(m => m.Status).HasConversion<string>();
            message.Property(m => m.Body).HasMaxLength(SmsText.MaxLength);
            message.Property(m => m.FailureReason).HasMaxLength(500);
            message.Property(m => m.ProviderMessageId).HasMaxLength(128);

            message.Property(m => m.From)
                .HasConversion(v => v.Value, v => PhoneNumber.Parse(v))
                .HasMaxLength(20);
            message.Property(m => m.To)
                .HasConversion(v => v.Value, v => PhoneNumber.Parse(v))
                .HasMaxLength(20);

            message.HasOne(m => m.Relay)
                .WithOne()
                .HasForeignKey<Relay>("MessageId")
                .OnDelete(DeleteBehavior.Cascade);

            // Unique, not merely indexed: this is what makes taking a message in idempotent. The
            // provider retries a webhook on any non-2xx and on a slow response, and a duplicated
            // forward costs money and buzzes the operator a second time. MessageRepository.ExistsAsync
            // is the check that normally catches it; the constraint is the backstop for two retries
            // genuinely in flight at once, which fails the write rather than sending twice.
            message.HasIndex(m => m.ProviderMessageId).IsUnique();
            message.HasIndex(m => m.ReceivedAt);
            message.HasIndex(m => m.Source);
        });

        modelBuilder.Entity<Relay>(relay =>
        {
            relay.HasKey(r => r.Id);
            relay.Property(r => r.Id).ValueGeneratedNever();
            relay.Property(r => r.Delivery).HasConversion<string>();
            relay.Property(r => r.Body).HasMaxLength(SmsText.MaxLength);
            relay.Property(r => r.Error).HasMaxLength(500);
            relay.Property(r => r.ProviderMessageId).HasMaxLength(128);
            relay.Property(r => r.Recipient)
                .HasConversion(v => v.Value, v => PhoneNumber.Parse(v))
                .HasMaxLength(20);

            // Delivery receipts arrive keyed by the provider id of the message we sent, so this is a
            // lookup path, not just a column.
            relay.HasIndex(r => r.ProviderMessageId);
        });
    }
}
