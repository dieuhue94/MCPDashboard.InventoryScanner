using System.Data;
using Microsoft.Data.SqlClient;

namespace MCPDashboard.InventoryScanner;

/// <summary>
/// Writes a scan into the MCPDashboard database (schema: MCPDashboard\Database\01_CreateDatabase.sql), one transaction
/// per server, so one bad server doesn't stop the rest.
///
/// Updated from the scan: IP, OS (+ build), CPU cores, RAM, SQL version (+ build) / edition / topology, the SQL Server
/// and IIS roles, AGs, databases (AG, size, last full backup) and IIS sites (name, state), and Server.LastScannedUtc,
/// which tells the app these are the job's and shows them read-only. Never touched: environment, status, applications,
/// contacts, notes and tags.
///
///   * Servers are matched on the short name (asmsqlp01.calegis.net -> ASMSQLP01) among servers that aren't Retired.
///     A server not in the dashboard yet is added with environment "NonProduction" and application "Unassigned".
///   * Servers that couldn't be reached are left as they are, or added with just their name (and IP when DNS knew
///     it) if they aren't in the dashboard yet.
///   * Servers are never deleted. Each run reports, in the log, discrepancies.csv and dbo.InventoryDiscrepancy (the
///     dashboard's Discrepancies page): servers it added (New), Active
///     servers not in the list (Not in list), Retired servers still in the list, which aren't added again (Retired but
///     in list), and list servers not scanned successfully for 7 days (Not responding).
///   * Databases are matched on server + instance + name. New ones get application "Unassigned". A database recorded
///     in the dashboard but no longer on its instance is removed, only when that instance answered.
///   * AGs and IIS sites no longer on the server are removed (AGs only when every SQL instance on it answered).
///   * An OS or SQL version name missing from the Admin lists is added to them.
/// </summary>
public sealed class DashboardUpdater(SqlConnection db, bool dryRun)
{
    private const int ActiveStatusId = 1;
    private const int RetiredStatusId = 3;
    private const int SqlServerRoleId = 1;
    private const int IisRoleId = 2;
    private const int StandaloneTopologyId = 1;
    private const int AgTopologyId = 2;
    private const int MixedTopologyId = 3;

    private SqlTransaction tx = default!;
    private Dictionary<string, int> operatingSystems = [];
    private Dictionary<string, int> sqlVersions = [];
    private Dictionary<string, int> editions = [];

    /// <summary>A server in the list hasn't been scanned successfully for this long: reported as not responding.</summary>
    private static readonly TimeSpan NotRespondingAfter = TimeSpan.FromDays(7);

    /// <summary>What a person should look at after this run: Category, Server, Detail.</summary>
    private readonly List<(string Category, string Server, string Detail)> discrepancies = [];

    /// <summary>
    /// Returns the number of servers that failed to update. The discrepancy report goes to the log and, when
    /// <paramref name="reportFolder"/> is given, to discrepancies.csv in it.
    /// </summary>
    public static async Task<int> RunAsync(string connectionString, IReadOnlyList<ServerResult> servers, bool dryRun, string? reportFolder)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync();

        await using (var check = new SqlCommand("SELECT COL_LENGTH(N'dbo.Server', N'LastScannedUtc');", db))
        {
            if (await check.ExecuteScalarAsync() is DBNull or null)
            {
                throw new InvalidOperationException("dbo.Server has no LastScannedUtc column: run Database\\08_Upgrade_ScanColumns.sql first.");
            }
        }
        await using (var check = new SqlCommand($"SELECT COUNT(*) FROM dbo.SqlTopology WHERE SqlTopologyId = {MixedTopologyId};", db))
        {
            if ((int)(await check.ExecuteScalarAsync())! == 0)
            {
                throw new InvalidOperationException("dbo.SqlTopology has no \"AG + Standalone\" row: run Database\\14_Upgrade_MixedTopology.sql first.");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Updating the dashboard database ({db.DataSource}/{db.Database}){(dryRun ? " - DRY RUN, changes are rolled back" : "")}.");
        var updater = new DashboardUpdater(db, dryRun);
        var failed = await updater.UpdateAsync(servers);
        await updater.FindDiscrepanciesAsync(servers);
        updater.ReportDiscrepancies(reportFolder);
        await updater.SaveDiscrepanciesAsync(servers.Count);
        return failed;
    }

    /// <summary>
    /// Records the findings in dbo.InventoryDiscrepancy for the dashboard's Discrepancies page: one open row per
    /// category + server. Still found: LastSeenUtc and Detail are updated. No longer found: resolved, except "New",
    /// which a person closes by marking it reviewed.
    /// </summary>
    private async Task SaveDiscrepanciesAsync(int listedCount)
    {
        await using (var check = new SqlCommand("SELECT OBJECT_ID(N'dbo.InventoryDiscrepancy', N'U');", db))
        {
            if (await check.ExecuteScalarAsync() is DBNull or null)
            {
                Console.WriteLine("Discrepancies not saved to the dashboard: run Database\\15_Upgrade_Discrepancies.sql to add the table.");
                return;
            }
        }
        if (dryRun)
        {
            Console.WriteLine("Discrepancies not saved to the dashboard (dry run).");
            return;
        }

        tx = (SqlTransaction)await db.BeginTransactionAsync();
        try
        {
            int added = 0, resolved = 0;
            foreach (var (category, server, detail) in discrepancies)
            {
                await using var cmd = Command("""
                    UPDATE dbo.InventoryDiscrepancy SET LastSeenUtc = SYSUTCDATETIME(), Detail = @detail
                    WHERE Category = @category AND ServerName = @server AND ResolvedUtc IS NULL;
                    IF @@ROWCOUNT > 0
                        SELECT 0;
                    ELSE
                    BEGIN
                        INSERT dbo.InventoryDiscrepancy (Category, ServerName, Detail) VALUES (@category, @server, @detail);
                        SELECT 1;
                    END;
                    """,
                    ("@category", SqlDbType.NVarChar, category), ("@server", SqlDbType.NVarChar, server),
                    ("@detail", SqlDbType.NVarChar, Truncate(detail, 1000)));
                added += (int)(await cmd.ExecuteScalarAsync())!;
            }

            // An empty list would make every server "Not in list" and resolve nothing useful: only resolve after a real run.
            if (listedCount > 0)
            {
                var found = discrepancies.Select(d => $"{d.Category}|{d.Server}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                var open = new List<(int Id, string Key)>();
                await using (var cmd = Command("SELECT DiscrepancyId, Category + N'|' + ServerName FROM dbo.InventoryDiscrepancy WHERE ResolvedUtc IS NULL AND Category <> N'New';"))
                await using (var r = await cmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        open.Add((r.GetInt32(0), r.GetString(1)));
                    }
                }
                foreach (var (id, _) in open.Where(o => !found.Contains(o.Key)))
                {
                    await using var cmd = Command("UPDATE dbo.InventoryDiscrepancy SET ResolvedUtc = SYSUTCDATETIME(), Resolution = N'No longer found by the inventory job' WHERE DiscrepancyId = @id;",
                        ("@id", SqlDbType.Int, id));
                    await cmd.ExecuteNonQueryAsync();
                    resolved++;
                }
            }

            await tx.CommitAsync();
            Console.WriteLine($"Discrepancies saved to the dashboard: {added} new, {resolved} resolved.");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            Console.WriteLine($"Discrepancies not saved to the dashboard: {ex.Message}");
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    /// <summary>
    /// Servers are never deleted: Active servers missing from the list and list servers that stopped answering are
    /// only reported, for someone to check and, if it's gone, mark Retired in the dashboard.
    /// </summary>
    private async Task FindDiscrepanciesAsync(IReadOnlyList<ServerResult> servers)
    {
        var listed = servers.ToDictionary(s => ShortName(s.Host), StringComparer.OrdinalIgnoreCase);
        if (listed.Count == 0)
        {
            return;
        }

        await using var cmd = new SqlCommand("SELECT Name, LastScannedUtc FROM dbo.Server WHERE ServerStatusId <> @retired ORDER BY Name;", db);
        cmd.Parameters.Add("@retired", SqlDbType.Int).Value = RetiredStatusId;
        await using var r = await cmd.ExecuteReaderAsync();
        var cutoff = DateTime.UtcNow - NotRespondingAfter;
        while (await r.ReadAsync())
        {
            var name = r.GetString(0);
            DateTime? lastScanned = r.IsDBNull(1) ? null : r.GetDateTime(1);
            var seen = lastScanned is DateTime d ? $"last scanned {d:yyyy-MM-dd} UTC" : "never scanned";
            if (!listed.TryGetValue(name, out var scan))
            {
                discrepancies.Add(("Not in list", name, $"Active in the dashboard but not in the server list ({seen}): retire it if it's gone"));
            }
            else if (!scan.Reachable && (lastScanned is null || lastScanned < cutoff))
            {
                discrepancies.Add(("Not responding", name, $"In the list but not reachable ({seen}): {string.Join(" | ", scan.Errors)}"));
            }
        }
    }

    private void ReportDiscrepancies(string? reportFolder)
    {
        var rows = discrepancies
            .OrderBy(d => d.Category switch { "New" => 0, "Not in list" => 1, "Retired but in list" => 2, _ => 3 })
            .ThenBy(d => d.Server, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine();
        Console.WriteLine(rows.Count == 0 ? "Discrepancies: none." : $"Discrepancies to review: {rows.Count}");
        foreach (var group in rows.GroupBy(d => d.Category))
        {
            Console.WriteLine($"  {group.Key} ({group.Count()}):");
            foreach (var (_, server, detail) in group)
            {
                Console.WriteLine($"    {server}: {detail}");
            }
        }

        if (reportFolder is not null)
        {
            Directory.CreateDirectory(reportFolder);
            ReportWriter.WriteCsv(Path.Combine(reportFolder, "discrepancies.csv"), ["Category", "Server", "Detail"],
                rows.Select(d => new object?[] { d.Category, d.Server, d.Detail }));
        }
    }

    private async Task<bool> IsRetiredAsync(string name)
    {
        await using var cmd = Command("SELECT COUNT(*) FROM dbo.Server WHERE Name = @name AND ServerStatusId = @retired;",
            ("@name", SqlDbType.NVarChar, name), ("@retired", SqlDbType.Int, RetiredStatusId));
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private async Task<int> UpdateAsync(IReadOnlyList<ServerResult> servers)
    {
        var agBackups = LatestAgBackups(servers);
        int updated = 0, skipped = 0, failed = 0;

        foreach (var server in servers.OrderBy(s => s.Host, StringComparer.OrdinalIgnoreCase))
        {
            var name = ShortName(server.Host);
            tx = (SqlTransaction)await db.BeginTransactionAsync();
            try
            {
                // A Retired server back in the list isn't added again as a second server: someone decides.
                if (await ReadServerAsync(name) is null && await IsRetiredAsync(name))
                {
                    await tx.RollbackAsync();
                    skipped++;
                    discrepancies.Add(("Retired but in list", name, "Retired in the dashboard but still in the server list: clean up AD, or set it back to Active"));
                    Console.WriteLine($"  - {name}: Retired in the dashboard, left as is");
                    continue;
                }

                List<string> changes;
                if (server.Reachable)
                {
                    changes = await UpdateServerAsync(name, server, agBackups);
                }
                else if (await AddUnreachableServerAsync(name, server))
                {
                    changes = ["added, but it couldn't be reached: details are left to fill in by hand"];
                }
                else
                {
                    await tx.RollbackAsync();
                    skipped++;
                    Console.WriteLine($"  - {name}: not reachable in the scan, left as is");
                    continue;
                }
                if (dryRun)
                {
                    await tx.RollbackAsync();
                }
                else
                {
                    await tx.CommitAsync();
                }
                updated++;
                if (changes.Count > 0 && changes[0].StartsWith("added", StringComparison.Ordinal))
                {
                    discrepancies.Add(("New", name, "Added by the scan as NonProduction / Unassigned: check environment, applications and contacts"));
                }
                Console.WriteLine(changes.Count == 0 ? $"  = {name}: no changes" : $"  ~ {name}: {string.Join("; ", changes)}");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ! {name}: FAILED, scan details not saved: {ex.Message}");
                Console.ResetColor();
                await AddNameOnlyAsync(name);
            }
            finally
            {
                await tx.DisposeAsync();
            }
        }

        Console.WriteLine($"Dashboard: {updated} server(s) updated or added, {skipped} skipped (unreachable, already there), {failed} failed{(dryRun ? " (dry run: rolled back)" : "")}.");
        return failed;
    }

    private async Task<List<string>> UpdateServerAsync(string name, ServerResult scan, Dictionary<string, DateTime> agBackups)
    {
        // Lookups are read inside the transaction: a value added for an earlier server may have been rolled back.
        operatingSystems = await ReadLookupAsync("SELECT Name, OperatingSystemId FROM dbo.OperatingSystem;");
        sqlVersions = await ReadLookupAsync("SELECT Name, SqlVersionId FROM dbo.SqlVersion;");
        editions = await ReadLookupAsync("SELECT Name, ServerEditionId FROM dbo.ServerEdition;");

        var changes = new List<string>();
        var row = await ReadServerAsync(name);
        if (row is null)
        {
            await AddServerAsync(name, scan);
            changes.Add("added (environment NonProduction, application Unassigned)");
            row = (await ReadServerAsync(name))!;
        }

        await UpdateServerRowAsync(row, scan, changes);
        await SyncRolesAsync(row.ServerId, scan, changes);
        if (scan.IsSqlServer)
        {
            var agIds = await SyncAvailabilityGroupsAsync(row.ServerId, scan, changes);
            await SyncDatabasesAsync(name, row.ServerId, scan, agIds, agBackups, changes);
        }
        if (scan.IsIis && !scan.Errors.Any(e => e.StartsWith("IIS:", StringComparison.Ordinal)))
        {
            await SyncIisSitesAsync(row.ServerId, scan, changes);
        }
        return changes;
    }

    /* ---------- Server ---------- */

    private sealed record ServerRow(int ServerId, string? IpAddress, int OperatingSystemId, int? ServerEditionId, int? SqlVersionId,
                                    int? SqlTopologyId, int CpuCores, int MemoryGb, string? OsBuild, string? SqlBuild);

    private async Task<ServerRow?> ReadServerAsync(string name)
    {
        await using var cmd = Command("""
            SELECT ServerId, IpAddress, OperatingSystemId, ServerEditionId, SqlVersionId, SqlTopologyId, CpuCores, MemoryGb, OsBuild, SqlBuild
            FROM dbo.Server
            WHERE Name = @name AND ServerStatusId <> @retired;
            """, ("@name", SqlDbType.NVarChar, name), ("@retired", SqlDbType.Int, RetiredStatusId));
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
        {
            return null;
        }
        return new ServerRow(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2), NullableInt(r, 3), NullableInt(r, 4),
                             NullableInt(r, 5), r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8),
                             r.IsDBNull(9) ? null : r.GetString(9));
    }

    /// <summary>After a failed update, makes sure the server is at least listed (name only, so nothing can clash).</summary>
    private async Task AddNameOnlyAsync(string name)
    {
        await tx.DisposeAsync();
        tx = (SqlTransaction)await db.BeginTransactionAsync();
        try
        {
            if (await ReadServerAsync(name) is not null)
            {
                await tx.RollbackAsync();
                return;
            }
            await AddServerAsync(name, new ServerResult { Host = name });
            if (dryRun)
            {
                await tx.RollbackAsync();
            }
            else
            {
                await tx.CommitAsync();
            }
            Console.WriteLine($"      {name}: added with its name only");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            Console.WriteLine($"      {name}: couldn't be added either: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds a server the scan couldn't reach when it isn't in the dashboard yet, so every server in the list shows up.
    /// It gets no LastScannedUtc, so its details stay editable by hand. Returns false when it's already there.
    /// </summary>
    private async Task<bool> AddUnreachableServerAsync(string name, ServerResult scan)
    {
        operatingSystems = await ReadLookupAsync("SELECT Name, OperatingSystemId FROM dbo.OperatingSystem;");
        if (await ReadServerAsync(name) is not null)
        {
            return false;
        }
        await AddServerAsync(name, scan);
        if (scan.IpAddress is not null)
        {
            await Command("UPDATE dbo.Server SET IpAddress = @ip WHERE Name = @name AND ServerStatusId <> @retired;",
                ("@ip", SqlDbType.VarChar, scan.IpAddress), ("@name", SqlDbType.NVarChar, name),
                ("@retired", SqlDbType.Int, RetiredStatusId)).ExecuteNonQueryAsync();
        }
        return true;
    }

    private async Task AddServerAsync(string name, ServerResult scan)
    {
        var environmentId = await LookupIdAsync("Environment", "EnvironmentId", "NonProduction", null);
        var applicationId = await UnassignedApplicationIdAsync();
        var osId = await LookupIdAsync("OperatingSystem", "OperatingSystemId", scan.OsName ?? "Unknown", operatingSystems);

        await Command("""
            DECLARE @id int;
            INSERT dbo.Server (Name, EnvironmentId, ServerStatusId, OperatingSystemId, CpuCores, MemoryGb)
            VALUES (@name, @env, @status, @os, 0, 0);
            SET @id = SCOPE_IDENTITY();
            INSERT dbo.ServerApplication (ServerId, ApplicationId) VALUES (@id, @app);
            """,
            ("@name", SqlDbType.NVarChar, name), ("@env", SqlDbType.Int, environmentId), ("@status", SqlDbType.Int, ActiveStatusId),
            ("@os", SqlDbType.Int, osId), ("@app", SqlDbType.Int, applicationId)).ExecuteNonQueryAsync();
    }

    private async Task UpdateServerRowAsync(ServerRow row, ServerResult scan, List<string> changes)
    {
        var ip = scan.IpAddress ?? row.IpAddress;
        var osId = scan.OsName is null ? row.OperatingSystemId : await LookupIdAsync("OperatingSystem", "OperatingSystemId", scan.OsName, operatingSystems);
        var cpu = scan.CpuCores ?? scan.LogicalProcessors ?? row.CpuCores;
        var memory = scan.MemoryGb ?? row.MemoryGb;

        var osBuild = scan.OsBuild ?? row.OsBuild;

        int? versionId, editionId, topologyId;
        string? sqlBuild;
        var answered = scan.SqlInstances.Where(i => i.Error is null).ToList();
        if (!scan.IsSqlServer)
        {
            (versionId, editionId, topologyId, sqlBuild) = (null, null, null, null);
        }
        else if (answered.Count == 0)
        {
            (versionId, editionId, topologyId, sqlBuild) = (row.SqlVersionId, row.ServerEditionId, row.SqlTopologyId, row.SqlBuild);
        }
        else
        {
            // The server row holds one version / edition: the default instance's, or the first named one.
            var main = answered.FirstOrDefault(i => i.Instance == WmiScanner.DefaultSqlServiceName) ?? answered[0];
            versionId = main.VersionName is null ? row.SqlVersionId : await LookupIdAsync("SqlVersion", "SqlVersionId", main.VersionName, sqlVersions);
            editionId = main.Edition is not null && editions.TryGetValue(main.Edition, out var e) ? e : row.ServerEditionId;
            topologyId = SqlScanner.TopologyOf(answered.SelectMany(i => i.Databases)) switch
            {
                "AG" => AgTopologyId,
                "AG + Standalone" => MixedTopologyId,
                _ => StandaloneTopologyId,
            };
            sqlBuild = main.ProductVersion is null ? row.SqlBuild : $"{main.ProductVersion} {main.ProductLevel}".Trim();
        }

        void Note(string field, object? from, object? to)
        {
            if (!Equals(from, to))
            {
                changes.Add($"{field} {from ?? "-"} -> {to ?? "-"}");
            }
        }
        var before = changes.Count;
        Note("IP", row.IpAddress, ip);
        Note("OS", NameOf(operatingSystems, row.OperatingSystemId), NameOf(operatingSystems, osId));
        Note("CPU", row.CpuCores, cpu);
        Note("RAM GB", row.MemoryGb, memory);
        Note("SQL", NameOf(sqlVersions, row.SqlVersionId), NameOf(sqlVersions, versionId));
        Note("edition", NameOf(editions, row.ServerEditionId), NameOf(editions, editionId));
        Note("topology", TopologyName(row.SqlTopologyId), TopologyName(topologyId));
        Note("OS build", row.OsBuild, osBuild);
        Note("SQL build", row.SqlBuild, sqlBuild);

        // Always stamp LastScannedUtc (it tells the app the job owns these fields); ModifiedUtc only on a real change.
        await Command("""
            UPDATE dbo.Server
            SET IpAddress = @ip, OperatingSystemId = @os, CpuCores = @cpu, MemoryGb = @mem,
                SqlVersionId = @version, ServerEditionId = @edition, SqlTopologyId = @topology,
                OsBuild = @osBuild, SqlBuild = @sqlBuild, LastScannedUtc = @scanned,
                ModifiedUtc = CASE WHEN @changed = 1 THEN SYSUTCDATETIME() ELSE ModifiedUtc END
            WHERE ServerId = @id;
            """,
            ("@ip", SqlDbType.VarChar, ip), ("@os", SqlDbType.Int, osId), ("@cpu", SqlDbType.Int, cpu), ("@mem", SqlDbType.Int, memory),
            ("@version", SqlDbType.Int, versionId), ("@edition", SqlDbType.Int, editionId), ("@topology", SqlDbType.Int, topologyId),
            ("@osBuild", SqlDbType.VarChar, osBuild), ("@sqlBuild", SqlDbType.VarChar, sqlBuild),
            ("@scanned", SqlDbType.DateTime2, WholeSeconds(scan.ScannedUtc)), ("@changed", SqlDbType.Bit, changes.Count > before),
            ("@id", SqlDbType.Int, row.ServerId)).ExecuteNonQueryAsync();
    }

    private async Task SyncRolesAsync(int serverId, ServerResult scan, List<string> changes)
    {
        var wanted = new HashSet<int>();
        if (scan.IsSqlServer) wanted.Add(SqlServerRoleId);
        if (scan.IsIis) wanted.Add(IisRoleId);

        var current = new HashSet<int>();
        await using (var cmd = Command("SELECT ServerRoleId FROM dbo.ServerRoleAssignment WHERE ServerId = @id;", ("@id", SqlDbType.Int, serverId)))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) current.Add(r.GetInt32(0));
        }

        foreach (var role in wanted.Except(current))
        {
            await Command("INSERT dbo.ServerRoleAssignment (ServerId, ServerRoleId) VALUES (@id, @role);",
                ("@id", SqlDbType.Int, serverId), ("@role", SqlDbType.Int, role)).ExecuteNonQueryAsync();
            changes.Add($"+role {RoleName(role)}");
        }
        foreach (var role in current.Except(wanted))
        {
            await Command("DELETE dbo.ServerRoleAssignment WHERE ServerId = @id AND ServerRoleId = @role;",
                ("@id", SqlDbType.Int, serverId), ("@role", SqlDbType.Int, role)).ExecuteNonQueryAsync();
            changes.Add($"-role {RoleName(role)}");
        }
    }

    /* ---------- AGs and databases ---------- */

    /// <summary>Syncs the server's AGs and returns name -> AvailabilityGroupId.</summary>
    private async Task<Dictionary<string, int>> SyncAvailabilityGroupsAsync(int serverId, ServerResult scan, List<string> changes)
    {
        var existing = new Dictionary<string, (int Id, string? Listener)>(StringComparer.OrdinalIgnoreCase);
        var nextSort = 0;
        await using (var cmd = Command("SELECT AvailabilityGroupId, Name, Listener, SortOrder FROM dbo.AvailabilityGroup WHERE ServerId = @id;",
                         ("@id", SqlDbType.Int, serverId)))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                existing[r.GetString(1)] = (r.GetInt32(0), r.IsDBNull(2) ? null : r.GetString(2));
                nextSort = Math.Max(nextSort, r.GetInt32(3) + 1);
            }
        }

        var answered = scan.SqlInstances.Where(i => i.Error is null).ToList();
        var found = answered.SelectMany(i => i.AvailabilityGroups)
            .GroupBy(ag => ag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ag in found)
        {
            if (existing.TryGetValue(ag.Name, out var current))
            {
                ids[ag.Name] = current.Id;
                if (ag.Listener is not null && !string.Equals(ag.Listener, current.Listener, StringComparison.OrdinalIgnoreCase))
                {
                    await Command("UPDATE dbo.AvailabilityGroup SET Listener = @listener WHERE AvailabilityGroupId = @id;",
                        ("@listener", SqlDbType.NVarChar, ag.Listener), ("@id", SqlDbType.Int, current.Id)).ExecuteNonQueryAsync();
                    changes.Add($"AG {ag.Name} listener -> {ag.Listener}");
                }
                continue;
            }

            ids[ag.Name] = (int)(await Command("""
                INSERT dbo.AvailabilityGroup (ServerId, Name, Listener, SortOrder)
                OUTPUT INSERTED.AvailabilityGroupId
                VALUES (@server, @name, @listener, @sort);
                """,
                ("@server", SqlDbType.Int, serverId), ("@name", SqlDbType.NVarChar, ag.Name), ("@listener", SqlDbType.NVarChar, ag.Listener),
                ("@sort", SqlDbType.Int, nextSort++)).ExecuteScalarAsync())!;
            changes.Add($"+AG {ag.Name}");
        }

        // Only remove AGs when every instance answered; otherwise a missing AG may just be an instance that failed.
        if (answered.Count == scan.SqlInstances.Count)
        {
            foreach (var (agName, current) in existing.Where(e => !ids.ContainsKey(e.Key)))
            {
                await Command("DELETE dbo.AvailabilityGroup WHERE AvailabilityGroupId = @id;", ("@id", SqlDbType.Int, current.Id)).ExecuteNonQueryAsync();
                changes.Add($"-AG {agName}");
            }
        }
        return ids;
    }

    private async Task SyncDatabasesAsync(string serverName, int serverId, ServerResult scan, Dictionary<string, int> agIds,
                                          Dictionary<string, DateTime> agBackups, List<string> changes)
    {
        var existing = new Dictionary<string, (int Id, string? Instance, string Name, int? AgId, decimal? SizeMb, DateTime? LastFullBackup)>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = Command("SELECT DatabaseId, InstanceName, Name, AvailabilityGroupId, SizeMb, LastFullBackup FROM dbo.[Database] WHERE ServerId = @id;",
                         ("@id", SqlDbType.Int, serverId)))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var instance = r.IsDBNull(1) ? null : r.GetString(1);
                existing[DatabaseKey(instance, r.GetString(2))] = (r.GetInt32(0), instance, r.GetString(2), NullableInt(r, 3),
                    r.IsDBNull(4) ? null : r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDateTime(5));
            }
        }

        int? applicationId = null;
        int added = 0, updated = 0, removed = 0;
        foreach (var instance in scan.SqlInstances.Where(i => i.Error is null))
        {
            string? instanceName = instance.Instance == WmiScanner.DefaultSqlServiceName ? null : instance.Instance;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in instance.Databases)
            {
                var key = DatabaseKey(instanceName, d.Name);
                seen.Add(key);
                int? agId = d.AvailabilityGroup is not null && agIds.TryGetValue(d.AvailabilityGroup, out var id) ? id : null;
                var size = d.SizeMb is decimal s ? Math.Round(s, 2) : (decimal?)null;

                // AG backups usually run on one replica only: take the latest seen on any replica of the same AG.
                var backup = WholeSeconds(d.LastFullBackup);
                var agListener = instance.AvailabilityGroups.FirstOrDefault(a => string.Equals(a.Name, d.AvailabilityGroup, StringComparison.OrdinalIgnoreCase))?.Listener;
                if (d.AvailabilityGroup is not null && agBackups.TryGetValue(AgBackupKey(d.AvailabilityGroup, agListener, d.Name), out var agBackup)
                    && (backup is null || agBackup > backup))
                {
                    backup = WholeSeconds(agBackup);
                }

                if (!existing.TryGetValue(key, out var row))
                {
                    applicationId ??= await UnassignedApplicationIdAsync();
                    await Command("""
                        INSERT dbo.[Database] (Name, ServerId, InstanceName, AvailabilityGroupId, ApplicationId, SizeMb, LastFullBackup)
                        VALUES (@name, @server, @instance, @ag, @app, @size, @backup);
                        """,
                        ("@name", SqlDbType.NVarChar, d.Name), ("@server", SqlDbType.Int, serverId), ("@instance", SqlDbType.NVarChar, instanceName),
                        ("@ag", SqlDbType.Int, agId), ("@app", SqlDbType.Int, applicationId), ("@size", SqlDbType.Decimal, size),
                        ("@backup", SqlDbType.DateTime2, backup)).ExecuteNonQueryAsync();
                    added++;
                    continue;
                }

                // A missing backup in the scan keeps the recorded one (e.g. msdb history purged).
                backup ??= row.LastFullBackup;
                if (row.AgId == agId && row.SizeMb == size && row.LastFullBackup == backup)
                {
                    continue;
                }
                await Command("UPDATE dbo.[Database] SET AvailabilityGroupId = @ag, SizeMb = @size, LastFullBackup = @backup WHERE DatabaseId = @id;",
                    ("@ag", SqlDbType.Int, agId), ("@size", SqlDbType.Decimal, size), ("@backup", SqlDbType.DateTime2, backup),
                    ("@id", SqlDbType.Int, row.Id)).ExecuteNonQueryAsync();
                updated++;
            }

            foreach (var gone in existing.Values.Where(e => string.Equals(e.Instance, instanceName, StringComparison.OrdinalIgnoreCase)
                                                             && !seen.Contains(DatabaseKey(e.Instance, e.Name))))
            {
                await Command("DELETE dbo.[Database] WHERE DatabaseId = @id;", ("@id", SqlDbType.Int, gone.Id)).ExecuteNonQueryAsync();
                Console.WriteLine($"      {serverName}{(instanceName is null ? "" : $"\\{instanceName}")}: database {gone.Name} is no longer on the server, removed");
                removed++;
            }
        }

        if (added > 0) changes.Add($"+{added} database(s)");
        if (updated > 0) changes.Add($"{updated} database(s) updated");
        if (removed > 0) changes.Add($"-{removed} database(s)");
    }

    /* ---------- IIS ---------- */

    private async Task SyncIisSitesAsync(int serverId, ServerResult scan, List<string> changes)
    {
        var existing = new Dictionary<string, (int Id, string? State)>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = Command("SELECT IisSiteId, Name, State FROM dbo.IisSite WHERE ServerId = @id;", ("@id", SqlDbType.Int, serverId)))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) existing[r.GetString(1)] = (r.GetInt32(0), r.IsDBNull(2) ? null : r.GetString(2));
        }

        var found = scan.IisSites.Where(s => s.Name.Length > 0).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int added = 0, removed = 0;
        var stateChanges = new List<string>();
        foreach (var site in found)
        {
            if (!existing.TryGetValue(site.Name, out var row))
            {
                await Command("INSERT dbo.IisSite (ServerId, Name, State) VALUES (@id, @name, @state);",
                    ("@id", SqlDbType.Int, serverId), ("@name", SqlDbType.NVarChar, site.Name),
                    ("@state", SqlDbType.NVarChar, site.State)).ExecuteNonQueryAsync();
                added++;
            }
            // A state the scan couldn't read keeps the recorded one.
            else if (site.State is not null && site.State != row.State)
            {
                await Command("UPDATE dbo.IisSite SET State = @state WHERE IisSiteId = @id;",
                    ("@state", SqlDbType.NVarChar, site.State), ("@id", SqlDbType.Int, row.Id)).ExecuteNonQueryAsync();
                stateChanges.Add($"{site.Name} {row.State ?? "-"} -> {site.State}");
            }
        }
        var foundSet = found.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (site, row) in existing.Where(e => !foundSet.Contains(e.Key)))
        {
            await Command("DELETE dbo.IisSite WHERE IisSiteId = @id;", ("@id", SqlDbType.Int, row.Id)).ExecuteNonQueryAsync();
            removed++;
        }
        if (added > 0) changes.Add($"+{added} IIS site(s)");
        if (removed > 0) changes.Add($"-{removed} IIS site(s)");
        if (stateChanges.Count > 0) changes.Add($"IIS state: {string.Join(", ", stateChanges)}");
    }

    /* ---------- Helpers ---------- */

    private SqlCommand Command(string sql, params (string Name, SqlDbType Type, object? Value)[] parameters)
    {
        var cmd = new SqlCommand(sql, db, tx);
        foreach (var (name, type, value) in parameters)
        {
            var p = cmd.Parameters.Add(name, type);
            p.Value = value ?? DBNull.Value;
            if (type == SqlDbType.Decimal)
            {
                (p.Precision, p.Scale) = ((byte)19, (byte)2);
            }
        }
        return cmd;
    }

    private async Task<Dictionary<string, int>> ReadLookupAsync(string sql)
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = Command(sql);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) values[r.GetString(0)] = r.GetInt32(1);
        return values;
    }

    /// <summary>Id of <paramref name="name"/> in an Admin list (Environment, OperatingSystem, SqlVersion), adding it at the end if missing.</summary>
    private async Task<int> LookupIdAsync(string table, string idColumn, string name, Dictionary<string, int>? cache)
    {
        if (cache is not null && cache.TryGetValue(name, out var cached))
        {
            return cached;
        }
        var id = (int)(await Command($"""
            DECLARE @id int = (SELECT {idColumn} FROM dbo.[{table}] WHERE Name = @name);
            IF @id IS NULL
            BEGIN
                INSERT dbo.[{table}] (Name, SortOrder) SELECT @name, ISNULL(MAX(SortOrder), -1) + 1 FROM dbo.[{table}];
                SET @id = SCOPE_IDENTITY();
            END
            SELECT @id;
            """, ("@name", SqlDbType.NVarChar, name)).ExecuteScalarAsync())!;
        if (cache is not null)
        {
            cache[name] = id;
        }
        return id;
    }

    private async Task<int> UnassignedApplicationIdAsync() => (int)(await Command("""
        DECLARE @id int = (SELECT ApplicationId FROM dbo.Application WHERE Name = N'Unassigned');
        IF @id IS NULL
        BEGIN
            INSERT dbo.Application (Name) VALUES (N'Unassigned');
            SET @id = SCOPE_IDENTITY();
        END
        SELECT @id;
        """).ExecuteScalarAsync())!;

    /// <summary>Latest full backup per AG database across every replica in the scan.</summary>
    private static Dictionary<string, DateTime> LatestAgBackups(IEnumerable<ServerResult> servers)
    {
        var latest = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in servers.SelectMany(s => s.SqlInstances))
        {
            foreach (var d in instance.Databases.Where(d => d.AvailabilityGroup is not null && d.LastFullBackup is not null))
            {
                var listener = instance.AvailabilityGroups.FirstOrDefault(a => string.Equals(a.Name, d.AvailabilityGroup, StringComparison.OrdinalIgnoreCase))?.Listener;
                var key = AgBackupKey(d.AvailabilityGroup!, listener, d.Name);
                if (!latest.TryGetValue(key, out var current) || d.LastFullBackup > current)
                {
                    latest[key] = d.LastFullBackup!.Value;
                }
            }
        }
        return latest;
    }

    // The listener tells apart same-named AGs on different clusters.
    private static string AgBackupKey(string ag, string? listener, string database) => $"{ag}\u001f{listener}\u001f{database}";

    private static string DatabaseKey(string? instance, string name) => $"{instance}\u001f{name}";

    /// <summary>asmsqlp01.calegis.net -> ASMSQLP01, as the dashboard stores names.</summary>
    public static string ShortName(string host) => host.Split('.')[0].ToUpperInvariant();

    // [Database].LastFullBackup is datetime2(0); drop the milliseconds so an unchanged backup compares equal.
    private static DateTime? WholeSeconds(DateTime? value) =>
        value is DateTime v ? new DateTime(v.Ticks - v.Ticks % TimeSpan.TicksPerSecond, v.Kind) : null;

    private static int? NullableInt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    private static string? NameOf(Dictionary<string, int> lookup, int? id) =>
        id is null ? null : lookup.FirstOrDefault(kv => kv.Value == id).Key ?? $"#{id}";

    private static string? TopologyName(int? id) => id switch { StandaloneTopologyId => "Standalone", AgTopologyId => "AG", MixedTopologyId => "AG + Standalone", null => null, _ => $"#{id}" };

    private static string RoleName(int id) => id switch { SqlServerRoleId => "SQL Server", IisRoleId => "IIS", _ => $"#{id}" };
}
