<#
    Builds the App and MemScan projects and drops Desktop shortcuts for
    both:
      - "M&M Damage Parser"  -> dist\MnmDamageParser.exe       (elevated --
        ReadProcessMemory needs admin)
      - "M&M Mob Map"        -> dist\MemScan\MnmMemScan.exe    (NOT
        elevated -- confirmed working unelevated on this machine; opens a
        console window with MemScan's REPL, where `map <mob name>` opens
        the dot-map window)

    Just run it once from PowerShell:

        powershell -ExecutionPolicy Bypass -File .\install.ps1

    Re-run it any time after a code change to refresh dist\ (the shortcuts
    keep working -- they point at fixed paths under dist\). Close both
    tools first if they're running -- a locked exe/dll can't be republished.
#>

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$desktop = [Environment]::GetFolderPath('Desktop')
$shell   = New-Object -ComObject WScript.Shell

function New-AppIcon {
    param([string]$Path, [string]$Letter, [string]$HexColor)
    try {
        Add-Type -AssemblyName System.Drawing
        $bmp = New-Object System.Drawing.Bitmap 64, 64
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::FromArgb(24, 28, 38))
        $font  = New-Object System.Drawing.Font 'Segoe UI', 26, ([System.Drawing.FontStyle]::Bold)
        $color = [System.Drawing.ColorTranslator]::FromHtml($HexColor)
        $brush = New-Object System.Drawing.SolidBrush $color
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
        $g.DrawString($Letter, $font, $brush, (New-Object System.Drawing.RectangleF 0,0,64,64), $fmt)
        $g.Dispose(); $brush.Dispose()
        $hicon = $bmp.GetHicon()
        $icon  = [System.Drawing.Icon]::FromHandle($hicon)
        $fs = [System.IO.File]::Create($Path)
        $icon.Save($fs); $fs.Close()
        $icon.Dispose(); $bmp.Dispose()
        return $Path
    } catch {
        Write-Warning "couldn't generate an icon at $Path ($($_.Exception.Message))"
        return $null
    }
}

function Publish-Shortcut {
    param(
        [string]$Proj, [string]$OutDir, [string]$ExeName,
        [string]$ShortcutName, [string]$Description,
        [string]$IconLetter, [string]$IconColor,
        [switch]$Elevate
    )
    Write-Host "Publishing $Proj -> $OutDir ..." -ForegroundColor Cyan
    dotnet publish $Proj -c Release -o $OutDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Proj ($LASTEXITCODE)" }

    $exePath = Join-Path $OutDir $ExeName
    if (-not (Test-Path $exePath)) { throw "expected $exePath after publish, not found" }

    $iconPath = New-AppIcon -Path (Join-Path $OutDir 'app.ico') -Letter $IconLetter -HexColor $IconColor
    if (-not $iconPath) { $iconPath = $exePath }

    $lnkPath = Join-Path $desktop "$ShortcutName.lnk"
    $sc = $shell.CreateShortcut($lnkPath)
    $sc.TargetPath       = $exePath
    $sc.WorkingDirectory = $OutDir
    $sc.IconLocation     = $iconPath
    $sc.Description      = $Description
    $sc.Save()

    if ($Elevate) {
        # Flip the "Run as administrator" bit in the .lnk (byte 0x15, flag 0x20).
        $bytes = [System.IO.File]::ReadAllBytes($lnkPath)
        $bytes[0x15] = $bytes[0x15] -bor 0x20
        [System.IO.File]::WriteAllBytes($lnkPath, $bytes)
    }

    Write-Host "  Shortcut : $lnkPath"
    Write-Host "  Launches : $exePath $(if ($Elevate) { '(elevated)' } else { '(not elevated)' })"
    Write-Host ""
}

Publish-Shortcut `
    -Proj (Join-Path $root 'src\App\App.csproj') `
    -OutDir (Join-Path $root 'dist') `
    -ExeName 'MnmDamageParser.exe' `
    -ShortcutName 'M&M Damage Parser' `
    -Description 'M&M Damage Parser (runs as Administrator)' `
    -IconLetter 'M' -IconColor '#FF4500' `
    -Elevate

Publish-Shortcut `
    -Proj (Join-Path $root 'src\MemScan\MemScan.csproj') `
    -OutDir (Join-Path $root 'dist\MemScan') `
    -ExeName 'MnmMemScan.exe' `
    -ShortcutName 'M&M Mob Map' `
    -Description 'M&M memory scanner / mob map REPL (not elevated)' `
    -IconLetter 'R' -IconColor '#22C7FF'

Write-Host "Done." -ForegroundColor Green
Write-Host ""
Write-Host "M&M Damage Parser: double-click, approve the UAC prompt (expected -- it"
Write-Host "  needs admin to read the game's memory), the meter window opens."
Write-Host "M&M Mob Map: double-click, a console window opens with the MemScan prompt."
Write-Host "  Type 'map' and press Enter to open the dot-map window (no mob name needed)."
