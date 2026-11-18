using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace MCPDashboard.InventoryScanner;

/// <summary>
/// Reads OS, CPU and memory over WMI (DCOM) and lists the SQL Server engine and IIS services.
/// Needs local admin (or WMI remote access) on the target for the account running the job.
/// </summary>
public static partial class WmiScanner
{
    public const string IisServiceName = "W3SVC";
    public const string DefaultSqlServiceName = "MSSQLSERVER";
    public const string NamedSqlServicePrefix = "MSSQL$";

    /// <summary>Fills the Windows details on <paramref name="result"/> and returns the SQL engine and IIS services found.</summary>
    public static List<ServiceInfo> Scan(ServerResult result, int timeoutSeconds)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var scope = new ManagementScope($@"\\{result.Host}\root\cimv2", new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            Timeout = timeout,
        });
        scope.Connect();

        foreach (var os in Query(scope, "SELECT Caption, Version FROM Win32_OperatingSystem", timeout))
        {
            result.OsCaption = (os["Caption"] as string)?.Trim();
            result.OsName = ShortOsName(result.OsCaption);
            result.OsBuild = os["Version"] as string;
        }

        var processors = Query(scope, "SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor", timeout);
        if (processors.Count > 0)
        {
            result.CpuSockets = processors.Count;
            result.CpuCores = processors.Sum(p => Convert.ToInt32(p["NumberOfCores"] ?? 0));
            result.LogicalProcessors = processors.Sum(p => Convert.ToInt32(p["NumberOfLogicalProcessors"] ?? 0));
        }

        foreach (var cs in Query(scope, "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", timeout))
        {
            // Windows reports a little less than what's installed, so round to the nearest GB.
            result.MemoryGb = (int)Math.Round(Convert.ToUInt64(cs["TotalPhysicalMemory"] ?? 0UL) / (1024d * 1024 * 1024));
        }

        return Query(scope,
                $"SELECT Name, State FROM Win32_Service WHERE Name = '{DefaultSqlServiceName}' OR Name LIKE 'MSSQL$%' OR Name = '{IisServiceName}'",
                timeout)
            .Select(s => new ServiceInfo((string)s["Name"], s["State"] as string ?? "Unknown"))
            .ToList();
    }

    /// <summary>
    /// The same SQL engine and IIS services, from the Service Control Manager over RPC instead of WMI: for servers
    /// where WMI is blocked or broken. Installed services are listed whether they are running, stopped or disabled.
    /// </summary>
    public static List<ServiceInfo> ListServicesViaScm(string host, int timeoutSeconds)
    {
        // GetServices has no timeout of its own.
        var task = Task.Run(() => ServiceController.GetServices(host));
        if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            throw new System.TimeoutException($"Service Control Manager didn't answer within {timeoutSeconds}s");
        }

        var services = new List<ServiceInfo>();
        foreach (var service in task.Result)
        {
            using (service)
            {
                var info = new ServiceInfo(service.ServiceName, service.Status switch
                {
                    ServiceControllerStatus.Running => "Running",
                    ServiceControllerStatus.Stopped => "Stopped",
                    var other => other.ToString(),
                });
                if (IsSqlEngine(info) || info.Name.Equals(IisServiceName, StringComparison.OrdinalIgnoreCase))
                {
                    services.Add(info);
                }
            }
        }
        return services;
    }

    public static bool IsSqlEngine(ServiceInfo service) =>
        service.Name.Equals(DefaultSqlServiceName, StringComparison.OrdinalIgnoreCase)
        || service.Name.StartsWith(NamedSqlServicePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Microsoft Windows Server 2019 Datacenter" -> "Windows Server 2019".</summary>
    public static string? ShortOsName(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return null;
        }
        var match = WindowsServerRegex().Match(caption);
        return match.Success ? match.Value : caption.Replace("Microsoft ", "").Trim();
    }

    private static List<ManagementBaseObject> Query(ManagementScope scope, string wql, TimeSpan timeout)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql),
            new System.Management.EnumerationOptions { Timeout = timeout, ReturnImmediately = true, Rewindable = false });
        return searcher.Get().Cast<ManagementBaseObject>().ToList();
    }

    [GeneratedRegex(@"Windows Server \d{4}( R2)?")]
    private static partial Regex WindowsServerRegex();
}
