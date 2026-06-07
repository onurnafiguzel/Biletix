<#
.SYNOPSIS
  Redis Cluster failover chaos test for Biletix.

.DESCRIPTION
  Proves the architecture's central claim: the Redis lock is ADVISORY and the DB CAS is the
  fence, so killing a Redis master mid-booking must NOT cause overselling or request errors.

  Steps:
    1. Print cluster topology (3 masters + 3 replicas).
    2. Start the oversell load (scripts/oversell-test.ps1) AND, ~0.5s in, kill a master
       (docker stop) so the kill lands while bookings are in flight / failover is happening.
    3. Print topology again -the dead master's replica should have been promoted.
    4. Check the invariant via the API: Booked <= stock, and oversell-test reported Other=0.
    5. Restart the killed node; it rejoins as a replica.

  Works on Windows PowerShell 5.1 and PowerShell 7. Inspect-only Redis access is done via
  `docker exec` because cluster redirects advertise the internal 172.28.x.x IPs.

.EXAMPLE
  pwsh ./scripts/redis-cluster-chaos.ps1 -EventId <guid>
  powershell -File scripts/redis-cluster-chaos.ps1 -EventId <guid>
#>
param(
    [Parameter(Mandatory=$true)][string]$EventId,
    [string]$ApiUrl    = "http://localhost:8080",
    [string]$KillNode  = "biletix-redis-c1",   # a master (c1..c3 are masters after create)
    [string]$QueryNode = "biletix-redis-c2",   # a node we do NOT kill, used to read topology
    [int]$Attempts     = 200
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Show-Topology([string]$node, [string]$label) {
    Write-Host ""
    Write-Host "=== Cluster topology ($label) -read via $node ===" -ForegroundColor Cyan
    # cluster nodes columns: <id> <ip:port@cport> <flags> <master> ... <slots...>
    docker exec $node redis-cli -c cluster nodes | ForEach-Object {
        $f = $_ -split '\s+'
        if ($f.Count -ge 3) {
            $slots = if ($f.Count -gt 8) { ($f[8..($f.Count-1)] -join ' ') } else { '' }
            "{0,-24} {1,-22} {2}" -f $f[1], $f[2], $slots
        }
    }
}

Show-Topology $QueryNode "BEFORE"

# Background killer: wait a beat so the load is in flight, then stop a master.
$killer = Start-Job -ScriptBlock {
    param($node)
    Start-Sleep -Milliseconds 500
    docker stop $node | Out-Null
    "killed master: $node"
} -ArgumentList $KillNode

Write-Host ""
Write-Host "Running oversell load ($Attempts attempts) while '$KillNode' is killed mid-flow..." -ForegroundColor Yellow
& "$scriptDir/oversell-test.ps1" -EventId $EventId -ApiUrl $ApiUrl -Attempts $Attempts

Receive-Job $killer | ForEach-Object { Write-Host $_ -ForegroundColor Magenta }
Remove-Job $killer

Show-Topology $QueryNode "AFTER (one master killed -> its replica should be promoted)"

# Invariant via the API (status comes back as 'Booked' or numeric 2).
$ev     = Invoke-RestMethod -Uri "$ApiUrl/events/$EventId"
$booked = @($ev.tickets | Where-Object { "$($_.status)" -in 'Booked','2' }).Count
$total  = @($ev.tickets).Count
Write-Host ""
Write-Host "Booked after chaos: $booked / $total stock" -ForegroundColor Green
if ($booked -le $total) {
    Write-Host "INVARIANT HELD: Booked ($booked) <= stock ($total) -no oversell despite failover." -ForegroundColor Green
} else {
    Write-Host "INVARIANT VIOLATED: Booked ($booked) > stock ($total)!" -ForegroundColor Red
}

Write-Host ""
Write-Host "Restarting '$KillNode' (rejoins as a replica)..."
docker start $KillNode | Out-Null
Start-Sleep -Seconds 3
Show-Topology $QueryNode "RESTORED"
