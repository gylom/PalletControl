using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

public sealed record ExportTable(string Name, List<string> Headers, List<List<object?>> Rows);

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string LegacyTerminalAdmin = "TerminalAdmin";
    public const string Superuser = "Superuser";
    public const string Viewer = "Viewer";
    public const string User = "User";
}

public static class ReceiptStatus
{
    public const string Active = "ACTIVE";
    public const string Cancelled = "CANCELLED";
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<Transporter> Transporters => Set<Transporter>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<PalletType> PalletTypes => Set<PalletType>();
    public DbSet<PalletReceipt> Receipts => Set<PalletReceipt>();
    public DbSet<PalletReceiptItem> ReceiptItems => Set<PalletReceiptItem>();
    public DbSet<ReceiptAction> ReceiptActions => Set<ReceiptAction>();
    public DbSet<WarningEvent> WarningEvents => Set<WarningEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<TerminalSettings> TerminalSettings => Set<TerminalSettings>();
    public DbSet<UserSettingsRecord> UserSettings => Set<UserSettingsRecord>();
    public DbSet<ViewerTransporterAssignment> ViewerTransporterAssignments => Set<ViewerTransporterAssignment>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<LinehaulReceipt> LinehaulReceipts => Set<LinehaulReceipt>();
    public DbSet<LinehaulCommentOption> LinehaulCommentOptions => Set<LinehaulCommentOption>();
    public DbSet<ReceivedControlEntry> ReceivedControlEntries => Set<ReceivedControlEntry>();
    public DbSet<ReceivedControlWarning> ReceivedControlWarnings => Set<ReceivedControlWarning>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.VehicleId).IsUnique();
        b.Entity<Transporter>().HasIndex(x => new { x.TerminalId, x.Name }).IsUnique();
        b.Entity<PalletType>().HasIndex(x => x.Name).IsUnique();
        b.Entity<PalletReceipt>().HasIndex(x => x.ReceiptNumber).IsUnique();
        b.Entity<PalletReceipt>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<WarningEvent>().HasIndex(x => new { x.TerminalId, x.AcknowledgedAtUtc, x.CreatedAtUtc });
        b.Entity<Holiday>().HasIndex(x => x.Date).IsUnique();
        b.Entity<TerminalSettings>().HasIndex(x => x.TerminalId).IsUnique();
        b.Entity<UserSettingsRecord>().ToTable("UserSettings");
        b.Entity<UserSettingsRecord>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<UserSettingsRecord>().HasOne(x => x.User).WithOne().HasForeignKey<UserSettingsRecord>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ViewerTransporterAssignment>().HasKey(x => new { x.UserId, x.TransporterId });
        b.Entity<ViewerTransporterAssignment>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ViewerTransporterAssignment>().HasOne(x => x.Transporter).WithMany().HasForeignKey(x => x.TransporterId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LinehaulReceipt>().HasIndex(x => x.ReceiptNumber).IsUnique();
        b.Entity<LinehaulReceipt>().HasIndex(x => x.PalletReceiptNumber);
        b.Entity<LinehaulReceipt>().HasIndex(x => new { x.FromTerminalId, x.ToTerminalId, x.BusinessDate });
        b.Entity<LinehaulCommentOption>().HasIndex(x => x.TerminalId);
        b.Entity<ReceivedControlEntry>().HasIndex(x => x.ControlNumber).IsUnique();
        b.Entity<ReceivedControlEntry>().HasIndex(x => new { x.TerminalId, x.FromTerminalId, x.BusinessDate });
        b.Entity<ReceivedControlWarning>().HasIndex(x => new { x.TerminalId, x.AcknowledgedAtUtc, x.CreatedAtUtc });
        b.Entity<Vehicle>().Property(x => x.OperatingDays).HasDefaultValue("1,2,3,4,5");

        b.Entity<Vehicle>()
            .HasOne(x => x.Transporter)
            .WithMany(x => x.Vehicles)
            .HasForeignKey(x => x.TransporterId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<PalletReceipt>()
            .HasOne(x => x.Vehicle)
            .WithMany()
            .HasForeignKey(x => x.VehicleId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<PalletReceipt>()
            .HasOne(x => x.Driver)
            .WithMany()
            .HasForeignKey(x => x.DriverId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<PalletReceipt>()
            .HasOne(x => x.SubmittedByUser)
            .WithMany()
            .HasForeignKey(x => x.SubmittedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<PalletReceipt>()
            .HasMany(x => x.Actions)
            .WithOne(x => x.Receipt)
            .HasForeignKey(x => x.ReceiptId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<ReceiptAction>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<WarningEvent>()
            .HasOne(x => x.Receipt)
            .WithMany()
            .HasForeignKey(x => x.ReceiptId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<WarningEvent>()
            .HasOne(x => x.TriggeredByUser)
            .WithMany()
            .HasForeignKey(x => x.TriggeredByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<WarningEvent>()
            .HasOne(x => x.AcknowledgedByUser)
            .WithMany()
            .HasForeignKey(x => x.AcknowledgedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.User;
    public bool Active { get; set; } = true;
    public int TerminalId { get; set; }

    public bool ShowMilestoneNotifications { get; set; } = true;
    public bool ShowLeaderboardNotifications { get; set; } = true;
    public bool ShowBalanceNotifications { get; set; } = true;

    // Per-user navigation/data access controlled from Admin -> Tab access.
    public bool ShowDriverStatisticsTab { get; set; } = true;
    public bool ShowDailyCheckTab { get; set; } = true;

    // Operational modules can be combined freely on one user.
    public bool HasInternalPalletAccounting { get; set; } = true;
    public bool HasLinehaul { get; set; } = false;
    public bool HasReceivedControl { get; set; } = false;

    [JsonIgnore] public Terminal? Terminal { get; set; }
}

public class ViewerTransporterAssignment
{
    public int UserId { get; set; }
    public int TransporterId { get; set; }

    [JsonIgnore] public AppUser? User { get; set; }
    [JsonIgnore] public Transporter? Transporter { get; set; }
}

public class Terminal
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Aliases { get; set; } = "";
    public bool Active { get; set; } = true;

    // Only SRD / ARE / KRS are operating PalletControl terminals. Other rows are
    // Linehaul/Mottatt locations owned by one operating terminal.
    public bool IsOperatingTerminal { get; set; } = true;
    public int? ScopeTerminalId { get; set; }
}

public class Transporter
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public int TerminalId { get; set; }
    [JsonIgnore] public List<Vehicle> Vehicles { get; set; } = [];
}

public class Vehicle
{
    public int Id { get; set; }
    public string VehicleId { get; set; } = "";
    public bool Active { get; set; } = true;
    public int TerminalId { get; set; }
    public string OperatingDays { get; set; } = "1,2,3,4,5";
    [JsonIgnore] public Terminal? Terminal { get; set; }
    public int? TransporterId { get; set; }
    [JsonIgnore] public Transporter? Transporter { get; set; }
}

public class Holiday
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

public class Driver
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public int TerminalId { get; set; }
    [JsonIgnore] public Terminal? Terminal { get; set; }
}

public class PalletType
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public bool UserSelectable { get; set; } = true;
}

public class PalletReceipt
{
    public int Id { get; set; }
    public string ReceiptNumber { get; set; } = "";
    public int TerminalId { get; set; }
    public Terminal? Terminal { get; set; }

    public int? VehicleId { get; set; }
    [JsonIgnore] public Vehicle? Vehicle { get; set; }
    public int? DriverId { get; set; }
    [JsonIgnore] public Driver? Driver { get; set; }

    public string VehicleSnapshot { get; set; } = "";
    public string DriverSnapshot { get; set; } = "";
    public string TransporterSnapshot { get; set; } = "";

    public string Direction { get; set; } = "";
    public DateOnly BusinessDate { get; set; }
    public DateTime SubmittedAtUtc { get; set; }

    public int SubmittedByUserId { get; set; }
    public AppUser? SubmittedByUser { get; set; }

    public string IdempotencyKey { get; set; } = "";
    public string Status { get; set; } = ReceiptStatus.Active;
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }

    public List<PalletReceiptItem> Items { get; set; } = [];
    public List<ReceiptAction> Actions { get; set; } = [];
}

public class PalletReceiptItem
{
    public int Id { get; set; }
    public int PalletReceiptId { get; set; }
    [JsonIgnore] public PalletReceipt? PalletReceipt { get; set; }
    public int PalletTypeId { get; set; }
    public PalletType? PalletType { get; set; }
    public int Quantity { get; set; }
}

public class ReceiptAction
{
    public int Id { get; set; }
    public int ReceiptId { get; set; }
    [JsonIgnore] public PalletReceipt? Receipt { get; set; }
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
}

public class WarningEvent
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public string Message { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public int TerminalId { get; set; }

    public int? ReceiptId { get; set; }
    public PalletReceipt? Receipt { get; set; }

    public int TriggeredByUserId { get; set; }
    public AppUser? TriggeredByUser { get; set; }

    public DateTime? AcknowledgedAtUtc { get; set; }
    public int? AcknowledgedByUserId { get; set; }
    public AppUser? AcknowledgedByUser { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}


public class UserSettingsRecord
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Theme { get; set; } = "normal";
    public bool ShowMilestoneNotifications { get; set; } = true;
    public bool ShowLeaderboardNotifications { get; set; } = true;
    public bool ShowBalanceNotifications { get; set; } = true;
    [JsonIgnore] public AppUser? User { get; set; }
}

public class TerminalSettings
{
    public int Id { get; set; }
    public int TerminalId { get; set; }
    public bool AllowUsersAddDrivers { get; set; } = true;
    public bool LargeInEnabled { get; set; } = true;
    public int LargeInThreshold { get; set; } = 20;
    public bool LargeOutEnabled { get; set; } = true;
    public int LargeOutThreshold { get; set; } = 20;
    public bool RecentVehicleEnabled { get; set; } = true;
    public int RecentVehicleMinutes { get; set; } = 5;
    public bool RecentDriverEnabled { get; set; } = true;
    public int RecentDriverMinutes { get; set; } = 5;
    public bool DuplicateEnabled { get; set; } = true;
    public int DuplicateMinutes { get; set; } = 5;
    public bool RapidSubmissionsEnabled { get; set; } = true;
    public int RapidSubmissionCount { get; set; } = 3;
    public int RapidSubmissionMinutes { get; set; } = 10;
    public bool DailyTotalEnabled { get; set; } = true;
    public int DailyTotalThreshold { get; set; } = 60;
    public bool CancellationWarningEnabled { get; set; } = true;
    public bool CancellationReversedWarningEnabled { get; set; } = true;
    public bool MilestoneNotificationsEnabled { get; set; } = true;
    public int MonthlyMilestoneStep { get; set; } = 100;
    public bool LeaderboardNotificationsEnabled { get; set; } = true;
    public bool BalanceNotificationsEnabled { get; set; } = true;
    public int DriverUnmatchedInDeduction { get; set; } = 15;

    public static TerminalSettings FromGlobal(int terminalId, AppSettings g) => new()
    {
        TerminalId = terminalId,
        AllowUsersAddDrivers = g.AllowUsersAddDrivers,
        LargeInEnabled = g.LargeInEnabled, LargeInThreshold = g.LargeInThreshold,
        LargeOutEnabled = g.LargeOutEnabled, LargeOutThreshold = g.LargeOutThreshold,
        RecentVehicleEnabled = g.RecentVehicleEnabled, RecentVehicleMinutes = g.RecentVehicleMinutes,
        RecentDriverEnabled = g.RecentDriverEnabled, RecentDriverMinutes = g.RecentDriverMinutes,
        DuplicateEnabled = g.DuplicateEnabled, DuplicateMinutes = g.DuplicateMinutes,
        RapidSubmissionsEnabled = g.RapidSubmissionsEnabled, RapidSubmissionCount = g.RapidSubmissionCount, RapidSubmissionMinutes = g.RapidSubmissionMinutes,
        DailyTotalEnabled = g.DailyTotalEnabled, DailyTotalThreshold = g.DailyTotalThreshold,
        CancellationWarningEnabled = g.CancellationWarningEnabled, CancellationReversedWarningEnabled = g.CancellationReversedWarningEnabled,
        MilestoneNotificationsEnabled = g.MilestoneNotificationsEnabled, MonthlyMilestoneStep = g.MonthlyMilestoneStep,
        LeaderboardNotificationsEnabled = g.LeaderboardNotificationsEnabled, BalanceNotificationsEnabled = g.BalanceNotificationsEnabled,
        DriverUnmatchedInDeduction = g.DriverUnmatchedInDeduction
    };
}

public class LinehaulCommentOption
{
    public int Id { get; set; }
    public int TerminalId { get; set; }
    public string Text { get; set; } = "";
    public bool Active { get; set; } = true;
}

public class LinehaulReceipt
{
    public int Id { get; set; }
    public string ReceiptNumber { get; set; } = "";
    public int OwnerTerminalId { get; set; }
    public int FromTerminalId { get; set; }
    public int ToTerminalId { get; set; }
    public string FromTerminalSnapshot { get; set; } = "";
    public string ToTerminalSnapshot { get; set; } = "";
    public string UnitReference { get; set; } = "";
    public string PalletReceiptNumber { get; set; } = "";
    public int PalletCount { get; set; }
    public string CommentOptionSnapshot { get; set; } = "";
    public string FreeComment { get; set; } = "";
    public DateOnly BusinessDate { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    public int SubmittedByUserId { get; set; }
    public string Status { get; set; } = ReceiptStatus.Active;
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }
}

public static class ReceivedControlStatus
{
    public const string NoReceipt = "NO_RECEIPT";
    public const string ReceiptHigher = "RECEIPT_HIGHER";
    public const string ReceiptLower = "RECEIPT_LOWER";
    public const string Exact = "EXACT";
    public static string Resolve(bool received, int? receiptQty, int actualQty)
    {
        if (!received) return NoReceipt;
        if ((receiptQty ?? 0) > actualQty) return ReceiptHigher;
        if ((receiptQty ?? 0) < actualQty) return ReceiptLower;
        return Exact;
    }
}

public class ReceivedControlEntry
{
    public int Id { get; set; }
    public string ControlNumber { get; set; } = "";
    public int TerminalId { get; set; }
    public int FromTerminalId { get; set; }
    public string FromTerminalSnapshot { get; set; } = "";
    public string UnitReference { get; set; } = "";
    public string Comment { get; set; } = "";
    public bool PalletReceiptReceived { get; set; }
    public int? ReceiptPalletCount { get; set; }
    public int ActualPalletCount { get; set; }
    public string Result { get; set; } = ReceivedControlStatus.NoReceipt;
    public DateOnly BusinessDate { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    public int SubmittedByUserId { get; set; }
    public string Status { get; set; } = ReceiptStatus.Active;
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }
}

public class ReceivedControlWarning
{
    public int Id { get; set; }
    public int TerminalId { get; set; }
    public int EntryId { get; set; }
    public string Message { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public int? AcknowledgedByUserId { get; set; }
}

public class AppSettings
{
    public int Id { get; set; } = 1;
    public bool AllowUsersAddDrivers { get; set; } = true;

    public bool LargeInEnabled { get; set; } = true;
    public int LargeInThreshold { get; set; } = 20;
    public bool LargeOutEnabled { get; set; } = true;
    public int LargeOutThreshold { get; set; } = 20;

    public bool RecentVehicleEnabled { get; set; } = true;
    public int RecentVehicleMinutes { get; set; } = 5;
    public bool RecentDriverEnabled { get; set; } = true;
    public int RecentDriverMinutes { get; set; } = 5;

    public bool DuplicateEnabled { get; set; } = true;
    public int DuplicateMinutes { get; set; } = 5;

    public bool RapidSubmissionsEnabled { get; set; } = true;
    public int RapidSubmissionCount { get; set; } = 3;
    public int RapidSubmissionMinutes { get; set; } = 10;

    public bool DailyTotalEnabled { get; set; } = true;
    public int DailyTotalThreshold { get; set; } = 60;

    public bool CancellationWarningEnabled { get; set; } = true;
    public bool CancellationReversedWarningEnabled { get; set; } = true;

    public bool MilestoneNotificationsEnabled { get; set; } = true;
    public int MonthlyMilestoneStep { get; set; } = 100;
    public bool LeaderboardNotificationsEnabled { get; set; } = true;
    public bool BalanceNotificationsEnabled { get; set; } = true;

    // Driver statistics adjustment: every unmatched IN receipt deducts this many pallets.
    public int DriverUnmatchedInDeduction { get; set; } = 15;
}

public class VehicleComplianceRow
{
    public DateOnly Date { get; set; }
    public int VehicleId { get; set; }
    public string Vehicle { get; set; } = "";
    public string Transporter { get; set; } = "";
    public List<int> InDriverIds { get; set; } = [];
    public List<int> OutDriverIds { get; set; } = [];
    public List<string> InDrivers { get; set; } = [];
    public List<string> OutDrivers { get; set; } = [];
    public bool HasIn { get; set; }
    public bool HasOut { get; set; }
    public bool Complete { get; set; }
    public bool IsToday { get; set; }
    public string Status { get; set; } = "";
}

public class StatisticsRow
{
    public string Transporter { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public string PalletType { get; set; } = "";
    public int InQty { get; set; }
    public int OutQty { get; set; }
    public int Balance { get; set; }
    public int Movement { get; set; }
}

public class DriverStatisticsRow
{
    public int DriverId { get; set; }
    public string Driver { get; set; } = "";
    public string Vehicles { get; set; } = "";
    public int InReceipts { get; set; }
    public int OutReceipts { get; set; }
    public int UnmatchedInReceipts { get; set; }
    public int InQty { get; set; }
    public int OutQty { get; set; }
    public int RawBalance { get; set; }
    public int Deduction { get; set; }
    public int AdjustedBalance { get; set; }
    public int Movement { get; set; }
}

public class DriverAdjustmentDetail
{
    public int DriverId { get; set; }
    public string Driver { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public DateOnly Date { get; set; }
    public int InReceipts { get; set; }
    public int OutReceipts { get; set; }
    public int UnmatchedInReceipts { get; set; }
    public int Deduction { get; set; }
}

public class DriverLeaderboardRow
{
    public int Rank { get; set; }
    public int DriverId { get; set; }
    public string Driver { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public int InQty { get; set; }
    public int OutQty { get; set; }
    public int Balance { get; set; }
    public int Movement { get; set; }
}

