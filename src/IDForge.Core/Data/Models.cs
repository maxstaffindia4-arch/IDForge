namespace IDForge.Core.Data;

public sealed class Employee
{
    public long Id { get; set; }
    public required string Uid { get; set; }
    public required string Name { get; set; }
    public required string BloodGroup { get; set; }
    public required string Position { get; set; }
    public required string ContractType { get; set; }
    public string? PhotoPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public bool IsDeleted => DeletedAt.HasValue;
}

public sealed class CardIssue
{
    public long Id { get; set; }
    public long EmployeeId { get; set; }
    public int Version { get; set; } = 1;
    public DateOnly PrintDate { get; set; }
    public DateOnly ValidUntil { get; set; }
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? OutputPdfPath { get; set; }
    public string? OutputPngPath { get; set; }
    /// <summary>Idempotency key (e.g. "batchrow:42:attempt:1"). Unique when set.</summary>
    public string? IssueKey { get; set; }
}

public sealed class BatchJob
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Open"; // Open, Completed
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BatchItem
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public int RowIndex { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Ready, Approved, Exported, Failed, Skipped
    public string? Error { get; set; }
    // Row payload as JSON: name, bloodGroup, position, uid, contractType, printDate, photoPath
    public required string PayloadJson { get; set; }
    public long? EmployeeId { get; set; }
    public long? CardIssueId { get; set; }
}

/// <summary>A persisted batch session (one import/review run).</summary>
public sealed class BatchSession
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Open or Completed.</summary>
    public string Status { get; set; } = "Open";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A persisted batch row — survives app restarts (plan §8).</summary>
public sealed class BatchRowEntry
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public string Uid { get; set; } = "";
    public string Name { get; set; } = "";
    public string BloodGroup { get; set; } = "N/A";
    public string Position { get; set; } = "";
    public string ContractType { get; set; } = "Permanent";
    public string PrintDate { get; set; } = "";   // yyyy-MM-dd
    public string? PhotoPath { get; set; }
    public string? CutoutPath { get; set; }       // cached processed cutout PNG
    public string BgMode { get; set; } = "PlainWhite";
    public double Zoom { get; set; } = 1.0;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    /// <summary>Auto-fit transform the nudge values are relative to.</summary>
    public double AutoScale { get; set; } = 1.0;
    public double AutoOffsetX { get; set; }
    public double AutoOffsetY { get; set; }
    /// <summary>Draft, NeedsPhoto, Processing, NeedsReview, Approved, Issued, Failed, Skipped.</summary>
    public string Status { get; set; } = "Draft";
    public string? Error { get; set; }
    public int AttemptCount { get; set; }
    public long? CardIssueId { get; set; }
    public bool Approved { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Outcome of the one-time UID-prefix -> digits migration.</summary>
public sealed class UidMigrationResult
{
    public int EmployeesMigrated { get; set; }
    public int BatchRowsMigrated { get; set; }
    public List<string> Changes { get; set; } = new();
    /// <summary>Normalization collisions or un-migratable values; migration aborts on these.</summary>
    public List<string> Conflicts { get; set; } = new();
    public string? BackupPath { get; set; }
    public bool HasChanges => EmployeesMigrated + BatchRowsMigrated > 0;
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public long? EmployeeId { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string User { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
