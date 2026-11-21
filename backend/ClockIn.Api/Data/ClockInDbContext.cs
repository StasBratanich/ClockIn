using ClockIn.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClockIn.Api.Data;

public sealed class ClockInDbContext(DbContextOptions<ClockInDbContext> options) : DbContext(options)
{
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<FailedPunchAttempt> FailedPunchAttempts => Set<FailedPunchAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.Property(employee => employee.EmployeeNumber).HasColumnType("varchar(20)");
            entity.Property(employee => employee.FullName).HasColumnType("nvarchar(120)");
            entity.HasIndex(employee => employee.EmployeeNumber).IsUnique();
            entity.HasData(
                new Employee { Id = 1, EmployeeNumber = "1001", FullName = "Demo Employee 1001" },
                new Employee { Id = 2, EmployeeNumber = "1002", FullName = "Demo Employee 1002" },
                new Employee { Id = 3, EmployeeNumber = "1003", FullName = "Demo Employee 1003" });
        });

        modelBuilder.Entity<Shift>(entity =>
        {
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_Shifts_StatusAndClockOut",
                    "([Status] IN (1, 3) AND [ClockOutAtUtc] IS NULL) OR ([Status] = 2 AND [ClockOutAtUtc] IS NOT NULL AND [ClockOutAtUtc] > [ClockInAtUtc])");
                table.HasCheckConstraint(
                    "CK_Shifts_UtcOffsets",
                    "DATEPART(TZOFFSET, [ClockInAtUtc]) = 0 AND ([ClockOutAtUtc] IS NULL OR DATEPART(TZOFFSET, [ClockOutAtUtc]) = 0)");
            });
            entity.Property(shift => shift.ClockInAtUtc).HasColumnType("datetimeoffset");
            entity.Property(shift => shift.ClockOutAtUtc).HasColumnType("datetimeoffset");
            entity.Property(shift => shift.Status).HasConversion<byte>().HasColumnType("tinyint");
            entity.HasOne(shift => shift.Employee)
                .WithMany(employee => employee.Shifts)
                .HasForeignKey(shift => shift.EmployeeId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(shift => shift.EmployeeId)
                .IsUnique()
                .HasFilter("[Status] = 1")
                .HasDatabaseName("IX_Shifts_EmployeeId_Open");
        });

        modelBuilder.Entity<FailedPunchAttempt>(entity =>
        {
            entity.ToTable(table => table.HasCheckConstraint("CK_FailedPunchAttempts_Action", "[Action] IN (1, 2)"));
            entity.Property(attempt => attempt.Action).HasConversion<byte>().HasColumnType("tinyint");
            entity.Property(attempt => attempt.FailureCode).HasColumnType("varchar(32)");
            entity.Property(attempt => attempt.ReceivedAtServerUtc).HasColumnType("datetimeoffset");
            entity.HasOne(attempt => attempt.Employee)
                .WithMany(employee => employee.FailedPunchAttempts)
                .HasForeignKey(attempt => attempt.EmployeeId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}
