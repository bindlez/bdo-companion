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
    'Native bridge smoke test passed.'
} finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
