namespace MCPDashboard.InventoryScanner;

public class ServerResult
{
    public string Host { get; set; } = string.Empty;
    public string? IpAddress { get; set; }

    /// <summary>True once WMI, SQL or the IIS config answered.</summary>
    public bool Reachable { get; set; }

    /// <summary>SQL Server, IIS, SQL Server + IIS, None, or Unknown when the server couldn't be reached.</summary>
    public string Role { get; set; } = "Unknown";

    public bool IsSqlServer { get; set; }
    public bool IsIis { get; set; }

    /// <summary>Full caption, e.g. "Microsoft Windows Server 2022 Standard".</summary>
    public string? OsCaption { get; set; }

    /// <summary>Short name matching the dashboard's OS list, e.g. "Windows Server 2022".</summary>
    public string? OsName { get; set; }

    public string? OsBuild { get; set; }
    public int? CpuSockets { get; set; }
    public int? CpuCores { get; set; }
    public int? LogicalProcessors { get; set; }
    public int? MemoryGb { get; set; }

    /// <summary>State of the W3SVC (IIS) service, when installed.</summary>
    public string? IisServiceState { get; set; }

    public List<SqlInstanceResult> SqlInstances { get; set; } = [];
    public List<IisSiteResult> IisSites { get; set; } = [];

    /// <summary>Anything that failed along the way; a server can be partly scanned.</summary>
    public List<string> Errors { get; set; } = [];

    public DateTime ScannedUtc { get; set; }
}

public class SqlInstanceResult
{
    /// <summary>MSSQLSERVER for the default instance, otherwise the instance name.</summary>
    public string Instance { get; set; } = string.Empty;
    public string ConnectTo { get; set; } = string.Empty;
    public string? ServiceState { get; set; }

    /// <summary>e.g. 16.0.4135.4</summary>
    public string? ProductVersion { get; set; }

    /// <summary>Name matching the dashboard's SQL version list, e.g. "SQL Server 2022 (16.0)".</summary>
    public string? VersionName { get; set; }

    public string? ProductLevel { get; set; }

    /// <summary>Standard, Enterprise, Developer, Express, ...</summary>
    public string? Edition { get; set; }

    /// <summary>SERVERPROPERTY('Edition') as returned, e.g. "Enterprise Edition: Core-based Licensing (64-bit)".</summary>
    public string? EditionFull { get; set; }

    public bool HadrEnabled { get; set; }

    /// <summary>AG when the instance hosts at least one availability group, otherwise Standalone.</summary>
    public string? Topology { get; set; }

    public List<AvailabilityGroupResult> AvailabilityGroups { get; set; } = [];
    public List<DatabaseResult> Databases { get; set; } = [];
    public string? Error { get; set; }

    // Host details as SQL Server sees them; used when WMI can't reach the server.
    internal int? HostLogicalProcessors { get; set; }
    internal int? HostMemoryMb { get; set; }
    internal string? HostOs { get; set; }
}

public class AvailabilityGroupResult
{
    public string Name { get; set; } = string.Empty;

    /// <summary>This replica's role: PRIMARY or SECONDARY.</summary>
    public string? LocalRole { get; set; }

    public string? Listener { get; set; }
}

public class DatabaseResult
{
    public string Name { get; set; } = string.Empty;

    /// <summary>sys.databases.replica_id is set: the database is in an AG.</summary>
    public bool InAvailabilityGroup { get; set; }

    public string? AvailabilityGroup { get; set; }
    public string? State { get; set; }
    public string? RecoveryModel { get; set; }
    public int? CompatibilityLevel { get; set; }
    public decimal? SizeMb { get; set; }
    public DateTime? CreateDate { get; set; }

    /// <summary>Latest full backup in this instance's msdb, in the SQL Server's local time.</summary>
    public DateTime? LastFullBackup { get; set; }
}

public class IisSiteResult
{
    public long? Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Started, Stopped, Starting, Stopping or Unknown; null when it couldn't be read.</summary>
    public string? State { get; set; }

    /// <summary>protocol bindingInformation pairs, e.g. "https *:443:portal.calegis.net".</summary>
    public string Bindings { get; set; } = string.Empty;

    public string? PhysicalPath { get; set; }
    public string? AppPool { get; set; }
    public string? RuntimeVersion { get; set; }
    public string? PipelineMode { get; set; }
    public string? Identity { get; set; }
    public bool AutoStart { get; set; }
    public int Applications { get; set; }
}

public record ServiceInfo(string Name, string State);
