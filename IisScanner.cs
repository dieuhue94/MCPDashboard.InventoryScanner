using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MCPDashboard.InventoryScanner;

/// <summary>
/// Reads the sites and their app pools from IIS's applicationHost.config over the admin share (\\host\c$), so it
/// needs no WinRM or IIS tools; the account running the job needs local admin on the target.
/// </summary>
public static class IisScanner
{
    /// <summary>The sites on <paramref name="host"/>, or null when there is no IIS config there.</summary>
    public static List<IisSiteResult>? ReadSites(string host)
    {
        var path = $@"\\{host}\c$\Windows\System32\inetsrv\config\applicationHost.config";
        if (!File.Exists(path))
        {
            return null;
        }

        var appHost = XDocument.Load(path).Root?.Element("system.applicationHost");
        var sitesElement = appHost?.Element("sites");
        if (sitesElement is null)
        {
            return [];
        }

        // Pool settings left out of an <add> fall back to <applicationPoolDefaults>, then to IIS's own defaults.
        var poolsElement = appHost!.Element("applicationPools");
        var poolDefaults = poolsElement?.Element("applicationPoolDefaults");
        var pools = (poolsElement?.Elements("add") ?? [])
            .Where(p => p.Attribute("name") is not null)
            .GroupBy(p => (string)p.Attribute("name")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var defaultPool = (string?)sitesElement.Element("applicationDefaults")?.Attribute("applicationPool") ?? "DefaultAppPool";

        var sites = new List<IisSiteResult>();
        foreach (var site in sitesElement.Elements("site"))
        {
            var applications = site.Elements("application").ToList();
            var root = applications.FirstOrDefault(a => (string?)a.Attribute("path") == "/");
            var poolName = (string?)root?.Attribute("applicationPool") ?? defaultPool;
            pools.TryGetValue(poolName, out var pool);

            sites.Add(new IisSiteResult
            {
                Id = (long?)site.Attribute("id"),
                Name = (string?)site.Attribute("name") ?? "",
                Bindings = string.Join("; ", site.Element("bindings")?.Elements("binding")
                    .Select(b => $"{(string?)b.Attribute("protocol")} {(string?)b.Attribute("bindingInformation")}") ?? []),
                PhysicalPath = (string?)root?.Elements("virtualDirectory").FirstOrDefault(v => (string?)v.Attribute("path") == "/")?.Attribute("physicalPath"),
                AppPool = poolName,
                RuntimeVersion = PoolSetting(pool, poolDefaults, "managedRuntimeVersion") ?? "v4.0",
                PipelineMode = PoolSetting(pool, poolDefaults, "managedPipelineMode") ?? "Integrated",
                Identity = PoolIdentity(pool, poolDefaults),
                AutoStart = !string.Equals((string?)site.Attribute("serverAutoStart"), "false", StringComparison.OrdinalIgnoreCase),
                Applications = applications.Count,
            });
        }
        return sites;
    }

    /// <summary>
    /// Site name -> "Started", "Stopped", ... from IIS itself over WMI (root\WebAdministration). That namespace comes
    /// with the "IIS Management Scripts and Tools" feature (Web-Scripting-Tools); without it this throws.
    /// </summary>
    public static Dictionary<string, string> ReadStates(string host, int timeoutSeconds)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var scope = new ManagementScope($@"\\{host}\root\WebAdministration", new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,   // required by this namespace
            Timeout = timeout,
        });
        scope.Connect();

        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name FROM Site"),
            new System.Management.EnumerationOptions { Timeout = timeout });
        foreach (ManagementObject site in searcher.Get())
        {
            using (site)
            {
                var result = site.InvokeMethod("GetState", null, null);
                states[(string)site["Name"]] = Convert.ToInt32(result?["ReturnValue"] ?? 4) switch
                {
                    0 => "Starting",
                    1 => "Started",
                    2 => "Stopping",
                    3 => "Stopped",
                    _ => "Unknown",
                };
            }
        }
        return states;
    }

    // Get-Website comes with the same feature as the WMI provider, so fall back to appcmd, which every IIS has.
    private const string RemotingScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-Command -ComputerName '{host}' -ScriptBlock {
                if (Get-Module -ListAvailable -Name WebAdministration) {
                    Import-Module WebAdministration
                    Get-Website | ForEach-Object { [pscustomobject]@{ Name = $_.Name; State = [string]$_.State } }
                } else {
                    [xml]$xml = & "$env:windir\system32\inetsrv\appcmd.exe" list site /xml
                    $xml.appcmd.SITE | ForEach-Object { [pscustomobject]@{ Name = $_.'SITE.NAME'; State = $_.state } }
                }
            } | Select-Object Name, State | ConvertTo-Json -Compress
        } catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;

    /// <summary>
    /// Site name -> state through PowerShell remoting (WinRM): Get-Website on the server, or appcmd where the
    /// WebAdministration module isn't installed. Used when the WMI provider in <see cref="ReadStates"/> is missing.
    /// </summary>
    public static Dictionary<string, string> ReadStatesRemoting(string host, int timeoutSeconds)
    {
        if (!Regex.IsMatch(host, "^[A-Za-z0-9.-]+$"))
        {
            throw new ArgumentException($"Unexpected host name '{host}'");
        }

        var script = RemotingScript.Replace("{host}", host);
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // Connecting over WinRM plus loading the module takes a few seconds on its own.
        if (!process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds * 2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"PowerShell remoting didn't answer within {timeoutSeconds * 2}s");
        }
        if (process.ExitCode != 0)
        {
            var message = stderr.Result.Trim();
            throw new InvalidOperationException(message.Length > 0 ? message : $"powershell.exe exited with {process.ExitCode}");
        }

        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var json = stdout.Result.Trim();
        if (json.Length == 0)
        {
            return states;
        }

        // ConvertTo-Json writes a lone object, not an array, when there is one site.
        using var document = JsonDocument.Parse(json);
        var sites = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToList()
            : [document.RootElement];
        foreach (var site in sites)
        {
            var name = site.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var state = site.TryGetProperty("State", out var s) ? s.GetString() : null;
            if (!string.IsNullOrEmpty(name))
            {
                states[name] = string.IsNullOrEmpty(state) ? "Unknown" : state;
            }
        }
        return states;
    }

    private static string? PoolSetting(XElement? pool, XElement? defaults, string attribute) =>
        (string?)pool?.Attribute(attribute) ?? (string?)defaults?.Attribute(attribute);

    private static string PoolIdentity(XElement? pool, XElement? defaults)
    {
        var model = pool?.Element("processModel");
        var defaultModel = defaults?.Element("processModel");
        var type = (string?)model?.Attribute("identityType") ?? (string?)defaultModel?.Attribute("identityType") ?? "ApplicationPoolIdentity";
        return type == "SpecificUser"
            ? (string?)model?.Attribute("userName") ?? (string?)defaultModel?.Attribute("userName") ?? type
            : type;
    }
}
