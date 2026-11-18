using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MCPDashboard.InventoryScanner;

/// <summary>Writes the scan as inventory.json (everything) plus one CSV per kind of row, ready for Excel.</summary>
public static class ReportWriter
{
    public static void Write(string folder, IReadOnlyList<ServerResult> servers)
    {
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, "inventory.json"),
            JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true }));

        WriteCsv(Path.Combine(folder, "servers.csv"),
            ["Host", "IpAddress", "Reachable", "Role", "OS", "OsCaption", "OsBuild", "CpuSockets", "CpuCores", "LogicalProcessors",
             "MemoryGb", "SqlInstances", "SqlVersion", "SqlEdition", "SqlTopology", "Databases", "DatabasesInAg", "IisSites", "Errors"],
            servers.Select(s => new object?[]
            {
                s.Host, s.IpAddress, s.Reachable, s.Role, s.OsName, s.OsCaption, s.OsBuild, s.CpuSockets, s.CpuCores, s.LogicalProcessors,
                s.MemoryGb,
                Join(s.SqlInstances.Select(i => i.Instance)),
                Join(s.SqlInstances.Select(i => i.VersionName)),
                Join(s.SqlInstances.Select(i => i.Edition)),
                Join(s.SqlInstances.Select(i => i.Topology)),
                s.IsSqlServer ? s.SqlInstances.Sum(i => i.Databases.Count) : null,
                s.IsSqlServer ? s.SqlInstances.Sum(i => i.Databases.Count(d => d.InAvailabilityGroup)) : null,
                s.IsIis ? s.IisSites.Count : null,
                string.Join(" | ", s.Errors),
            }));

        WriteCsv(Path.Combine(folder, "sql_instances.csv"),
            ["Host", "Instance", "ConnectTo", "ServiceState", "SqlVersion", "ProductVersion", "ProductLevel", "Edition", "EditionFull",
             "HadrEnabled", "Topology", "AvailabilityGroups", "Databases", "DatabasesInAg", "Error"],
            servers.SelectMany(s => s.SqlInstances.Select(i => new object?[]
            {
                s.Host, i.Instance, i.ConnectTo, i.ServiceState, i.VersionName, i.ProductVersion, i.ProductLevel, i.Edition, i.EditionFull,
                i.HadrEnabled, i.Topology, i.AvailabilityGroups.Count, i.Databases.Count, i.Databases.Count(d => d.InAvailabilityGroup), i.Error,
            })));

        WriteCsv(Path.Combine(folder, "availability_groups.csv"),
            ["Host", "Instance", "AvailabilityGroup", "LocalRole", "Listener", "Databases"],
            servers.SelectMany(s => s.SqlInstances.SelectMany(i => i.AvailabilityGroups.Select(ag => new object?[]
            {
                s.Host, i.Instance, ag.Name, ag.LocalRole, ag.Listener,
                i.Databases.Count(d => string.Equals(d.AvailabilityGroup, ag.Name, StringComparison.OrdinalIgnoreCase)),
            }))));

        WriteCsv(Path.Combine(folder, "databases.csv"),
            ["Host", "Instance", "Database", "Topology", "AvailabilityGroup", "State", "RecoveryModel", "SizeMb", "CompatibilityLevel", "CreateDate", "LastFullBackup"],
            servers.SelectMany(s => s.SqlInstances.SelectMany(i => i.Databases.Select(d => new object?[]
            {
                s.Host, i.Instance, d.Name, d.InAvailabilityGroup ? "AG" : "Standalone", d.AvailabilityGroup, d.State, d.RecoveryModel,
                d.SizeMb, d.CompatibilityLevel, d.CreateDate, d.LastFullBackup,
            }))));

        WriteCsv(Path.Combine(folder, "iis_sites.csv"),
            ["Host", "SiteId", "Site", "State", "Bindings", "PhysicalPath", "AppPool", "RuntimeVersion", "PipelineMode", "Identity", "AutoStart", "Applications"],
            servers.SelectMany(s => s.IisSites.Select(site => new object?[]
            {
                s.Host, site.Id, site.Name, site.State, site.Bindings, site.PhysicalPath, site.AppPool, site.RuntimeVersion, site.PipelineMode,
                site.Identity, site.AutoStart, site.Applications,
            })));
    }

    private static string Join(IEnumerable<string?> values) =>
        string.Join(", ", values.Where(v => !string.IsNullOrEmpty(v)).Distinct());

    internal static void WriteCsv(string path, string[] header, IEnumerable<object?[]> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", header.Select(Escape)));
        foreach (var row in rows)
        {
            csv.AppendLine(string.Join(",", row.Select(v => Escape(Format(v)))));
        }
        // UTF-8 with BOM so Excel opens it with the right encoding.
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
