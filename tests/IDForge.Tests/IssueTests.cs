using IDForge.Core.Data;
using IDForge.Core.Issuing;
using IDForge.Core.Photo;
using SkiaSharp;
using Xunit;

namespace IDForge.Tests;

/// <summary>Plan §11: issue transaction, idempotency, UID-taken, failure isolation, resume.</summary>
public class IssueTests : IDisposable
{
    private readonly string _dir;
    private readonly CardStore _store;
    private readonly string _out;

    public IssueTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"idcard-issue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new CardStore(Path.Combine(_dir, "test.db"));
        _out = Path.Combine(_dir, "output");
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static SKBitmap Cutout()
    {
        var bmp = new SKBitmap(120, 160, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor(180, 140, 110) };
        canvas.DrawCircle(60, 50, 30, paint);   // face
        canvas.DrawRoundRect(new SKRect(10, 90, 110, 155), 12, 12, paint); // shoulders
        canvas.Flush();
        return bmp;
    }

    private IssueRequest Req(string uid = "UID20250101", string key = "k1", bool requireNew = true)
        => new(uid, "Test User", "O+", "Manager", "Permanent",
            new DateOnly(2026, 1, 15), Cutout(), null, key,
            RequireNewUid: requireNew, IncludePdf: true);

    [Fact]
    public void Issue_Valid_Row_Creates_Employee_Issue_And_Files()
    {
        var issuer = new CardIssuer(_store, _out, Path.Combine(_dir, "photos"));
        var outcome = issuer.Issue(Req());
        Assert.True(outcome.Success, $"failed at {outcome.Step}: {outcome.Error}");
        Assert.NotNull(outcome.Issue);
        Assert.True(File.Exists(outcome.PngPath) && new FileInfo(outcome.PngPath!).Length > 1000);
        Assert.True(File.Exists(outcome.PdfPath) && new FileInfo(outcome.PdfPath!).Length > 1000);
        Assert.Single(_store.AllEmployees());
        Assert.Single(_store.RecentAudit(10));
        Assert.Equal("issue", _store.RecentAudit(10)[0].Action);
    }

    [Fact]
    public void Issue_Same_Key_Twice_Creates_One_Issue()
    {
        var issuer = new CardIssuer(_store, _out, Path.Combine(_dir, "photos"));
        var first = issuer.Issue(Req());
        var second = issuer.Issue(Req());   // same issue key -> idempotent
        Assert.True(first.Success && second.Success);
        Assert.True(second.AlreadyIssued);
        Assert.Equal(first.Issue!.Id, second.Issue!.Id);
        Assert.Single(_store.IssuesForEmployee(first.Issue.EmployeeId));
    }

    [Fact]
    public void Issue_Uid_Taken_Blocked_When_RequireNewUid()
    {
        // Stored UID is digits-only (issuer normalizes "UID20250101" -> "20250101").
        _store.InsertEmployee(new Employee
        {
            Uid = "20250101", Name = "Existing", BloodGroup = "A+",
            Position = "Manager", ContractType = "Permanent"
        });
        var before = _store.AllEmployees().Count;
        var outcome = new CardIssuer(_store, _out, Path.Combine(_dir, "photos")).Issue(Req());
        Assert.False(outcome.Success);
        Assert.Equal("validate", outcome.Step);
        Assert.Equal(before, _store.AllEmployees().Count);
        Assert.Empty(Directory.GetFiles(_out));
    }

    [Fact]
    public void Issue_Existing_Uid_Allowed_For_Reissue()
    {
        var issuer = new CardIssuer(_store, _out, Path.Combine(_dir, "photos"));
        var first = issuer.Issue(Req(key: "k1", requireNew: false));
        Assert.True(first.Success, $"first failed at {first.Step}: {first.Error}");
        var second = issuer.Issue(Req(key: "k2", requireNew: false));
        Assert.True(second.Success, $"second failed at {second.Step}: {second.Error}");
        Assert.Equal(2, second.Issue!.Version);   // re-issue bumps version
        Assert.Equal(2, _store.IssuesForEmployee(first.Issue.EmployeeId).Count);
    }

    [Fact]
    public void Issue_Render_Failure_Leaves_Records_Unchanged()
    {
        // Force the PNG write to fail: pre-create a DIRECTORY at the output path.
        var blocker = Path.Combine(_out, "UID20250101_v1.png");
        Directory.CreateDirectory(blocker);
        var outcome = new CardIssuer(_store, _out, Path.Combine(_dir, "photos")).Issue(Req());
        Assert.False(outcome.Success);
        Assert.Equal("render", outcome.Step);
        Assert.Empty(_store.AllEmployees());
        Assert.Empty(_store.RecentAudit(10));
    }

    [Fact]
    public void BatchSession_RoundTrips_And_Resume_Works()
    {
        var sid = _store.CreateBatchSession("Test batch");
        var entry = new BatchRowEntry
        {
            SessionId = sid, Uid = "UID20250102", Name = "Resume Test",
            Position = "Intern", Status = "NeedsReview",
            BgMode = "Model", Zoom = 1.25, OffsetX = -30, OffsetY = 40,
            AutoScale = 0.5, AutoOffsetX = 10, AutoOffsetY = -5,
            AttemptCount = 2, Error = "prev fail"
        };
        entry.Id = _store.SaveBatchRow(entry);
        entry.Status = "Failed"; entry.Error = "render blew up";
        _store.SaveBatchRow(entry);   // update same row

        // Simulate restart: new CardStore on the same db file.
        _store.Dispose();
        using var store2 = new CardStore(Path.Combine(_dir, "test.db"));
        var sess = store2.LatestOpenSession();
        Assert.NotNull(sess);
        Assert.Equal(sid, sess!.Id);
        var rows = store2.BatchRows(sid);
        var row = Assert.Single(rows);
        // Reopening ran the UID migration: "UID20250102" -> "20250102".
        Assert.Equal("20250102", row.Uid);
        Assert.Equal("Failed", row.Status);
        Assert.Equal("render blew up", row.Error);
        Assert.Equal(2, row.AttemptCount);
        Assert.Equal(1.25, row.Zoom);
        Assert.Equal(-30, row.OffsetX);
        Assert.Equal(40, row.OffsetY);
        Assert.Equal(0.5, row.AutoScale);
        Assert.Equal(10, row.AutoOffsetX);
        Assert.Equal(-5, row.AutoOffsetY);
        Assert.Equal("Model", row.BgMode);

        // No Employee/CardIssue may exist for a draft/failed row.
        Assert.Empty(store2.AllEmployees());

        store2.CompleteBatchSession(sid);
        Assert.Null(store2.LatestOpenSession());
    }

    [Fact]
    public void BatchRow_Deletes_Cleanly()
    {
        var sid = _store.CreateBatchSession();
        var e = new BatchRowEntry { SessionId = sid, Uid = "U1", Name = "N", Position = "Intern" };
        e.Id = _store.SaveBatchRow(e);
        _store.DeleteBatchRow(e.Id);
        Assert.Empty(_store.BatchRows(sid));
    }
}
