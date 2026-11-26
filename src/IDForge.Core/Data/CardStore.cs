using Microsoft.Data.Sqlite;

namespace IDForge.Core.Data;

/// <summary>SQLite-backed store for employees, card issues, and batch jobs.</summary>
public sealed class CardStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _dbPath;
    public SqliteConnection Connection => _conn;

    /// <summary>Result of the one-time UID-prefix migration (null on old DBs opened read-only scenarios).</summary>
    public UidMigrationResult? LastUidMigration { get; private set; }

    public CardStore(string dbPath)
    {
        _dbPath = dbPath;
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var pragma = _conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        Migrate();
        LastUidMigration = MigrateUidPrefixes();
    }

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS Employees (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Uid TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL,
                BloodGroup TEXT NOT NULL,
                Position TEXT NOT NULL,
                ContractType TEXT NOT NULL,
                PhotoPath TEXT,
                CreatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS CardIssues (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EmployeeId INTEGER NOT NULL REFERENCES Employees(Id),
                Version INTEGER NOT NULL,
                PrintDate TEXT NOT NULL,
                ValidUntil TEXT NOT NULL,
                IssuedAt TEXT NOT NULL,
                OutputPdfPath TEXT,
                OutputPngPath TEXT
            );
            CREATE TABLE IF NOT EXISTS BatchJobs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BatchItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL REFERENCES BatchJobs(Id),
                RowIndex INTEGER NOT NULL,
                Status TEXT NOT NULL,
                Error TEXT,
                PayloadJson TEXT NOT NULL,
                EmployeeId INTEGER,
                CardIssueId INTEGER
            );
            CREATE TABLE IF NOT EXISTS AuditLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EmployeeId INTEGER,
                Action TEXT NOT NULL,
                Detail TEXT NOT NULL,
                User TEXT NOT NULL,
                At TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BatchSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BatchRows (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL REFERENCES BatchSessions(Id),
                Uid TEXT NOT NULL,
                Name TEXT NOT NULL,
                BloodGroup TEXT NOT NULL,
                Position TEXT NOT NULL,
                ContractType TEXT NOT NULL,
                PrintDate TEXT NOT NULL,
                PhotoPath TEXT,
                CutoutPath TEXT,
                BgMode TEXT NOT NULL DEFAULT 'PlainWhite',
                Zoom REAL NOT NULL DEFAULT 1.0,
                OffsetX REAL NOT NULL DEFAULT 0,
                OffsetY REAL NOT NULL DEFAULT 0,
                AutoScale REAL NOT NULL DEFAULT 1.0,
                AutoOffsetX REAL NOT NULL DEFAULT 0,
                AutoOffsetY REAL NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'Draft',
                Error TEXT,
                AttemptCount INTEGER NOT NULL DEFAULT 0,
                CardIssueId INTEGER,
                Approved INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL
            );
            """);
        // Soft-delete column added later; migrate existing DBs.
        if (!ColumnExists("Employees", "DeletedAt"))
            Exec("ALTER TABLE Employees ADD COLUMN DeletedAt TEXT");
        // Issue idempotency key (plan §5).
        if (!ColumnExists("CardIssues", "IssueKey"))
            Exec("ALTER TABLE CardIssues ADD COLUMN IssueKey TEXT");
        Exec("CREATE UNIQUE INDEX IF NOT EXISTS IX_CardIssues_IssueKey ON CardIssues(IssueKey) WHERE IssueKey IS NOT NULL");
    }

    // ---------- UID digits migration (runs once per DB open; idempotent) ----------

    /// <summary>
    /// Converts stored "UID20250318"/"ID. 20250318" values to bare digits in
    /// Employees.Uid and BatchRows.Uid. Backs up the DB first, logs every
    /// change to AuditLog, and aborts without changes when two rows would
    /// normalize to the same digits.
    /// </summary>
    public UidMigrationResult MigrateUidPrefixes()
    {
        var result = new UidMigrationResult();

        // Collect candidates: stored value differs from its normalized form.
        var empRows = new List<(long Id, string Old, string Digits)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Uid FROM Employees";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var old = r.GetString(1);
                var digits = Uid.Normalize(old);
                if (digits != null && digits != old)
                {
                    if (Uid.IsValid(digits)) empRows.Add((r.GetInt64(0), old, digits));
                    else result.Conflicts.Add($"Employee {r.GetInt64(0)} UID '{old}' cannot be normalized to digits.");
                }
            }
        }
        var batchRows = new List<(long Id, string Old, string Digits)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Uid FROM BatchRows";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var old = r.GetString(1);
                var digits = Uid.Normalize(old);
                if (digits != null && digits != old)
                {
                    if (Uid.IsValid(digits)) batchRows.Add((r.GetInt64(0), old, digits));
                    else result.Conflicts.Add($"BatchRow {r.GetInt64(0)} UID '{old}' cannot be normalized to digits.");
                }
            }
        }
        if (empRows.Count == 0 && batchRows.Count == 0) return result;

        // Conflict check: final digit values must be unique across each table
        // (an untouched "20250318" row collides with a migrated "UID20250318").
        void CheckConflicts(List<(long Id, string Old, string Digits)> rows, string table)
        {
            var finalUids = new Dictionary<string, long>(StringComparer.Ordinal);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT Id, Uid FROM {table}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var stored = r.GetString(1);
                var digits = Uid.Normalize(stored) ?? stored;
                if (finalUids.TryGetValue(digits, out var other) && other != id)
                    result.Conflicts.Add($"{table}: '{stored}' (id {id}) and id {other} both normalize to '{digits}'.");
                else finalUids[digits] = id;
            }
        }
        CheckConflicts(empRows, "Employees");
        CheckConflicts(batchRows, "BatchRows");
        if (result.Conflicts.Count > 0) return result;   // stop: report, change nothing

        // Backup before touching data.
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_dbPath))!, "backups");
            Directory.CreateDirectory(dir);
            result.BackupPath = Path.Combine(dir,
                $"idcard_pre_uidmig_{DateTime.Now:yyyyMMdd_HHmmss}.db");
            BackupTo(result.BackupPath);
        }
        catch (Exception ex)
        {
            result.Conflicts.Add($"Backup failed — migration aborted: {ex.Message}");
            return result;
        }

        using var tx = _conn.BeginTransaction();
        try
        {
            foreach (var (id, old, digits) in empRows)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE Employees SET Uid=$d WHERE Id=$id";
                cmd.Parameters.AddWithValue("$d", digits);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                result.EmployeesMigrated++;
                result.Changes.Add($"Employee {id}: '{old}' -> '{digits}'");
                LogAuditTx(tx, null, "uid-migrate", $"Employee {id}: '{old}' -> '{digits}'");
            }
            foreach (var (id, old, digits) in batchRows)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE BatchRows SET Uid=$d WHERE Id=$id";
                cmd.Parameters.AddWithValue("$d", digits);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                result.BatchRowsMigrated++;
                result.Changes.Add($"BatchRow {id}: '{old}' -> '{digits}'");
                LogAuditTx(tx, null, "uid-migrate", $"BatchRow {id}: '{old}' -> '{digits}'");
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return result;
    }

    private void LogAuditTx(SqliteTransaction tx, long? employeeId, string action, string detail)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO AuditLog (EmployeeId, Action, Detail, User, At)
            VALUES ($e,$a,$d,$u,$t)
            """;
        cmd.Parameters.AddWithValue("$e", (object?)employeeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$d", detail);
        cmd.Parameters.AddWithValue("$u", Environment.UserName);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------- Employees ----------

    public Employee? FindEmployeeByUid(string uid)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Employees WHERE Uid = $uid AND DeletedAt IS NULL";
        cmd.Parameters.AddWithValue("$uid", uid);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEmployee(r) : null;
    }

    public List<Employee> SearchEmployees(string term)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM Employees
            WHERE (Uid LIKE $t OR Name LIKE $t) AND DeletedAt IS NULL
            ORDER BY Name LIMIT 100
            """;
        cmd.Parameters.AddWithValue("$t", $"%{term}%");
        var list = new List<Employee>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEmployee(r));
        return list;
    }

    public List<Employee> AllEmployees()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Employees WHERE DeletedAt IS NULL ORDER BY Name";
        var list = new List<Employee>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEmployee(r));
        return list;
    }

    public List<Employee> DeletedEmployees()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Employees WHERE DeletedAt IS NOT NULL ORDER BY Name";
        var list = new List<Employee>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEmployee(r));
        return list;
    }

    public Employee? FindEmployeeById(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Employees WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEmployee(r) : null;
    }

    public void SoftDeleteEmployee(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE Employees SET DeletedAt=$d WHERE Id=$id";
        cmd.Parameters.AddWithValue("$d", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void RestoreEmployee(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE Employees SET DeletedAt=NULL WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Permanently deletes an employee and their card issues
    /// ("Remove for good" in Deleted records). Audit entries are kept.</summary>
    public void PurgeEmployee(long id)
    {
        using var tx = _conn.BeginTransaction();
        try
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM CardIssues WHERE EmployeeId=$id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM Employees WHERE Id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    /// <summary>Hard-deletes soft-deleted employees older than <paramref name="days"/>.
    /// Returns the number purged. Called on Records refresh for the 30-day retention.</summary>
    public int PurgeDeletedOlderThan(int days)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var ids = new List<long>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM Employees WHERE DeletedAt IS NOT NULL AND DeletedAt < $c";
            cmd.Parameters.AddWithValue("$c", cutoff.ToString("O"));
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        foreach (var id in ids) PurgeEmployee(id);
        return ids.Count;
    }

    /// <summary>Audit history for one employee, newest first.</summary>
    public List<AuditEntry> AuditForEmployee(long employeeId, int limit = 50)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM AuditLog WHERE EmployeeId=$e ORDER BY Id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$e", employeeId);
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AuditEntry
            {
                Id = r.GetInt64(0),
                EmployeeId = r.IsDBNull(1) ? null : r.GetInt64(1),
                Action = r.GetString(2),
                Detail = r.GetString(3),
                User = r.GetString(4),
                At = DateTimeOffset.Parse(r.GetString(5))
            });
        }
        return list;
    }

    /// <summary>Hard-deletes every employee, issue, and batch row. Audit log kept.</summary>
    public void ClearAll()
    {
        Exec("DELETE FROM CardIssues; DELETE FROM Employees; DELETE FROM BatchItems; DELETE FROM BatchJobs;");
        LogAudit(null, "clear-all", "All records deleted");
    }

    /// <summary>Consistent snapshot via VACUUM INTO. Returns destination path.</summary>
    public string BackupTo(string destPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"VACUUM INTO '{destPath.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
        return destPath;
    }

    /// <summary>
    /// Copies a backup file over dbPath. The caller must have disposed any open
    /// store on dbPath and created a safety backup first.
    /// </summary>
    public static void RestoreFromBackup(string backupPath, string dbPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup not found.", backupPath);
        // Pooled connections keep WAL sidecars alive after Dispose.
        SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var f = dbPath + ext;
            if (File.Exists(f)) File.Delete(f);
        }
        File.Copy(backupPath, dbPath);
    }

    // ---------- Audit ----------

    /// <summary>Nulls photo-path references whose cache files were deleted as stale.</summary>
    public void ClearPhotoPaths(IEnumerable<string> missingPaths)
    {
        var set = new HashSet<string>(missingPaths, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return;
        using var tx = _conn.BeginTransaction();
        foreach (var (table, col) in new[] { ("Employees", "PhotoPath"), ("BatchRows", "CutoutPath") })
        {
            var ids = new List<(long Id, string Path)>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT Id, {col} FROM {table} WHERE {col} IS NOT NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read()) ids.Add((r.GetInt64(0), r.GetString(1)));
            }
            foreach (var (id, path) in ids)
                if (set.Contains(path))
                {
                    using var u = _conn.CreateCommand();
                    u.Transaction = tx;
                    u.CommandText = $"UPDATE {table} SET {col}=NULL WHERE Id=$id";
                    u.Parameters.AddWithValue("$id", id);
                    u.ExecuteNonQuery();
                }
        }
        tx.Commit();
    }

    public void LogAudit(long? employeeId, string action, string detail, string? user = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AuditLog (EmployeeId, Action, Detail, User, At)
            VALUES ($e,$a,$d,$u,$t)
            """;
        cmd.Parameters.AddWithValue("$e", (object?)employeeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$d", detail);
        cmd.Parameters.AddWithValue("$u", user ?? Environment.UserName);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<AuditEntry> RecentAudit(int limit = 50)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM AuditLog ORDER BY Id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AuditEntry
            {
                Id = r.GetInt64(0),
                EmployeeId = r.IsDBNull(1) ? null : r.GetInt64(1),
                Action = r.GetString(2),
                Detail = r.GetString(3),
                User = r.GetString(4),
                At = DateTimeOffset.Parse(r.GetString(5))
            });
        }
        return list;
    }

    public long InsertEmployee(Employee e)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Employees (Uid, Name, BloodGroup, Position, ContractType, PhotoPath, CreatedAt)
            VALUES ($uid, $name, $bg, $pos, $ct, $photo, $created)
            RETURNING Id
            """;
        cmd.Parameters.AddWithValue("$uid", e.Uid);
        cmd.Parameters.AddWithValue("$name", e.Name);
        cmd.Parameters.AddWithValue("$bg", e.BloodGroup);
        cmd.Parameters.AddWithValue("$pos", e.Position);
        cmd.Parameters.AddWithValue("$ct", e.ContractType);
        cmd.Parameters.AddWithValue("$photo", (object?)e.PhotoPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", e.CreatedAt.ToString("O"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void UpdateEmployee(Employee e, bool updateUid = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = updateUid
            ? """
            UPDATE Employees SET Uid=$uid, Name=$name, BloodGroup=$bg, Position=$pos,
                ContractType=$ct, PhotoPath=$photo WHERE Id=$id
            """
            : """
            UPDATE Employees SET Name=$name, BloodGroup=$bg, Position=$pos,
                ContractType=$ct, PhotoPath=$photo WHERE Id=$id
            """;
        if (updateUid) cmd.Parameters.AddWithValue("$uid", e.Uid);
        cmd.Parameters.AddWithValue("$id", e.Id);
        cmd.Parameters.AddWithValue("$name", e.Name);
        cmd.Parameters.AddWithValue("$bg", e.BloodGroup);
        cmd.Parameters.AddWithValue("$pos", e.Position);
        cmd.Parameters.AddWithValue("$ct", e.ContractType);
        cmd.Parameters.AddWithValue("$photo", (object?)e.PhotoPath ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Edits an employee record. If contract type or print date changed, a new
    /// card issue version is created with recalculated validity. UID changes are
    /// allowed (caller must have confirmed + checked duplicates).
    /// </summary>
    public CardIssue? EditEmployee(Employee emp, string name, string blood, string position,
        string contract, DateOnly printDate, bool uidChanged)
    {
        var oldIssue = LatestIssue(emp.Id);
        var oldContract = emp.ContractType;
        emp.Name = name; emp.BloodGroup = blood; emp.Position = position;
        emp.ContractType = contract;
        UpdateEmployee(emp, updateUid: uidChanged);
        LogAudit(emp.Id, "edit", $"Edited record (uid change: {uidChanged})");

        if (oldIssue == null || oldIssue.PrintDate != printDate || oldContract != contract)
        {
            var issue = new CardIssue
            {
                EmployeeId = emp.Id,
                Version = NextIssueVersion(emp.Id),
                PrintDate = printDate,
                ValidUntil = Validity.CalculateValidUntil(printDate, emp.ContractType)
            };
            issue.Id = InsertIssue(issue);
            return issue;
        }
        return null;
    }

    // ---------- Batch sessions / rows (persistent, plan §7-8) ----------

    public long CreateBatchSession(string name = "Batch")
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO BatchSessions (Name, Status, CreatedAt) VALUES ($n,'Open',$c) RETURNING Id";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Most recent unfinished session, if any.</summary>
    public BatchSession? LatestOpenSession()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM BatchSessions WHERE Status='Open' ORDER BY Id DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new BatchSession
        {
            Id = r.GetInt64(0), Name = r.GetString(1), Status = r.GetString(2),
            CreatedAt = DateTimeOffset.Parse(r.GetString(3))
        };
    }

    public void CompleteBatchSession(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE BatchSessions SET Status='Completed' WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<BatchSession> BatchSessions()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM BatchSessions ORDER BY Id DESC";
        var list = new List<BatchSession>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new BatchSession
            {
                Id = r.GetInt64(0), Name = r.GetString(1), Status = r.GetString(2),
                CreatedAt = DateTimeOffset.Parse(r.GetString(3))
            });
        return list;
    }

    /// <summary>Inserts or updates a batch row by Id (Id==0 inserts).</summary>
    public long SaveBatchRow(BatchRowEntry row)
    {
        row.UpdatedAt = DateTimeOffset.UtcNow;
        using var cmd = _conn.CreateCommand();
        if (row.Id == 0)
        {
            cmd.CommandText = """
                INSERT INTO BatchRows (SessionId, Uid, Name, BloodGroup, Position,
                    ContractType, PrintDate, PhotoPath, CutoutPath, BgMode, Zoom,
                    OffsetX, OffsetY, AutoScale, AutoOffsetX, AutoOffsetY, Status, Error,
                    AttemptCount, CardIssueId, Approved, UpdatedAt)
                VALUES ($sid,$uid,$name,$bg,$pos,$ct,$pd,$photo,$cutout,$mode,$z,$ox,$oy,
                    $as,$aox,$aoy,$st,$err,$att,$iid,$ap,$up) RETURNING Id
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE BatchRows SET Uid=$uid, Name=$name, BloodGroup=$bg,
                    Position=$pos, ContractType=$ct, PrintDate=$pd, PhotoPath=$photo,
                    CutoutPath=$cutout, BgMode=$mode, Zoom=$z, OffsetX=$ox, OffsetY=$oy,
                    AutoScale=$as, AutoOffsetX=$aox, AutoOffsetY=$aoy,
                    Status=$st, Error=$err, AttemptCount=$att, CardIssueId=$iid,
                    Approved=$ap, UpdatedAt=$up WHERE Id=$id
                """;
            cmd.Parameters.AddWithValue("$id", row.Id);
        }
        cmd.Parameters.AddWithValue("$sid", row.SessionId);
        cmd.Parameters.AddWithValue("$uid", row.Uid);
        cmd.Parameters.AddWithValue("$name", row.Name);
        cmd.Parameters.AddWithValue("$bg", row.BloodGroup);
        cmd.Parameters.AddWithValue("$pos", row.Position);
        cmd.Parameters.AddWithValue("$ct", row.ContractType);
        cmd.Parameters.AddWithValue("$pd", row.PrintDate);
        cmd.Parameters.AddWithValue("$photo", (object?)row.PhotoPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cutout", (object?)row.CutoutPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mode", row.BgMode);
        cmd.Parameters.AddWithValue("$z", row.Zoom);
        cmd.Parameters.AddWithValue("$ox", row.OffsetX);
        cmd.Parameters.AddWithValue("$oy", row.OffsetY);
        cmd.Parameters.AddWithValue("$as", row.AutoScale);
        cmd.Parameters.AddWithValue("$aox", row.AutoOffsetX);
        cmd.Parameters.AddWithValue("$aoy", row.AutoOffsetY);
        cmd.Parameters.AddWithValue("$st", row.Status);
        cmd.Parameters.AddWithValue("$err", (object?)row.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$att", row.AttemptCount);
        cmd.Parameters.AddWithValue("$iid", (object?)row.CardIssueId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ap", row.Approved ? 1 : 0);
        cmd.Parameters.AddWithValue("$up", row.UpdatedAt.ToString("O"));
        if (row.Id == 0)
            return (long)(cmd.ExecuteScalar() ?? 0L);
        cmd.ExecuteNonQuery();
        return row.Id;
    }

    public List<BatchRowEntry> BatchRows(long sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM BatchRows WHERE SessionId=$sid ORDER BY Id";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        var list = new List<BatchRowEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadBatchRow(r));
        return list;
    }

    public void DeleteBatchRow(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM BatchRows WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static BatchRowEntry ReadBatchRow(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        SessionId = r.GetInt64(r.GetOrdinal("SessionId")),
        Uid = r.GetString(r.GetOrdinal("Uid")),
        Name = r.GetString(r.GetOrdinal("Name")),
        BloodGroup = r.GetString(r.GetOrdinal("BloodGroup")),
        Position = r.GetString(r.GetOrdinal("Position")),
        ContractType = r.GetString(r.GetOrdinal("ContractType")),
        PrintDate = r.GetString(r.GetOrdinal("PrintDate")),
        PhotoPath = r.IsDBNull(r.GetOrdinal("PhotoPath")) ? null : r.GetString(r.GetOrdinal("PhotoPath")),
        CutoutPath = r.IsDBNull(r.GetOrdinal("CutoutPath")) ? null : r.GetString(r.GetOrdinal("CutoutPath")),
        BgMode = r.GetString(r.GetOrdinal("BgMode")),
        Zoom = r.GetDouble(r.GetOrdinal("Zoom")),
        OffsetX = r.GetDouble(r.GetOrdinal("OffsetX")),
        OffsetY = r.GetDouble(r.GetOrdinal("OffsetY")),
        AutoScale = r.GetDouble(r.GetOrdinal("AutoScale")),
        AutoOffsetX = r.GetDouble(r.GetOrdinal("AutoOffsetX")),
        AutoOffsetY = r.GetDouble(r.GetOrdinal("AutoOffsetY")),
        Status = r.GetString(r.GetOrdinal("Status")),
        Error = r.IsDBNull(r.GetOrdinal("Error")) ? null : r.GetString(r.GetOrdinal("Error")),
        AttemptCount = r.GetInt32(r.GetOrdinal("AttemptCount")),
        CardIssueId = r.IsDBNull(r.GetOrdinal("CardIssueId")) ? null : r.GetInt64(r.GetOrdinal("CardIssueId")),
        Approved = r.GetInt32(r.GetOrdinal("Approved")) != 0,
        UpdatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("UpdatedAt")))
    };

    // ---------- Issue transaction (plan §5) ----------

    /// <summary>Existing issue for an idempotency key, if any.</summary>
    public CardIssue? FindIssueByKey(string issueKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM CardIssues WHERE IssueKey=$k";
        cmd.Parameters.AddWithValue("$k", issueKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadIssue(r) : null;
    }

    /// <summary>Employee lookup including soft-deleted rows (for issue checks).</summary>
    public Employee? FindEmployeeByUidIncludingDeleted(string uid)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Employees WHERE Uid = $uid";
        cmd.Parameters.AddWithValue("$uid", uid);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEmployee(r) : null;
    }

    /// <summary>
    /// Atomically saves an issued card: upserts the Employee, inserts the
    /// CardIssue (with IssueKey), and writes an audit entry — all in one
    /// transaction. Idempotent: if issueKey already exists, returns the
    /// existing issue with created=false and changes nothing.
    /// </summary>
    public (Employee Emp, CardIssue Issue, bool Created) SaveIssuedCard(
        string uid, string name, string blood, string position, string contract,
        DateOnly printDate, string? photoPath, string issueKey, string? detail = null)
    {
        using var tx = _conn.BeginTransaction();
        try
        {
            // Idempotency: same key -> return existing, no second record.
            using (var chk = _conn.CreateCommand())
            {
                chk.Transaction = tx;
                chk.CommandText = "SELECT * FROM CardIssues WHERE IssueKey=$k";
                chk.Parameters.AddWithValue("$k", issueKey);
                using var r = chk.ExecuteReader();
                if (r.Read())
                {
                    var existing = ReadIssue(r);
                    r.Close();
                    var emp0 = FindEmployeeByIdTx(tx, existing.EmployeeId);
                    tx.Commit();
                    return (emp0!, existing, false);
                }
            }

            // Upsert employee (deleted record blocks the UID).
            var existingEmp = FindEmployeeByUidTx(tx, uid);
            if (existingEmp?.IsDeleted == true)
                throw new InvalidOperationException($"UID {uid} belongs to a deleted record — restore it first.");

            Employee emp;
            if (existingEmp == null)
            {
                emp = new Employee
                {
                    Uid = uid, Name = name, BloodGroup = blood,
                    Position = position, ContractType = contract, PhotoPath = photoPath
                };
                using (var ins = _conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT INTO Employees (Uid, Name, BloodGroup, Position, ContractType, PhotoPath, CreatedAt)
                        VALUES ($uid,$name,$bg,$pos,$ct,$photo,$created) RETURNING Id
                        """;
                    ins.Parameters.AddWithValue("$uid", emp.Uid);
                    ins.Parameters.AddWithValue("$name", emp.Name);
                    ins.Parameters.AddWithValue("$bg", emp.BloodGroup);
                    ins.Parameters.AddWithValue("$pos", emp.Position);
                    ins.Parameters.AddWithValue("$ct", emp.ContractType);
                    ins.Parameters.AddWithValue("$photo", (object?)photoPath ?? DBNull.Value);
                    ins.Parameters.AddWithValue("$created", emp.CreatedAt.ToString("O"));
                    emp.Id = (long)(ins.ExecuteScalar() ?? 0L);
                }
            }
            else
            {
                emp = existingEmp;
                emp.Name = name; emp.BloodGroup = blood; emp.Position = position;
                emp.ContractType = contract; emp.PhotoPath = photoPath ?? emp.PhotoPath;
                using (var upd = _conn.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = """
                        UPDATE Employees SET Name=$name, BloodGroup=$bg, Position=$pos,
                            ContractType=$ct, PhotoPath=$photo WHERE Id=$id
                        """;
                    upd.Parameters.AddWithValue("$id", emp.Id);
                    upd.Parameters.AddWithValue("$name", emp.Name);
                    upd.Parameters.AddWithValue("$bg", emp.BloodGroup);
                    upd.Parameters.AddWithValue("$pos", emp.Position);
                    upd.Parameters.AddWithValue("$ct", emp.ContractType);
                    upd.Parameters.AddWithValue("$photo", (object?)emp.PhotoPath ?? DBNull.Value);
                    upd.ExecuteNonQuery();
                }
            }

            var issue = new CardIssue
            {
                EmployeeId = emp.Id,
                Version = NextIssueVersionTx(tx, emp.Id),
                PrintDate = printDate,
                ValidUntil = Validity.CalculateValidUntil(printDate, emp.ContractType),
                IssueKey = issueKey
            };
            using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO CardIssues (EmployeeId, Version, PrintDate, ValidUntil, IssuedAt,
                        OutputPdfPath, OutputPngPath, IssueKey)
                    VALUES ($eid,$ver,$pd,$vu,$ia,$pdf,$png,$key) RETURNING Id
                    """;
                ins.Parameters.AddWithValue("$eid", issue.EmployeeId);
                ins.Parameters.AddWithValue("$ver", issue.Version);
                ins.Parameters.AddWithValue("$pd", issue.PrintDate.ToString("yyyy-MM-dd"));
                ins.Parameters.AddWithValue("$vu", issue.ValidUntil.ToString("yyyy-MM-dd"));
                ins.Parameters.AddWithValue("$ia", issue.IssuedAt.ToString("O"));
                ins.Parameters.AddWithValue("$pdf", DBNull.Value);
                ins.Parameters.AddWithValue("$png", DBNull.Value);
                ins.Parameters.AddWithValue("$key", issueKey);
                issue.Id = (long)(ins.ExecuteScalar() ?? 0L);
            }

            // Audit inside the same transaction.
            using (var aud = _conn.CreateCommand())
            {
                aud.Transaction = tx;
                aud.CommandText = """
                    INSERT INTO AuditLog (EmployeeId, Action, Detail, User, At)
                    VALUES ($e,'issue',$d,$u,$t)
                    """;
                aud.Parameters.AddWithValue("$e", emp.Id);
                aud.Parameters.AddWithValue("$d", detail ?? $"Issued card v{issue.Version} ({issueKey})");
                aud.Parameters.AddWithValue("$u", Environment.UserName);
                aud.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                aud.ExecuteNonQuery();
            }

            tx.Commit();
            return (emp, issue, true);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private Employee? FindEmployeeByUidTx(SqliteTransaction tx, string uid)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT * FROM Employees WHERE Uid=$uid";
        cmd.Parameters.AddWithValue("$uid", uid);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEmployee(r) : null;
    }

    private Employee? FindEmployeeByIdTx(SqliteTransaction tx, long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT * FROM Employees WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEmployee(r) : null;
    }

    private int NextIssueVersionTx(SqliteTransaction tx, long employeeId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(Version),0)+1 FROM CardIssues WHERE EmployeeId=$eid";
        cmd.Parameters.AddWithValue("$eid", employeeId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---------- Card issues ----------

    public long InsertIssue(CardIssue i)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CardIssues (EmployeeId, Version, PrintDate, ValidUntil, IssuedAt, OutputPdfPath, OutputPngPath)
            VALUES ($eid, $ver, $pd, $vu, $ia, $pdf, $png)
            RETURNING Id
            """;
        cmd.Parameters.AddWithValue("$eid", i.EmployeeId);
        cmd.Parameters.AddWithValue("$ver", i.Version);
        cmd.Parameters.AddWithValue("$pd", i.PrintDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$vu", i.ValidUntil.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$ia", i.IssuedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$pdf", (object?)i.OutputPdfPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$png", (object?)i.OutputPngPath ?? DBNull.Value);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Sets rendered file paths on an existing issue row.</summary>
    public void SetIssueOutputs(long issueId, string? pdfPath, string? pngPath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE CardIssues SET OutputPdfPath=$p, OutputPngPath=$n WHERE Id=$id";
        cmd.Parameters.AddWithValue("$p", (object?)pdfPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)pngPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", issueId);
        cmd.ExecuteNonQuery();
    }

    public int NextIssueVersion(long employeeId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(Version),0)+1 FROM CardIssues WHERE EmployeeId=$eid";
        cmd.Parameters.AddWithValue("$eid", employeeId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public CardIssue? LatestIssue(long employeeId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM CardIssues WHERE EmployeeId=$eid ORDER BY Version DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$eid", employeeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadIssue(r) : null;
    }

    public List<CardIssue> IssuesForEmployee(long employeeId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM CardIssues WHERE EmployeeId=$eid ORDER BY Version";
        cmd.Parameters.AddWithValue("$eid", employeeId);
        var list = new List<CardIssue>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadIssue(r));
        return list;
    }

    // ---------- Batch ----------

    public long CreateBatchJob(string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO BatchJobs (Name, Status, CreatedAt) VALUES ($n,'Open',$c) RETURNING Id";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void AddBatchItem(long jobId, int rowIndex, string payloadJson)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO BatchItems (JobId, RowIndex, Status, PayloadJson)
            VALUES ($j,$i,'Pending',$p)
            """;
        cmd.Parameters.AddWithValue("$j", jobId);
        cmd.Parameters.AddWithValue("$i", rowIndex);
        cmd.Parameters.AddWithValue("$p", payloadJson);
        cmd.ExecuteNonQuery();
    }

    public List<BatchItem> BatchItems(long jobId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM BatchItems WHERE JobId=$j ORDER BY RowIndex";
        cmd.Parameters.AddWithValue("$j", jobId);
        var list = new List<BatchItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadBatchItem(r));
        return list;
    }

    public void SetBatchItemStatus(long itemId, string status, string? error = null,
        long? employeeId = null, long? issueId = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE BatchItems SET Status=$s, Error=$e,
                EmployeeId=COALESCE($eid, EmployeeId),
                CardIssueId=COALESCE($iid, CardIssueId)
            WHERE Id=$id
            """;
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eid", (object?)employeeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$iid", (object?)issueId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", itemId);
        cmd.ExecuteNonQuery();
    }

    // ---------- Readers ----------

    private static Employee ReadEmployee(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        Uid = r.GetString(r.GetOrdinal("Uid")),
        Name = r.GetString(r.GetOrdinal("Name")),
        BloodGroup = r.GetString(r.GetOrdinal("BloodGroup")),
        Position = r.GetString(r.GetOrdinal("Position")),
        ContractType = r.GetString(r.GetOrdinal("ContractType")),
        PhotoPath = r.IsDBNull(r.GetOrdinal("PhotoPath")) ? null : r.GetString(r.GetOrdinal("PhotoPath")),
        CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("CreatedAt"))),
        DeletedAt = r.IsDBNull(r.GetOrdinal("DeletedAt")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("DeletedAt")))
    };

    private static CardIssue ReadIssue(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        EmployeeId = r.GetInt64(r.GetOrdinal("EmployeeId")),
        Version = r.GetInt32(r.GetOrdinal("Version")),
        PrintDate = DateOnly.Parse(r.GetString(r.GetOrdinal("PrintDate"))),
        ValidUntil = DateOnly.Parse(r.GetString(r.GetOrdinal("ValidUntil"))),
        IssuedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("IssuedAt"))),
        OutputPdfPath = r.IsDBNull(r.GetOrdinal("OutputPdfPath")) ? null : r.GetString(r.GetOrdinal("OutputPdfPath")),
        OutputPngPath = r.IsDBNull(r.GetOrdinal("OutputPngPath")) ? null : r.GetString(r.GetOrdinal("OutputPngPath")),
        IssueKey = r.IsDBNull(r.GetOrdinal("IssueKey")) ? null : r.GetString(r.GetOrdinal("IssueKey"))
    };

    private static BatchItem ReadBatchItem(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        JobId = r.GetInt64(r.GetOrdinal("JobId")),
        RowIndex = r.GetInt32(r.GetOrdinal("RowIndex")),
        Status = r.GetString(r.GetOrdinal("Status")),
        Error = r.IsDBNull(r.GetOrdinal("Error")) ? null : r.GetString(r.GetOrdinal("Error")),
        PayloadJson = r.GetString(r.GetOrdinal("PayloadJson")),
        EmployeeId = r.IsDBNull(r.GetOrdinal("EmployeeId")) ? null : r.GetInt64(r.GetOrdinal("EmployeeId")),
        CardIssueId = r.IsDBNull(r.GetOrdinal("CardIssueId")) ? null : r.GetInt64(r.GetOrdinal("CardIssueId"))
    };

    public void Dispose() => _conn.Dispose();
}
