public record SubmissionWarningDto(string Type, string Severity, string Message);
public record ReceiptValidation(string? Error, Vehicle? Vehicle, Driver? Driver, List<ReceiptItemRequest> PositiveItems)
{
    public static ReceiptValidation Fail(string error) => new(error, null, null, []);
}

public record LoginRequest(string Username, string Password);
public record LoginResponse(string Token, string Username, string DisplayName, string Role, int TerminalId, string TerminalCode, bool ShowDriverStatisticsTab, bool ShowDailyCheckTab, bool HasInternalPalletAccounting, bool HasLinehaul, bool HasReceivedControl, string Theme, string ViewerScopeLabel);
public record QuickDriverRequest(string Name);
public record ReceiptItemRequest(int PalletTypeId, int Quantity);
public record CreateReceiptRequest(string IdempotencyKey, int VehicleId, int DriverId, string Direction, List<ReceiptItemRequest> Items, bool ConfirmWarnings = false, DateOnly? BusinessDate = null);
public record CancelRequest(string Reason);
public record ReverseCancellationRequest(string? Reason);
public record UserPreferenceRequest(bool ShowMilestoneNotifications, bool ShowLeaderboardNotifications, bool ShowBalanceNotifications, string Theme);
public record SelfPasswordRequest(string CurrentPassword, string NewPassword);
public record SwitchTerminalRequest(int TerminalId);

public record AdminTransporterRequest(string Name, int? TerminalId = null);
public record AdminVehicleRequest(string VehicleId, int TerminalId, int TransporterId);
public record VehicleTransporterRequest(int TransporterId);
public record VehicleScheduleRequest(List<int>? Days);
public record AdminHolidayRequest(DateOnly Date, string? Name);
public record AdminDriverRequest(string Name, int TerminalId);
public record AdminPalletTypeRequest(string Name, bool UserSelectable);
public record AdminPalletTypeUpdate(bool Active, bool UserSelectable);
public record AdminUserRequest(string Username, string DisplayName, string Password, string Role, int TerminalId, bool HasInternalPalletAccounting, bool HasLinehaul, bool HasReceivedControl, bool ShowDriverStatisticsTab = true, bool ShowDailyCheckTab = true, List<int>? ViewerTransporterIds = null);
public record AdminUserUpdateRequest(string DisplayName, string Role, int TerminalId, bool Active, bool HasInternalPalletAccounting, bool HasLinehaul, bool HasReceivedControl, bool ShowDriverStatisticsTab = true, bool ShowDailyCheckTab = true, List<int>? ViewerTransporterIds = null);
public record AdminPasswordRequest(string Password);
public record AdminTabAccessRequest(bool ShowDriverStatisticsTab, bool ShowDailyCheckTab);
public record AdminActiveRequest(bool Active);
public record AdminSettingsRequest(
    bool AllowUsersAddDrivers,
    bool LargeInEnabled,
    int LargeInThreshold,
    bool LargeOutEnabled,
    int LargeOutThreshold,
    bool RecentVehicleEnabled,
    int RecentVehicleMinutes,
    bool RecentDriverEnabled,
    int RecentDriverMinutes,
    bool DuplicateEnabled,
    int DuplicateMinutes,
    bool RapidSubmissionsEnabled,
    int RapidSubmissionCount,
    int RapidSubmissionMinutes,
    bool DailyTotalEnabled,
    int DailyTotalThreshold,
    bool CancellationWarningEnabled,
    bool CancellationReversedWarningEnabled,
    bool MilestoneNotificationsEnabled,
    int MonthlyMilestoneStep,
    bool LeaderboardNotificationsEnabled,
    bool BalanceNotificationsEnabled,
    int? DriverUnmatchedInDeduction);


public record AdminTerminalRequest(string? Code, string? Name, string? Aliases);
public record AdminTerminalUpdateRequest(string? Code, string? Name, string? Aliases, bool Active);
public record AdminLinehaulLocationRequest(int? TerminalId, string? Code, string? Name, string? Aliases);
public record AdminLinehaulLocationUpdateRequest(string? Code, string? Name, string? Aliases, bool Active);
public record AdminLinehaulCommentRequest(int? TerminalId, string? Text);
public record CreateLinehaulReceiptRequest(string? UnitReference, string? PalletReceiptNumber, int PalletCount, int FromTerminalId, int ToTerminalId, int? CommentOptionId, string? FreeComment, DateOnly? BusinessDate);
public record CreateReceivedControlRequest(int FromTerminalId, string? UnitReference, string? Comment, bool PalletReceiptReceived, int? ReceiptPalletCount, int ActualPalletCount, DateOnly? BusinessDate);
public sealed record ImportIssue(int Row, string Message);
public sealed record ImportDataRow(int RowNumber, Dictionary<string, string> Values);
public sealed record ImportGrid(List<string> Headers, List<ImportDataRow> Rows);
public sealed record PendingLinehaulImport(int RowNumber, DateOnly BusinessDate, string UnitReference, string PalletReceiptNumber, int PalletCount, int FromTerminalId, int ToTerminalId, string FromTerminalCode, string ToTerminalCode, string StandardComment, string FreeComment, string DuplicateKey);
public sealed record PendingReceivedControlImport(int RowNumber, DateOnly BusinessDate, int FromTerminalId, string FromTerminalCode, string UnitReference, bool PalletReceiptReceived, int? ReceiptPalletCount, int ActualPalletCount, string Comment, string DuplicateKey);

