using System.Text;
using System.Text.Json;

namespace IdCardTool.Core.Data;

/// <summary>CSV/JSON export of employee records with their latest card issue.</summary>
public static class RecordsExporter
{
    public sealed record Row(Employee Emp, CardIssue? Issue);

    public static List<Row> Collect(CardStore store, bool includeDeleted = false)
    {
        var emps = includeDeleted ? store.DeletedEmployees() : store.AllEmployees();
        // includeDeleted path also needs non-deleted; merge both lists
        if (includeDeleted)
        {
            var all = store.AllEmployees();
            var set = new HashSet<long>(emps.Select(e => e.Id));
            emps = emps.Concat(all.Where(e => !set.Contains(e.Id))).ToList();
        }
        return emps.Select(e => new Row(e, store.LatestIssue(e.Id))).ToList();
    }

    private static string[] Fields(Row r) => new[]
    {
        r.Emp.Uid,
        r.Emp.Name,
        r.Emp.BloodGroup,
        r.Emp.Position,
        r.Emp.ContractType,
        r.Issue?.PrintDate.ToString("yyyy-MM-dd") ?? "",
        r.Issue?.ValidUntil.ToString("yyyy-MM-dd") ?? "",
        r.Issue?.IssuedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "",
        $"v{r.Issue?.Version ?? 0}",
        r.Emp.PhotoPath ?? "",
        r.Emp.IsDeleted ? "deleted" : "active",
        r.Emp.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
    };

    private static readonly string[] Header =
        { "UID", "Name", "BloodGroup", "Position", "ContractType", "PrintDate",
          "ValidUntil", "LastIssued", "LatestVersion", "PhotoPath", "Status", "CreatedAt" };

    public static string ToCsv(IEnumerable<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header.Select(Csv)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", Fields(r).Select(Csv)));
        return sb.ToString();
    }

    private static string Csv(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public static string ToJson(IEnumerable<Row> rows)
    {
        var list = rows.Select(r => new
        {
            uid = r.Emp.Uid,
            name = r.Emp.Name,
            bloodGroup = r.Emp.BloodGroup,
            position = r.Emp.Position,
            contractType = r.Emp.ContractType,
            printDate = r.Issue?.PrintDate.ToString("yyyy-MM-dd"),
            validUntil = r.Issue?.ValidUntil.ToString("yyyy-MM-dd"),
            lastIssued = r.Issue?.IssuedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            latestVersion = r.Issue?.Version ?? 0,
            photoPath = r.Emp.PhotoPath,
            status = r.Emp.IsDeleted ? "deleted" : "active",
            createdAt = r.Emp.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        });
        return JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
    }
}
