# AnonymMailClient Build and Publish Automation Script
param(
    [string]$DeployRoot = "C:\E\kp\aaBenimProgramlarim\AnonymMailClient"
)

$ErrorActionPreference = 'Stop'
$slnDir = $PSScriptRoot
$projectCsproj = Join-Path $slnDir "AnonymMailClient\AnonymMailClient.csproj"
$publishDir = Join-Path $slnDir "publish"

Write-Host "Creating publish directories..." -ForegroundColor Cyan
$portableDir = Join-Path $publishDir "portable"
$frameworkDir = Join-Path $publishDir "framework-dependent"
New-Item -ItemType Directory -Path $portableDir -Force | Out-Null
New-Item -ItemType Directory -Path $frameworkDir -Force | Out-Null

Write-Host "Publishing Portable (Self-Contained) Single EXE..." -ForegroundColor Cyan
dotnet publish $projectCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $portableDir
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed." }

Write-Host "Publishing Framework-Dependent Single EXE..." -ForegroundColor Cyan
dotnet publish $projectCsproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $frameworkDir
if ($LASTEXITCODE -ne 0) { throw "Framework-dependent publish failed." }

$fdExe = Join-Path $frameworkDir "AnonymMailClient.exe"
$fdRenamed = Join-Path $frameworkDir "AnonymMailClient-FrameworkDependent-RequiresNET10.exe"
if (Test-Path $fdExe) {
    Move-Item -LiteralPath $fdExe -Destination $fdRenamed -Force
}

Write-Host "Signing published executables with Authenticode..." -ForegroundColor Cyan
if (Test-Path "C:\E\kp\aaBenimProgramlarim\Imza\Imzala.ps1") {
    & "C:\E\kp\aaBenimProgramlarim\Imza\Imzala.ps1" $publishDir
    & "C:\E\kp\aaBenimProgramlarim\Imza\Dogrula.ps1" $publishDir
}

Write-Host "Deploying portable build to $DeployRoot..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $DeployRoot -Force | Out-Null

$targetExe = Join-Path $DeployRoot "AnonymMailClient.exe"
$sourcePortable = Join-Path $portableDir "AnonymMailClient.exe"

# If the deployed application is currently running, terminate it before updating
$runningProcs = @(Get-Process | Where-Object { $_.ProcessName -eq 'AnonymMailClient' -and $_.Path -and $_.Path.StartsWith($DeployRoot, [StringComparison]::OrdinalIgnoreCase) })
foreach ($p in $runningProcs) {
    Write-Host "Stopping running instance (PID $($p.Id))..." -ForegroundColor Yellow
    $p.Kill()
    $p.WaitForExit(3000)
}

Copy-Item $sourcePortable $targetExe -Force

# Create Desktop/Start Menu shortcut in C:\E\Kısayollar\!Benim Programlarım
$shortcutFolder = "C:\E\Kısayollar\!Benim Programlarım"
if (Test-Path $shortcutFolder) {
    $shortcutPath = Join-Path $shortcutFolder "AnonymMailClient.lnk"
    $wsh = New-Object -ComObject WScript.Shell
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $DeployRoot
    $shortcut.Description = "AnonymMailClient - Anonymous Tor Email Client"
    $shortcut.Save()
    Write-Host "Created shortcut: $shortcutPath" -ForegroundColor Green
}

Write-Host "Publication and deployment completed successfully." -ForegroundColor Green
