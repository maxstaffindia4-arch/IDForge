using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IDForge.Core;
using IDForge.Core.Data;
using IDForge.Core.Issuing;

namespace IDForge.Tests;

/// <summary>Fix J-E — Records behaviour: startup purge, edit banner,
/// "Last issued" column, 30-day expiry.</summary>
[Collection("Wpf")]
public class FixJPartETests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"idfjc_e_{Guid.NewGuid():N}");
    private readonly CardStore _store;

    public FixJPartETests()
    {
        Directory.CreateDirectory(_dir);
        _store = new CardStore(Path.Combine(_dir, "idcard.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Employee SeedEmployee(string uid = "20000001")
    {
        var e = new Employee
        {
            Uid = uid, Name = "Edit Target", BloodGroup = "O+",
            Position = "Manager", ContractType = "Permanent"
        };
        e.Id = _store.InsertEmployee(e);
        return e;
    }

    [Fact]
    public void Purge_Runs_At_Startup_Only_And_Audits_Each_Record()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            MainWindow.SkipResumePrompt = true;
            var w1 = new MainWindow();   // ctor runs the startup purge
            try
            {
                // Seed a record deleted >30 days ago directly in the store.
                var emp = new Employee
                {
                    Uid = "3" + Random.Shared.NextInt64(10000000, 99999999).ToString(),
                    Name = "Old Deleted", BloodGroup = "A+",
                    Position = "Intern", ContractType = "Contractual"
                };
                emp.Id = w1.Svc.Store.InsertEmployee(emp);
                w1.Svc.Store.SoftDeleteEmployee(emp.Id);
                using (var cmd = w1.Svc.Store.Connection.CreateCommand())
                {
                    cmd.CommandText = "UPDATE Employees SET DeletedAt=$d WHERE Id=$id";
                    cmd.Parameters.AddWithValue("$d", DateTimeOffset.UtcNow.AddDays(-40).ToString("O"));
                    cmd.Parameters.AddWithValue("$id", emp.Id);
                    cmd.ExecuteNonQuery();
                }

                // A refresh must NOT purge it.
                w1.RefreshRecordsPublic();
                Assert.Contains(w1.Svc.Store.DeletedEmployees(), d => d.Id == emp.Id);

                // A second window = a new startup: it purges, with an audit entry.
                var w2 = new MainWindow();
                try
                {
                    Assert.DoesNotContain(w2.Svc.Store.DeletedEmployees(), d => d.Id == emp.Id);
                    var audit = w2.Svc.Store.AuditForEmployee(emp.Id);
                    Assert.Contains(audit, a => a.Action == "purge");
                }
                finally { w2.Close(); }
            }
            finally { w1.Close(); }
        });
    }

    [Fact]
    public void Edit_Keeps_Card_Image_And_Photo_Refs()
    {
        var emp = SeedEmployee();
        var png = Path.Combine(_dir, "card_v1.png");
        File.WriteAllBytes(png, new byte[] { 1, 2, 3, 4 });
        var (emp2, issue1, _) = _store.SaveIssuedCard(
            emp.Uid, emp.Name, emp.BloodGroup, emp.Position, emp.ContractType,
            new DateOnly(2026, 1, 1), null, "editkey1",
            issuePhoto: new IssuePhotoRefs("issues/UID_x_v1/cutout.webp",
                "issues/UID_x_v1/original.jpg", "{\"bg\":\"PlainWhite\"}"));
        _store.SetIssueOutputs(issue1.Id, null, png);
        issue1 = _store.IssuesForEmployee(emp.Id).Single(i => i.Id == issue1.Id);

        // Edit that changes contract -> new version; image + photo must carry over.
        var issue2 = _store.EditEmployee(emp2, "Edit Target", "O+", "Manager",
            "Contractual", new DateOnly(2026, 6, 1), uidChanged: false);
        Assert.NotNull(issue2);
        Assert.Equal(2, issue2!.Version);
        Assert.Equal(png, issue2.OutputPngPath);
        Assert.Equal("issues/UID_x_v1/cutout.webp", issue2.PhotoCutoutPath);
        Assert.Equal("issues/UID_x_v1/original.jpg", issue2.PhotoOriginalPath);
        Assert.Equal("{\"bg\":\"PlainWhite\"}", issue2.PhotoSettingsJson);

        // The card image file itself is untouched.
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(png));

        // And it survives a reload from the DB.
        var reloaded = _store.IssuesForEmployee(emp.Id).Single(i => i.Id == issue2.Id);
        Assert.Equal(png, reloaded.OutputPngPath);
        Assert.Equal("issues/UID_x_v1/cutout.webp", reloaded.PhotoCutoutPath);
    }

    [Fact]
    public void Edit_Banner_Appears_With_Reissue_Button()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            MainWindow.SkipResumePrompt = true;
            var w = new MainWindow();
            var uid = "2" + Random.Shared.NextInt64(10000000, 99999999).ToString();
            try
            {
                var emp = new Employee
                {
                    Uid = uid, Name = "Banner Person", BloodGroup = "B+",
                    Position = "Supervisor", ContractType = "Permanent"
                };
                emp.Id = w.Svc.Store.InsertEmployee(emp);
                var issuedAt = DateTimeOffset.UtcNow.AddDays(-3);
                w.NotifyRecordEdited(emp, issuedAt);

                Assert.Equal(Visibility.Visible, w.PnlEditBanner.Visibility);
                Assert.Contains("Card image is from", w.TxtEditBanner.Text);
                Assert.Contains("Re-issue to update the card", w.TxtEditBanner.Text);
                Assert.Contains(issuedAt.LocalDateTime.ToString("dd/MM/yyyy"), w.TxtEditBanner.Text);
                var btn = FindDescendant<Button>(w.PnlEditBanner);
                Assert.NotNull(btn);
                Assert.Equal("Re-issue", btn!.Content);

                // Cleanup: remove the seeded record from the shared dev DB.
                w.Svc.Store.PurgeEmployee(emp.Id);
            }
            finally { w.Close(); }
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t) return t;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    [Fact]
    public void Column_Header_Reads_Last_Issued()
    {
        var xaml = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "IDForge", "MainWindow.xaml")));
        Assert.Contains("Header=\"Last issued\"", xaml);
        Assert.DoesNotContain("Header=\"Issued on\"", xaml);
    }

    [Fact]
    public void Expires_Soon_Is_Within_30_Days_Only()
    {
        var today = Validity.TodayInDhaka();
        Assert.Equal("Expires soon", MainWindow.ValidityKindFor(today, today));
        Assert.Equal("Expires soon", MainWindow.ValidityKindFor(today.AddDays(30), today));
        Assert.Equal("Active", MainWindow.ValidityKindFor(today.AddDays(31), today));
        Assert.Equal("Active", MainWindow.ValidityKindFor(today.AddDays(365), today));
        Assert.Equal("Expired", MainWindow.ValidityKindFor(today.AddDays(-1), today));
        Assert.Equal("None", MainWindow.ValidityKindFor(null, today));
    }
}
