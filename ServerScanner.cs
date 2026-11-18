using System.Net;
using System.Net.Sockets;

namespace MCPDashboard.InventoryScanner;

/// <summary>Scans one server: Windows details over WMI, then each SQL Server instance and the IIS sites.</summary>
public static class ServerScanner
{
    public static async Task<ServerResult> ScanAsync(string host, int timeoutSeconds)
    {
        var result = new ServerResult { Host = host, ScannedUtc = DateTime.UtcNow };

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            result.IpAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch (SocketException ex)
        {
            result.Errors.Add($"DNS: {ex.Message}");
            return result;
        }

        // Null when WMI couldn't be reached; SQL and IIS are then probed directly.
        List<ServiceInfo>? services = null;
        try
        {
            services = await Task.Run(() => WmiScanner.Scan(result, timeoutSeconds));
            result.Reachable = true;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"WMI: {ex.Message}");

            // Without WMI there are no OS / CPU / RAM, but the installed services still tell SQL Server and IIS apart,
            // stopped or not.
            try
            {
                services = await Task.Run(() => WmiScanner.ListServicesViaScm(host, timeoutSeconds));
                result.Reachable = true;
            }
            catch (Exception scmEx)
            {
                result.Errors.Add($"Services: {scmEx.Message}");
            }
        }

        await ScanSqlAsync(result, services, timeoutSeconds);
        await Task.Run(() => ScanIis(result, services, timeoutSeconds));

        result.Role = (result.IsSqlServer, result.IsIis) switch
        {
            (true, true) => "SQL Server + IIS",
            (true, false) => "SQL Server",
            (false, true) => "IIS",
            _ => result.Reachable ? "None" : "Unknown",
        };
        return result;
    }

    private static async Task ScanSqlAsync(ServerResult result, List<ServiceInfo>? services, int timeoutSeconds)
    {
        if (services is null)
        {
            // Without WMI the installed instances are unknown: try the default instance only.
            var probe = await SqlScanner.ScanAsync(result.Host, new ServiceInfo(WmiScanner.DefaultSqlServiceName, "Unknown"), timeoutSeconds);
            if (probe.Error is not null)
            {
                result.Errors.Add($"SQL probe of default instance: {probe.Error}");
                return;
            }
            result.Reachable = true;
            result.SqlInstances.Add(probe);
            result.IsSqlServer = true;
            FillHostFromSql(result, probe);
            return;
        }

        // Any installed engine service makes it a SQL Server, even stopped, disabled or refusing connections.
        foreach (var service in services.Where(WmiScanner.IsSqlEngine).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            result.IsSqlServer = true;
            var instance = await SqlScanner.ScanAsync(result.Host, service, timeoutSeconds);
            result.SqlInstances.Add(instance);
            if (instance.Error is not null)
            {
                result.Errors.Add($"SQL {instance.ConnectTo}: {instance.Error}");
            }
            else
            {
                FillHostFromSql(result, instance);
            }
        }
    }

    // Fills what WMI would have given (when it couldn't be reached) from what SQL Server sees of the host.
    private static void FillHostFromSql(ServerResult result, SqlInstanceResult instance)
    {
        result.LogicalProcessors ??= instance.HostLogicalProcessors;
        result.MemoryGb ??= instance.HostMemoryMb is int mb ? (int)Math.Round(mb / 1024d) : null;
        result.OsCaption ??= instance.HostOs;
        result.OsName ??= WmiScanner.ShortOsName(instance.HostOs);
    }

    private static void ScanIis(ServerResult result, List<ServiceInfo>? services, int timeoutSeconds)
    {
        var w3svc = services?.FirstOrDefault(s => s.Name.Equals(WmiScanner.IisServiceName, StringComparison.OrdinalIgnoreCase));
        if (services is not null && w3svc is null)
        {
            return;
        }
        result.IisServiceState = w3svc?.State;

        try
        {
            var sites = IisScanner.ReadSites(result.Host);
            if (sites is null)
            {
                if (w3svc is not null)
                {
                    result.IsIis = true;
                    result.Errors.Add("IIS: W3SVC is installed but applicationHost.config couldn't be found");
                }
                return;
            }
            result.Reachable = true;
            result.IsIis = true;
            result.IisSites = sites;
        }
        catch (Exception ex)
        {
            result.IsIis = w3svc is not null;
            result.Errors.Add($"IIS: {ex.Message}");
            return;
        }

        ReadSiteStates(result, w3svc, timeoutSeconds);
    }

    // Kept apart from the "IIS:" errors above: a missing state doesn't stop the site list from being saved.
    private static void ReadSiteStates(ServerResult result, ServiceInfo? w3svc, int timeoutSeconds)
    {
        if (result.IisSites.Count == 0)
        {
            return;
        }

        // With the web service itself stopped, no site is serving, whatever each one is set to.
        if (w3svc is not null && w3svc.State != "Running")
        {
            result.IisSites.ForEach(s => s.State = "Stopped");
            return;
        }

        Dictionary<string, string> states;
        try
        {
            states = IisScanner.ReadStates(result.Host, timeoutSeconds);
        }
        catch (Exception wmiEx)
        {
            // The WMI provider needs the IIS Management Scripts and Tools feature; PowerShell remoting doesn't.
            try
            {
                states = IisScanner.ReadStatesRemoting(result.Host, timeoutSeconds);
            }
            catch (Exception psEx)
            {
                result.Errors.Add($"IIS state: WMI: {wmiEx.Message}; PowerShell remoting: {psEx.Message}");
                return;
            }
        }

        foreach (var site in result.IisSites)
        {
            site.State = states.GetValueOrDefault(site.Name);
        }
    }
}
