# ==================================================================================
# CONFIGURATION
# ==================================================================================
$ProjectName   = "DotNetVisualizer.Web"
$ProjectPath   = ".\DotNetVisualizer.Web\DotNetVisualizer.Web.csproj"
$TargetFolder  = "C:\DAV"
$ServiceName   = "DAV"
$Port          = "9999"
$FirewallRule  = "Allow DotNet Web Service (Port $Port)"

# Ensure running as Administrator
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as an Administrator. Please reopen PowerShell as Administrator."
    Exit
}

Write-Host "=== Starting Automation Deployment for $ServiceName ===" -ForegroundColor Cyan

# ==================================================================================
# 1. STOP & CLEANUP EXISTING SERVICE
# ==================================================================================
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "-> Stopping existing service '$ServiceName'..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    
    Write-Host "-> Removing existing service registration..." -ForegroundColor Yellow
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

# Clear out target directory to prevent System.BadImageFormatException architecture mixups
if (Test-Path $TargetFolder) {
    Write-Host "-> Wiping destination folder '$TargetFolder' to ensure clean state..." -ForegroundColor Yellow
    Remove-Item -Path "$TargetFolder\*" -Recurve -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "-> Creating target folder configuration directory..." -ForegroundColor Green
    New-Item -ItemType Directory -Force -Path $TargetFolder | Out-Null
}

# ==================================================================================
# 2. COMPILING AND PUBLISHING (Self-Contained)
# ==================================================================================
Write-Host "-> Executing pristine self-contained win-x64 compilation..." -ForegroundColor Green
dotnet publish $ProjectPath -c Release -r win-x64 --self-contained true -o $TargetFolder

if ($LASTEXITCODE -ne 0) {
    Write-Error "Compilation / Publishing failed. Aborting deployment."
    Exit
}

# ==================================================================================
# 3. CONFIGURE SECURE FILE PERMISSIONS
# ==================================================================================
Write-Host "-> Applying security permissions on directory '$TargetFolder'..." -ForegroundColor Green
# Ensure local SYSTEM account has explicit FullControl permission to prevent runtime initialization crashes
$Acl = Get-Acl $TargetFolder
$SystemPermission = New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$Acl.SetAccessRule($SystemPermission)
Set-Acl $TargetFolder $Acl

# ==================================================================================
# 4. REGISTER WINDOWS SERVICE
# ==================================================================================
Write-Host "-> Provisioning new Windows Service..." -ForegroundColor Green
$BinaryPath = Join-Path $TargetFolder "$ProjectName.exe"

sc.exe create $ServiceName binPath= $BinaryPath start= auto | Out-Null
sc.exe config $ServiceName Environment= "ASPNETCORE_URLS=http://*:$Port" | Out-Null
sc.exe description $ServiceName "Manages backend runtime logic engine processing for DotNet Assembly Structural Analysis Visualizer." | Out-Null

# ==================================================================================
# 5. OPEN SYSTEM NETWORKING FIREWALL
# ==================================================================================
if (Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue) {
    Remove-NetFirewallRule -DisplayName $FirewallRule | Out-Null
}
Write-Host "-> Configuring firewall inbound exceptions on TCP port $Port..." -ForegroundColor Green
New-NetFirewallRule -DisplayName $FirewallRule -Direction Inbound -LocalPort $Port -Protocol TCP -Action Allow | Out-Null

# ==================================================================================
# 6. VERIFY AND LAUNCH
# ==================================================================================
Write-Host "-> Activating Windows Service..." -ForegroundColor Cyan
Start-Service -Name $ServiceName

if ((Get-Service -Name $ServiceName).Status -eq "Running") {
    Write-Host "===[ SUCCESS ]===" -ForegroundColor Green
    Write-Host "Service '$ServiceName' is active and running."
    Write-Host "Application is listening internally and public-facing on port: $Port" -ForegroundColor Green
} else {
    Write-Error "Service setup finalized but failed to maintain active execution state. Review Event Viewer logs."
}