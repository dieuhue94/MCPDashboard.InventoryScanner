using System.Collections.Concurrent;
using System.Text.Json;
using MCPDashboard.InventoryScanner;

// Scans a list of servers and records, for each one: OS, CPU, RAM and whether it is a SQL Server, an IIS server, both
// or neither. SQL Server hosts also get version, edition, AGs (sys.availability_groups) and their user databases,
// standalone or in an AG (sys.databases.replica_id); IIS hosts get their sites. Results go to a folder as
// inventory.json and CSV files and, with --update-dashboard, into the MCPDashboard database (see DashboardUpdater).
//
//   MCPDashboard.InventoryScanner [--input servers.txt] [--output <folder>] [--parallel 8] [--timeout 30]
//                                 [--update-dashboard] [--dry-run]
//   MCPDashboard.InventoryScanner --import <results folder or inventory.json> [--dry-run]
//
//   --input             server list, one per line (default: servers.txt next to the exe)
//   --output            results folder (default: results\<yyyyMMdd-HHmmss> next to the exe)
//   --parallel          servers scanned at the same time (default 8)
//   --timeout           seconds to wait when connecting to WMI or SQL Server (default 30)
//   --update-dashboard  after the scan, write the results to the dashboard database (appsettings.json)
//   --import            don't scan: write an earlier scan's inventory.json to the dashboard database
//   --dry-run           with --update-dashboard / --import: do the updates, report them, then roll back
//
// Run it as an account that is local admin on the servers (WMI and the \\host\c$ share for IIS), has
// VIEW SERVER STATE + VIEW ANY DATABASE (or sysadmin) on the SQL instances and, to update the dashboard,
// db_datareader + db_datawriter on the MCPDashboard database.

string? Arg(string name)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

var importPath = Arg("--import");
var updateDashboard = Flag("--update-dashboard") || importPath is not null;
var dryRun = Flag("--dry-run");

string? connectionString = null;
if (updateDashboard)
{
    var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    if (File.Exists(settingsPath))
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        if (settings.RootElement.TryGetProperty("ConnectionStrings", out var strings)
            && strings.TryGetProperty("Dashboard", out var dashboard))
        {
            connectionString = dashboard.GetString();
        }
    }
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.WriteLine($"ConnectionStrings:Dashboard is not set in {settingsPath}.");
        return 1;
    }
}

List<ServerResult> ordered;
string reportFolder;
if (importPath is not null)
{
    var jsonPath = Directory.Exists(importPath) ? Path.Combine(importPath, "inventory.json") : importPath;
    if (!File.Exists(jsonPath))
    {
        Console.WriteLine($"Scan results not found: {jsonPath}");
        return 1;
    }
    ordered = JsonSerializer.Deserialize<List<ServerResult>>(File.ReadAllText(jsonPath)) ?? [];
    Console.WriteLine($"Loaded {ordered.Count} server(s) from {jsonPath}.");
    reportFolder = Path.GetDirectoryName(Path.GetFullPath(jsonPath))!;
}
else
{
    var inputPath = Arg("--input") ?? Path.Combine(AppContext.BaseDirectory, "servers.txt");
    var outputFolder = Arg("--output") ?? Path.Combine(AppContext.BaseDirectory, "results", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    var parallel = int.TryParse(Arg("--parallel"), out var p) && p > 0 ? p : 8;
    var timeout = int.TryParse(Arg("--timeout"), out var t) && t > 0 ? t : 30;

    if (!File.Exists(inputPath))
    {
        Console.WriteLine($"Server list not found: {inputPath}");
        return 1;
    }

    var hosts = File.ReadAllLines(inputPath)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    Console.WriteLine($"Scanning {hosts.Count} server(s), {parallel} at a time.");

    var results = new ConcurrentBag<ServerResult>();
    var done = 0;
    var consoleLock = new object();
    await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (host, _) =>
    {
        var result = await ServerScanner.ScanAsync(host, timeout);
        results.Add(result);
        var n = Interlocked.Increment(ref done);

        var summary = result.Reachable
            ? $"{result.Role,-16} {result.OsName,-22} {result.CpuCores?.ToString() ?? "?",3} cores {result.MemoryGb?.ToString() ?? "?",4} GB"
              + (result.IsSqlServer ? $"  SQL: {result.SqlInstances.Count} instance(s), {result.SqlInstances.Sum(i => i.Databases.Count)} DB(s), {result.SqlInstances.Sum(i => i.Databases.Count(d => d.InAvailabilityGroup))} in AG" : "")
              + (result.IsIis ? $"  IIS: {result.IisSites.Count} site(s)" : "")
            : "UNREACHABLE";
        lock (consoleLock)
        {
            Console.ForegroundColor = !result.Reachable ? ConsoleColor.Red : result.Errors.Count > 0 ? ConsoleColor.Yellow : ConsoleColor.Gray;
            Console.WriteLine($"[{n,3}/{hosts.Count}] {result.Host,-32} {summary}");
            foreach (var error in result.Errors)
            {
                Console.WriteLine($"          {error}");
            }
            Console.ResetColor();
        }
    });

    ordered = results.OrderBy(r => r.Host, StringComparer.OrdinalIgnoreCase).ToList();
    ReportWriter.Write(outputFolder, ordered);
    reportFolder = outputFolder;

    Console.WriteLine();
    foreach (var role in ordered.GroupBy(r => r.Role).OrderBy(g => g.Key))
    {
        Console.WriteLine($"  {role.Key,-16} {role.Count()}");
    }
    Console.WriteLine($"Results written to {outputFolder}");
}

var dashboardFailures = 0;
if (updateDashboard)
{
    try
    {
        dashboardFailures = await DashboardUpdater.RunAsync(connectionString!, ordered, dryRun, reportFolder);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Dashboard update FAILED: {ex.Message}");
        return 3;
    }
}

// 0 = all good, 2 = some servers unreachable, 3 = some servers couldn't be written to the dashboard.
return dashboardFailures > 0 ? 3 : ordered.Any(r => !r.Reachable) ? 2 : 0;
