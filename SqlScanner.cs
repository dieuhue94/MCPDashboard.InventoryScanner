using Microsoft.Data.SqlClient;

namespace MCPDashboard.InventoryScanner;

/// <summary>
/// Reads version, edition, AGs and user databases from one SQL Server instance with Windows authentication.
/// The account running the job needs VIEW SERVER STATE and VIEW ANY DATABASE (or sysadmin) on the instance.
/// </summary>
public static class SqlScanner
{
    public static async Task<SqlInstanceResult> ScanAsync(string host, ServiceInfo service, int timeoutSeconds)
    {
        var isDefault = service.Name.Equals(WmiScanner.DefaultSqlServiceName, StringComparison.OrdinalIgnoreCase);
        var instanceName = isDefault ? null : service.Name[WmiScanner.NamedSqlServicePrefix.Length..];
        var result = new SqlInstanceResult
        {
            Instance = instanceName ?? WmiScanner.DefaultSqlServiceName,
            ConnectTo = instanceName is null ? host : $@"{host}\{instanceName}",
            ServiceState = service.State,
        };

        if (service.State is not ("Running" or "Unknown"))
        {
            result.Error = $"service is {service.State}";
            return result;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = result.ConnectTo,
                InitialCatalog = "master",
                IntegratedSecurity = true,
                Encrypt = true,
                TrustServerCertificate = true,
                ConnectTimeout = timeoutSeconds,
                CommandTimeout = 120,
                ApplicationName = "MCPDashboard.InventoryScanner",
            };
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync();

            await using (var reader = await Command(conn, """
                SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                       CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                       CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)),
                       CAST(ISNULL(SERVERPROPERTY('IsHadrEnabled'), 0) AS int),
                       (SELECT cpu_count FROM sys.dm_os_sys_info);
                """).ExecuteReaderAsync())
            {
                await reader.ReadAsync();
                result.ProductVersion = reader.GetString(0);
                result.EditionFull = reader.GetString(1);
                result.ProductLevel = reader.GetString(2);
                result.HadrEnabled = reader.GetInt32(3) == 1;
                result.HostLogicalProcessors = reader.GetInt32(4);
            }

            var (major, minor) = ParseVersion(result.ProductVersion);
            result.VersionName = VersionName(major, minor);
            result.Edition = ShortEdition(result.EditionFull);

            // physical_memory_kb is SQL Server 2012+, sys.dm_os_host_info is 2017+.
            if (major >= 11)
            {
                result.HostMemoryMb = Convert.ToInt32(await Command(conn, "SELECT physical_memory_kb / 1024 FROM sys.dm_os_sys_info;").ExecuteScalarAsync());
            }
            if (major >= 14)
            {
                result.HostOs = await Command(conn, "SELECT TOP (1) host_distribution FROM sys.dm_os_host_info;").ExecuteScalarAsync() as string;
            }

            if (result.HadrEnabled)
            {
                await using var reader = await Command(conn, """
                    SELECT ag.name,
                           rs.role_desc,
                           (SELECT TOP (1) l.dns_name FROM sys.availability_group_listeners l WHERE l.group_id = ag.group_id)
                    FROM sys.availability_groups ag
                    LEFT JOIN sys.dm_hadr_availability_replica_states rs ON rs.group_id = ag.group_id AND rs.is_local = 1
                    ORDER BY ag.name;
                    """).ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result.AvailabilityGroups.Add(new AvailabilityGroupResult
                    {
                        Name = reader.GetString(0),
                        LocalRole = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Listener = reader.IsDBNull(2) ? null : reader.GetString(2),
                    });
                }
            }
            // database_id > 4 skips master, tempdb, model and msdb. sys.databases.replica_id (SQL Server 2012+) is set
            // when the database is in an AG and NULL when it is standalone.
            var hasReplicaId = major >= 11;
            var agColumns = hasReplicaId
                ? "CAST(CASE WHEN d.replica_id IS NULL THEN 0 ELSE 1 END AS bit), ag.name"
                : "CAST(0 AS bit), CAST(NULL AS sysname)";
            var agJoins = hasReplicaId
                ? """
                  LEFT JOIN sys.availability_replicas ar ON ar.replica_id = d.replica_id
                  LEFT JOIN sys.availability_groups ag ON ag.group_id = ar.group_id
                  """
                : "";
            await using (var reader = await Command(conn, $"""
                SELECT d.name, {agColumns}, d.state_desc, d.recovery_model_desc, CAST(d.compatibility_level AS int),
                       (SELECT CAST(SUM(CAST(mf.size AS bigint)) * 8 / 1024.0 AS decimal(18, 2)) FROM sys.master_files mf WHERE mf.database_id = d.database_id),
                       d.create_date,
                       fb.last_full_backup
                FROM sys.databases d
                {agJoins}
                LEFT JOIN (SELECT database_name, MAX(backup_finish_date) AS last_full_backup
                           FROM msdb.dbo.backupset WHERE type = 'D' GROUP BY database_name) fb ON fb.database_name = d.name
                WHERE d.database_id > 4
                ORDER BY d.name;
                """).ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    result.Databases.Add(new DatabaseResult
                    {
                        Name = reader.GetString(0),
                        InAvailabilityGroup = reader.GetBoolean(1),
                        AvailabilityGroup = reader.IsDBNull(2) ? null : reader.GetString(2),
                        State = reader.GetString(3),
                        RecoveryModel = reader.IsDBNull(4) ? null : reader.GetString(4),
                        CompatibilityLevel = reader.GetInt32(5),
                        SizeMb = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                        CreateDate = reader.GetDateTime(7),
                        LastFullBackup = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    });
                }
            }
            result.Topology = TopologyOf(result.Databases);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    /// <summary>
    /// From the user databases: "AG" when all are in an AG, "AG + Standalone" when some are, otherwise "Standalone"
    /// (also with no databases, or an AG that holds none of this replica's databases).
    /// </summary>
    public static string TopologyOf(IEnumerable<DatabaseResult> databases)
    {
        var list = databases.ToList();
        var inAg = list.Count(d => d.InAvailabilityGroup);
        return inAg == 0 ? "Standalone" : inAg == list.Count ? "AG" : "AG + Standalone";
    }

    private static SqlCommand Command(SqlConnection conn, string sql) => new(sql, conn);

    private static (int Major, int Minor) ParseVersion(string? version)
    {
        var parts = (version ?? "").Split('.');
        return (parts.Length > 0 && int.TryParse(parts[0], out var major) ? major : 0,
                parts.Length > 1 && int.TryParse(parts[1], out var minor) ? minor : 0);
    }

    /// <summary>Names as in the dashboard's SQL version list, e.g. "SQL Server 2022 (16.0)".</summary>
    private static string VersionName(int major, int minor) => (major, minor) switch
    {
        (17, _) => "SQL Server 2025 (17.0)",
        (16, _) => "SQL Server 2022 (16.0)",
        (15, _) => "SQL Server 2019 (15.0)",
        (14, _) => "SQL Server 2017 (14.0)",
        (13, _) => "SQL Server 2016 (13.0)",
        (12, _) => "SQL Server 2014 (12.0)",
        (11, _) => "SQL Server 2012 (11.0)",
        (10, 50) => "SQL Server 2008 R2 (10.50)",
        (10, _) => "SQL Server 2008 (10.0)",
        (9, _) => "SQL Server 2005 (9.0)",
        _ => $"SQL Server ({major}.{minor})",
    };

    /// <summary>"Enterprise Edition: Core-based Licensing (64-bit)" -> "Enterprise".</summary>
    private static string? ShortEdition(string? edition)
    {
        if (edition is null)
        {
            return null;
        }
        foreach (var name in new[] { "Enterprise", "Standard", "Developer", "Express", "Web", "Business Intelligence" })
        {
            if (edition.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        return edition;
    }
}
