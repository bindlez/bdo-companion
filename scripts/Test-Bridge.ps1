param(
    [Parameter(Mandatory)] [string] $Executable,
    [int] $Port = 17392
)

$ErrorActionPreference = 'Stop'
$version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../VERSION') -Raw).Trim()
$base = "http://127.0.0.1:$Port"
$process = Start-Process -WindowStyle Hidden -PassThru -FilePath (Resolve-Path -LiteralPath $Executable) -ArgumentList "--port $Port --sample true"
try {
    $health = $null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($process.HasExited) { throw "Companion exited with code $($process.ExitCode)" }
        try {
            $health = Invoke-RestMethod "$base/health" -TimeoutSec 1
            break
        } catch {
            Start-Sleep -Milliseconds 200
        }
    }
    if (!$health.sample -or $health.pid -ne $process.Id -or $health.app_version -cne $version) {
        throw 'Unexpected health response or executable version'
    }
    $reading = Invoke-RestMethod "$base/durability" -TimeoutSec 5
    if ($reading.current -ne 45 -or $reading.maximum -ne 60 -or $reading.seconds_per_craft -ne 2) {
        throw 'Unexpected sample reading'
    }

    $body = @{ instance_id = $reading.instance_id; signal = $reading.signal } | ConvertTo-Json -Compress
    $denied = Invoke-WebRequest "$base/ack" -Method Post -Headers @{ Origin = 'http://example.test' } -ContentType 'application/json' -Body $body -SkipHttpErrorCheck -TimeoutSec 5
    if ($denied.StatusCode -ne 403) { throw 'Foreign origin was accepted' }
    $reply = Invoke-RestMethod "$base/ack" -Method Post -Headers @{ Origin = $base } -ContentType 'application/json' -Body $body -TimeoutSec 5
    if ($reply.acknowledged_signal -ne 1 -or (Invoke-RestMethod "$base/health" -TimeoutSec 5).last_ack_signal -ne 1) {
        throw 'Acknowledgment was not recorded'
    }
    $origin = @{ Origin = $base }
    $import = '{"region":"NA","items":[{"id":100,"name":"Test material","price":4200}]}'
    $saved = Invoke-RestMethod "$base/market-prices" -Method Post -Headers $origin -ContentType 'application/json' -Body $import -TimeoutSec 5
    if ($saved.quotes_received -ne 1) { throw 'Market quote was not imported' }
    $lookup = Invoke-RestMethod "$base/craft-quotes" -Method Post -Headers $origin -ContentType 'application/json' -Body '{"region":"NA","ids":[100]}' -TimeoutSec 5
    if ($lookup.items.Count -ne 1 -or $lookup.items[0].price -ne 4200) { throw 'Recipe quote did not round-trip' }
    $otherRegion = Invoke-RestMethod "$base/craft-quotes" -Method Post -Headers $origin -ContentType 'application/json' -Body '{"region":"EU","ids":[100]}' -TimeoutSec 5
    if ($otherRegion.items.Count -ne 0) { throw 'Market quote leaked across regions' }
    'Native bridge smoke test passed.'
} finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
