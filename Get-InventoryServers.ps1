<#
    The server list for the inventory scan, straight from AD: what get-adservers.ps1 -PrimaryNodes returns, without its
    dependencies (no ActiveDirectory or FailoverClusters module, no psping, no admin rights; any domain account can run it).

    Returns the DNS host names (lowercase) of enabled computers running Windows Server whose ManagedBy is one of
    -ManagedBy (default mcpadmin, matched as mcpadmin* against user and group names, like get-adservers does), except
    cluster names and AG listeners: those carry the MSClusterVirtualServer/ service principal name in AD. Physical
    cluster nodes, active or not, are included.

        .\Get-InventoryServers.ps1
        .\Get-InventoryServers.ps1 -ManagedBy mcpadmin, 'SQL Admins'
#>
[CmdletBinding()]
param(
    [string[]]$ManagedBy = @('mcpadmin'),

    # Accepted so this is a drop-in for get-adservers.ps1 -PrimaryNodes; that is what it always returns.
    [switch]$PrimaryNodes
)

$ErrorActionPreference = 'Stop'

# Escapes a value for an LDAP filter; * is kept as a wildcard when -Wildcard is set.
function Format-LdapValue([string]$value, [switch]$Wildcard) {
    $value = $value.Replace('\', '\5c').Replace('(', '\28').Replace(')', '\29').Replace([string][char]0, '\00')
    if (-not $Wildcard) { $value = $value.Replace('*', '\2a') }
    return $value
}

function Find-AD([string]$filter, [string[]]$properties) {
    $searcher = [adsisearcher]$filter
    $searcher.PageSize = 1000
    foreach ($property in $properties) { [void]$searcher.PropertiesToLoad.Add($property) }
    try { $searcher.FindAll() | ForEach-Object { $_ } }
    finally { $searcher.Dispose() }
}

$owners = foreach ($name in $ManagedBy) {
    $pattern = if ($name.Contains('*')) { $name } else { "$name*" }
    $value = Format-LdapValue $pattern -Wildcard
    $found = @(Find-AD "(&(|(objectCategory=person)(objectCategory=group))(|(sAMAccountName=$value)(name=$value)(userPrincipalName=$value)))" 'distinguishedName')
    if ($found.Count -eq 0) { throw "ManagedBy: no AD user or group matches '$pattern'." }
    $found | ForEach-Object { [string]$_.Properties['distinguishedname'][0] }
}

$ownerFilter = '(|' + (($owners | ForEach-Object { "(managedBy=$(Format-LdapValue $_))" }) -join '') + ')'
$filter = '(&(objectCategory=computer)(operatingSystem=Windows Server*)' +
    '(!(userAccountControl:1.2.840.113556.1.4.803:=2))' +    # not disabled
    $ownerFilter +
    '(!(servicePrincipalName=MSClusterVirtualServer/*)))'    # not a cluster name or listener

Find-AD $filter 'dNSHostName', 'name' |
    ForEach-Object {
        $hostName = if ($_.Properties['dnshostname'].Count) { $_.Properties['dnshostname'][0] } else { $_.Properties['name'][0] }
        ([string]$hostName).ToLowerInvariant()
    } |
    Sort-Object -Unique
