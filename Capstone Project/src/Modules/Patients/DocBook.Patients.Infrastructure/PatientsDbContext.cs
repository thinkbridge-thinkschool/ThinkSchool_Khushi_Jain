using DocBook.Patients.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocBook.Patients.Infrastructure;

public sealed class PatientsDbContext(DbContextOptions<PatientsDbContext> options) : DbContext(options)
{
    public const string Schema = "patients";

    public DbSet<Patient> Patients => Set<Patient>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);

        builder.Entity<Patient>(patient =>
        {
            patient.ToTable("patients", Schema);
            patient.HasKey(record => record.Id);

            patient.Property(record => record.Id)
                .HasConversion(id => id.Value, value => new PatientId(value))
                .ValueGeneratedNever();

            patient.Property(record => record.FullName)
                .HasMaxLength(Patient.MaxNameLength)
                .IsRequired();

            patient.Property(record => record.Email)
                .HasMaxLength(Patient.MaxEmailLength)
                .IsRequired();

            patient.Property(record => record.Phone).HasMaxLength(Patient.MaxPhoneLength);
            patient.Property(record => record.PasswordHash).HasMaxLength(100).IsRequired();

            // Unique so two accounts cannot claim one address even if two registrations race.
            patient.HasIndex(record => record.Email).IsUnique();

            patient.Ignore(record => record.DomainEvents);
        });
    }
}
