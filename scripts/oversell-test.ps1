param(
    [Parameter(Mandatory=$true)][string]$EventId,
    [string]$ApiUrl = "http://localhost:8080",
    [int]$Attempts = 110
)

$ErrorActionPreference = 'Stop'

Write-Host "Fetching tickets for event $EventId..."
$ev = Invoke-RestMethod -Uri "$ApiUrl/events/$EventId"
# status, API'de string ("Available") ya da sayisal enum (0 = Available) olarak donebilir;
# ikisini de kabul et. ("$($_.status)" stringe cevirir; int/string karsilastirma hatasini onler.)
$available = @($ev.tickets | Where-Object { "$($_.status)" -in 'Available','0' } | ForEach-Object { $_.id })
Write-Host "Available tickets: $($available.Count)"
if ($available.Count -lt 1) { throw "No available tickets to test." }

$jobs = 1..$Attempts | ForEach-Object {
    $i = $_
    Start-Job -ScriptBlock {
        param($ticketId, $url, $eventId)
        $body = @{
            ticketIds = @($ticketId)
            userId = [guid]::NewGuid().ToString()
            paymentDetails = @{ cardNumberMasked = "**** **** **** 1234"; holder = "Test" }
        } | ConvertTo-Json -Depth 4
        try {
            # -UseBasicParsing: PS 5.1'in IE tabanli HTML ayristiricisini devre disi birakir
            #   (PS 7'de zaten varsayilan, zararsiz). -SkipHttpErrorCheck KULLANMIYORUZ cunku
            #   PS 7'ye ozeldir; 4xx/5xx'i asagidaki catch'te yakaliyoruz.
            $r = Invoke-WebRequest -Uri "$url/bookings/$eventId" -Method Post -Body $body -ContentType 'application/json' -UseBasicParsing
            return [int]$r.StatusCode
        } catch {
            # PS 5.1 (WebException) ve PS 7 (HttpResponseException): her ikisinde de
            # Exception.Response.StatusCode bir HttpStatusCode enum'idir -> [int] ile koda cevrilir.
            if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
            return -1
        }
    } -ArgumentList $available[($i - 1) % $available.Count], $ApiUrl, $EventId
}

$results = $jobs | Wait-Job | Receive-Job
$jobs | Remove-Job

$ok = ($results | Where-Object { $_ -eq 200 }).Count
$conflict = ($results | Where-Object { $_ -eq 409 }).Count
$other = ($results | Where-Object { $_ -ne 200 -and $_ -ne 409 }).Count

Write-Host ""
Write-Host "200 OK     : $ok"
Write-Host "409 CONFLICT: $conflict"
Write-Host "Other      : $other"
Write-Host ""
Write-Host "Expected: 200 count <= AvailableTickets ($($available.Count))"
