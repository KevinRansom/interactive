Param(
  [string][Alias('c')]$configuration = "Debug",
  [switch] $ci
)

Set-StrictMode -version 2.0
$ErrorActionPreference = "Stop"

function TestUsingNPM([string] $testPath) {
    Write-Host "Installing packages"
    Start-Process -PassThru -WindowStyle Hidden -WorkingDirectory $testPath -Wait npm "i"
    Write-Host "Testing"
    $test = Start-Process -PassThru -WindowStyle Hidden -WorkingDirectory $testPath -Wait npm "run ciTest"
    Write-Host "Done with code $($test.ExitCode)"
    return $test.ExitCode
}

try {
    if ($ci -eq $true) {
        . (Join-Path $PSScriptRoot "..\buildSqlTools.cmd") $configuration
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    # invoke regular build/test script
    . (Join-Path $PSScriptRoot "common\build.ps1") -projects "$PSScriptRoot\..\dotnet-interactive.sln" @args
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
catch {
    Write-Host $_
    Write-Host $_.Exception
    Write-Host $_.ScriptStackTrace
    exit 1
}