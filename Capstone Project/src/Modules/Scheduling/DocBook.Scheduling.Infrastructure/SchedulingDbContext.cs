using DocBook.Infrastructure;
using DocBook.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocBook.Scheduling.Infrastructure;

public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : DbContext(options)
{
    public const string Schema = "scheduling";

    public DbSet<DoctorDaySchedule> Schedules => Set<DoctorDaySchedule>();

    public DbSet<Appointment> Appointments => Set<Appointment>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);

        builder.Entity<DoctorDaySchedule>(schedule =>
        {
            schedule.ToTable("doctor_day_schedules", Schema);
            schedule.HasKey(day => day.Id);

            schedule.Property(day => day.Id)
                .HasConversion(id => id.Value, value => new DoctorDayScheduleId(value))
                .ValueGeneratedNever();

            schedule.Property(day => day.DoctorId)
                .HasConversion(id => id.Value, value => new DoctorId(value));

            // Booking touches only a child row, so the repository bumps this token by hand.
            schedule.Property<byte[]>("Version").IsRowVersion();

            schedule.HasIndex(day => new { day.DoctorId, day.Date }).IsUnique();
            schedule.HasIndex(day => day.Date);

            schedule.Ignore(day => day.DomainEvents);

            // Required, or an appointment could outlive the day it belongs to as an orphan row.
            schedule.HasMany(day => day.Appointments)
                .WithOne()
                .HasForeignKey("ScheduleId")
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            schedule.Navigation(day => day.Appointments)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<Appointment>(appointment =>
        {
            appointment.ToTable("appointments", Schema);
            appointment.HasKey(booking => booking.Id);

            appointment.Property(booking => booking.Id)
                .HasConversion(id => id.Value, value => new AppointmentId(value))
                .ValueGeneratedNever();

            appointment.Property(booking => booking.PatientId)
                .HasConversion(id => id.Value, value => new PatientId(value));

            appointment.Property(booking => booking.Status).HasConversion<int>();

            appointment.Property(booking => booking.Reason)
                .HasMaxLength(Appointment.MaxReasonLength)
                .IsRequired();

            appointment.ComplexProperty(booking => booking.Slot, slot =>
            {
                slot.Property(period => period.Start).HasColumnName("starts_at");
                slot.Property(period => period.End).HasColumnName("ends_at");
            });

            appointment.HasIndex(booking => booking.PatientId);
        });

        builder.MapOutbox(Schema);
        builder.MapAuditTrail(Schema);
    }
}
