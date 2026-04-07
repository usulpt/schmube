$ErrorActionPreference = 'Stop'

function Show-LaunchError {
    param([string]$Message)

    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        $Message,
        'Schmube Launcher',
        [System.Windows.MessageBoxButton]::OK,
        [System.Windows.MessageBoxImage]::Error) | Out-Null
}

try {
    $repoRoot = Split-Path -Path $PSCommandPath -Parent
    $projectPath = Join-Path $repoRoot 'Schmube.csproj'
    $buildOutput = & dotnet build $projectPath -nologo 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed.`r`n`r`n$($buildOutput -join [Environment]::NewLine)"
    }

    $exePath = Join-Path $repoRoot 'bin\Debug\net9.0-windows\Schmube.exe'
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "Could not find $exePath."
    }

    $exeDirectory = Split-Path -Path $exePath -Parent
    Start-Process -FilePath $exePath -WorkingDirectory $exeDirectory
}
catch {
    Show-LaunchError $_.Exception.Message
    exit 1
}
