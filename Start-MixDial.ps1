# Starts MixDial by compiling MixDial.cs in memory and running it inside PowerShell.
# Used instead of a compiled .exe because Smart App Control blocks unsigned self-built executables.
# Run hidden with:  conhost.exe --headless powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File Start-MixDial.ps1

$source = Get-Content -Raw (Join-Path $PSScriptRoot 'MixDial.cs')
$types = Add-Type -TypeDefinition $source -ReferencedAssemblies System.Windows.Forms, System.Drawing -PassThru
$program = $types | Where-Object FullName -eq 'Program'

$program::LaunchCommand = 'conhost.exe --headless powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
$program::Main()
