<#
.SYNOPSIS
  Owner-run US-International dead-key check. It never changes the active layout or installs a hook.
.DESCRIPTION
  The owner may add and select US-International in Windows Settings before running, then restore their preferred layout.
  This uses only synthetic samples and a temporary project. PASS covers the two samples and the uncertainty-suppression
  unit tests; it does not verify other layouts or a real low-level hook. BuildOnly checks compilation without input.
#>
param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = [System.IO.Path]::GetFullPath((Join-Path $tempRoot ('tasktor-deadkey-' + [guid]::NewGuid().ToString('N'))))
if (-not $scratch.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary path.' }
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $reference = [System.Security.SecurityElement]::Escape((Join-Path $repo 'src/Tasktor.Capture/Tasktor.Capture.csproj'))
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><EnableWindowsTargeting>true</EnableWindowsTargeting></PropertyGroup>
  <ItemGroup><ProjectReference Include="$reference" /></ItemGroup>
</Project>
"@
    $source = @"
using System;
using System.Runtime.InteropServices;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Hook;

internal static class Check
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);

    private static int Main()
    {
        Console.WriteLine("Owner: select US-International yourself before continuing. This program does not change layouts.");
        Console.WriteLine("Type acute-accent e (synthetic sample), then press Enter:");
        string acute = Console.ReadLine() ?? "";
        Console.WriteLine("Type diaeresis u (synthetic sample), then press Enter:");
        string diaeresis = Console.ReadLine() ?? "";
        nint foreground = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(foreground, out _);
        nint layout = GetKeyboardLayout(thread);
        bool ok = thread != 0 && layout != 0 && acute == "\u00e9" && diaeresis == "\u00fc"
            && DeadKeyIsUncertain(layout, false) && DeadKeyIsUncertain(layout, true);
        Console.WriteLine(ok ? "PASS: two synthetic samples and conservative decoder outcomes" : "FAIL: sample, layout, or decoder outcome did not match");
        return ok ? 0 : 1;
    }

    private static bool DeadKeyIsUncertain(nint layout, bool shift)
    {
        var decoder = new KeyDecoder
        {
            DetailedLayout = (vk, scan, shifted, caps, altGr) =>
            {
                var result = WindowsKeyLayout.Translate(vk, scan, shifted, caps, altGr, layout);
                return result.Kind switch
                {
                    LayoutResultKind.Text => new LayoutDecision(LayoutOutcome.Text, result.Text),
                    LayoutResultKind.NoText => new LayoutDecision(LayoutOutcome.NoText, null),
                    _ => new LayoutDecision(LayoutOutcome.Uncertain, null),
                };
            },
        };
        if (shift) decoder.Decode(new RawInput(KeyDecoder.VkLShift, 0x2a, 0, 0, false));
        var decoded = decoder.Decode(new RawInput(0xde, 0x28, 0, 1, false));
        return decoded.Action == KeyAction.Uncertain && decoded.Text is null;
    }
}
"@
    $projectPath = Join-Path $scratch 'DeadKeyCheck.csproj'
    [System.IO.File]::WriteAllText($projectPath, $project, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $scratch 'Program.cs'), $source, [System.Text.UTF8Encoding]::new($false))
    & dotnet build $projectPath --nologo *> $null
    if ($LASTEXITCODE -ne 0) { Write-Output 'FAIL: probe did not build'; exit 1 }
    if ($BuildOnly) { Write-Output 'PASS: dead-key probe builds'; exit 0 }
    & dotnet test (Join-Path $repo 'tests/Tasktor.Capture.Tests/Tasktor.Capture.Tests.csproj') --filter 'FullyQualifiedName~DeadKeyRunTests' --nologo *> $null
    if ($LASTEXITCODE -ne 0) { Write-Output 'FAIL: uncertainty-suppression tests'; exit 1 }
    & dotnet run --project $projectPath --no-build --no-restore
    $result = $LASTEXITCODE
    if ($result -eq 0) { Write-Output 'PASS: uncertainty-suppression tests; hook remains detached pending full verification' }
    exit $result
}
finally {
    if ((Test-Path -LiteralPath $scratch) -and $scratch.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}