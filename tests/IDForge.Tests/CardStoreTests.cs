using IDForge.Core.Data;
using Xunit;

namespace IDForge.Tests;

public class CardStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly CardStore _store;

    public CardStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"idcard-test-{Guid.NewGuid():N}.db");
        _store = new CardStore(_dbPath);
    }

    [Fact]
    public void Employee_Insert_And_Find_By_Uid()
    {
        var e = new Employee
        {
            Uid = "UID20250318",
            Name = "Mehdi Hasan Sakib",
            BloodGroup = "N/A",
            Position = "Technical Executive",
            ContractType = "Contractual"
        };
        var id = _store.InsertEmployee(e);
        Assert.True(id > 0);

        var found = _store.FindEmployeeByUid("UID20250318");
        Assert.NotNull(found);
        Assert.Equal("Mehdi Hasan Sakib", found!.Name);
    }

    [Fact]
    public void Duplicate_Uid_Throws()
    {
        var e = new Employee
        {
            Uid = "UID1", Name = "A", BloodGroup = "N/A",
            Position = "Intern", ContractType = "Permanent"
        };
        _store.InsertEmployee(e);
        Assert.ThrowsAny<Exception>(() => _store.InsertEmployee(e));
    }

    [Fact]
    public void Issue_Version_Increments()
    {
        var e = new Employee
        {
            Uid = "UID2", Name = "B", BloodGroup = "O+",
            Position = "Manager", ContractType = "Permanent"
        };
        var eid = _store.InsertEmployee(e);
        Assert.Equal(1, _store.NextIssueVersion(eid));
        _store.InsertIssue(new CardIssue
        {
            EmployeeId = eid, Version = 1,
            PrintDate = new DateOnly(2026, 1, 1), ValidUntil = new DateOnly(2027, 1, 1)
        });
        Assert.Equal(2, _store.NextIssueVersion(eid));
    }

    [Fact]
    public void Batch_RoundTrip()
    {
        var jobId = _store.CreateBatchJob("Test batch");
        _store.AddBatchItem(jobId, 0, "{\"uid\":\"UID9\"}");
        var items = _store.BatchItems(jobId);
        Assert.Single(items);
        Assert.Equal("Pending", items[0].Status);
        _store.SetBatchItemStatus(items[0].Id, "Approved", employeeId: 5, issueId: 7);
        items = _store.BatchItems(jobId);
        Assert.Equal("Approved", items[0].Status);
        Assert.Equal(5, items[0].EmployeeId);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
