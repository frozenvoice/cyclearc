#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts/ReleaseDependencies.ps1')

function Assert-True([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "ASSERT FAILED: $Message" }
}
function Assert-Throws([scriptblock]$Action, [string]$Fragment) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Fragment*") { throw "Expected '$Fragment', got '$($_.Exception.Message)'" }
        return
    }
    throw "Expected an exception containing '$Fragment'"
}

# Synthetic PE and .NET bundle fixtures: no SDK probes, download, process launch, or install.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
public static class ReleaseDependencyFixture {
    static void U16(byte[] b, int p, ushort v) => BitConverter.GetBytes(v).CopyTo(b, p);
    static void U32(byte[] b, int p, uint v) => BitConverter.GetBytes(v).CopyTo(b, p);
    static void Text(byte[] b, int p, string v) => Encoding.ASCII.GetBytes(v + "\0").CopyTo(b, p);
    public static byte[] Pe(string import = "kernel32.dll", bool delay = false, bool clr = false,
        ushort machine = 0x8664, bool exports = false) {
        var b = new byte[2048];
        U16(b, 0, 0x5a4d); U32(b, 0x3c, 0x80); U32(b, 0x80, 0x4550);
        U16(b, 0x84, machine); U16(b, 0x86, 1); U16(b, 0x96, 0x22);
        const int opt = 0x98;
        bool x86 = machine == 0x14c;
        U16(b, 0x94, (ushort)(x86 ? 224 : 240));
        int directories = opt + (x86 ? 96 : 112);
        U16(b, opt, (ushort)(x86 ? 0x10b : 0x20b)); U32(b, opt + 16, 0x1000); U32(b, opt + 32, 0x1000);
        U32(b, opt + 36, 0x200); U32(b, opt + 56, 0x2000); U32(b, opt + 60, 0x200);
        U16(b, opt + 68, 2); U32(b, opt + (x86 ? 92 : 108), 16);
        int section = opt + (x86 ? 224 : 240);
        Text(b, section, ".rdata"); U32(b, section + 8, 0x600); U32(b, section + 12, 0x1000);
        U32(b, section + 16, 0x600); U32(b, section + 20, 0x200); U32(b, section + 36, 0x40000040);
        if (import != null) {
            int dir = directories + (delay ? 13 : 1) * 8;
            U32(b, dir, 0x1000); U32(b, dir + 4, delay ? 64u : 40u);
            if (delay) U32(b, 0x200, 1);
            U32(b, 0x200 + (delay ? 4 : 12), 0x1050); Text(b, 0x250, import);
        }
        if (clr) {
            U32(b, directories + 14 * 8, 0x1200); U32(b, directories + 14 * 8 + 4, 72);
            U32(b, 0x400, 72); U16(b, 0x404, 2); U16(b, 0x406, 5);
            U32(b, 0x408, 0x1260); U32(b, 0x40c, 100);
        }
        if (exports) {
            U32(b, directories, 0x1100); U32(b, directories + 4, 40);
            U32(b, 0x318, 3); U32(b, 0x320, 0x1150);
            string[] names = { "g_CLREngineMetrics", "CLRJitAttachState", "DotNetRuntimeInfo" };
            for (int i = 0; i < names.Length; i++) {
                U32(b, 0x350 + i * 4, (uint)(0x1180 + i * 40)); Text(b, 0x380 + i * 40, names[i]);
            }
        }
        return b;
    }
    public static byte[] Bundle(bool framework = false, string rid = "win-x64",
        string missing = "", bool exports = true, bool duplicate = false, bool badOffset = false) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Pe(exports: exports));
        var entries = new List<(long offset, long size, byte type, string name)>();
        void Add(byte type, string name, string data) {
            if (name == missing) return;
            byte[] content = Encoding.UTF8.GetBytes(data);
            entries.Add((stream.Position, content.Length, type, name)); writer.Write(content);
        }
        Add(4, "CycleArc.runtimeconfig.json", framework
            ? "{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}"
            : "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\"},{\"name\":\"Microsoft.WindowsDesktop.App\"}]}}");
        Add(3, "CycleArc.deps.json", "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0/" + rid + "\"}}");
        Add(1, "System.Private.CoreLib.dll", "synthetic managed runtime");
        Add(1, "PresentationFramework.dll", "synthetic WPF runtime");
        Add(2, "PresentationNative_cor3.dll", "synthetic native runtime");
        Add(2, "wpfgfx_cor3.dll", "synthetic native renderer");
        if (duplicate) entries.Add(entries[entries.Count - 1]);
        long header = stream.Position;
        writer.Write(6u); writer.Write(0u); writer.Write(entries.Count); writer.Write("fixture-id");
        var deps = entries.Find(e => e.type == 3); var config = entries.Find(e => e.type == 4);
        writer.Write(deps.offset); writer.Write(deps.size); writer.Write(config.offset); writer.Write(config.size); writer.Write(0ul);
        foreach (var e in entries) {
            writer.Write(badOffset ? header + 1 : e.offset); writer.Write(e.size); writer.Write(0L); writer.Write(e.type); writer.Write(e.name);
        }
        stream.Position = 0x600; writer.Write(header);
        writer.Write(Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE"));
        return stream.ToArray();
    }
}
'@

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-release-deps-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $native = Join-Path $testRoot 'CycleArc-Setup.exe'
    [IO.File]::WriteAllBytes($native, [ReleaseDependencyFixture]::Pe())
    $result = Assert-NativeReleaseExecutable -Path $native
    Assert-True ($result.Architecture -ceq 'win-x64' -and !$result.ManagedClrHeader) 'native x64 PE accepted'
    foreach ($dll in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'mscoree.dll', 'vcruntime140.dll', 'msvcp140.dll', 'Microsoft.Build.dll', 'arbitrary.dll')) {
        [IO.File]::WriteAllBytes($native, [ReleaseDependencyFixture]::Pe($dll))
        Assert-Throws { Assert-NativeReleaseExecutable -Path $native } $dll
        [IO.File]::WriteAllBytes($native, [ReleaseDependencyFixture]::Pe($dll, $true))
        Assert-Throws { Assert-NativeReleaseExecutable -Path $native } $dll
    }
    [IO.File]::WriteAllBytes($native, [ReleaseDependencyFixture]::Pe('api-ms-win-crt-runtime-l1-1-0.dll', $true))
    Assert-NativeReleaseExecutable -Path $native | Out-Null
    [IO.File]::WriteAllBytes($native, [ReleaseDependencyFixture]::Pe('kernel32.dll', $false, $true))
    Assert-Throws { Assert-NativeReleaseExecutable -Path $native } 'managed CLR header'
    [IO.File]::WriteAllBytes($native, [ReleaseDependencyFixture]::Pe('kernel32.dll', $false, $false, 0xaa64))
    Assert-Throws { Assert-NativeReleaseExecutable -Path $native } 'Windows x64'
    [IO.File]::WriteAllBytes($native, [ReleaseDependencyFixture]::Pe('kernel32.dll', $false, $false, 0x14c))
    Assert-Throws { Assert-NativeReleaseExecutable -Path $native } 'Windows x64'
    Assert-True ((Assert-NativeReleaseExecutable -Path $native -AllowX86).Architecture -ceq 'win-x86') 'only the embedded engine may use Windows x86'
    [IO.File]::WriteAllBytes($native, [byte[]]@(77, 90, 0))
    Assert-Throws { Assert-NativeReleaseExecutable -Path $native } 'Image'
    $brokenImports = [ReleaseDependencyFixture]::Pe()
    [BitConverter]::GetBytes([uint32]0x1100).CopyTo($brokenImports, 0x20c)
    $brokenImports[0x214] = 1 # Destroy descriptor terminator.
    [IO.File]::WriteAllBytes($native, $brokenImports)
    Assert-Throws { Assert-NativeReleaseExecutable -Path $native } 'import'

    $app = Join-Path $testRoot 'CycleArc.exe'
    [IO.File]::WriteAllBytes($app, [ReleaseDependencyFixture]::Bundle())
    Assert-True ((Assert-SelfContainedReleaseApp -Path $app).SelfContained) 'bundled x64 runtimes accepted'
    Assert-Throws { Assert-NativeReleaseExecutable -Path $app } 'CoreCLR host'
    [IO.File]::WriteAllBytes($app, [ReleaseDependencyFixture]::Bundle($true))
    Assert-Throws { Assert-SelfContainedReleaseApp -Path $app } 'installed .NET framework'
    [IO.File]::WriteAllBytes($app, [ReleaseDependencyFixture]::Bundle($false, 'win-arm64'))
    Assert-Throws { Assert-SelfContainedReleaseApp -Path $app } 'win-x64'
    foreach ($missing in @('System.Private.CoreLib.dll', 'PresentationFramework.dll', 'PresentationNative_cor3.dll', 'wpfgfx_cor3.dll')) {
        [IO.File]::WriteAllBytes($app, [ReleaseDependencyFixture]::Bundle($false, 'win-x64', $missing))
        Assert-Throws { Assert-SelfContainedReleaseApp -Path $app } $missing
    }
    [IO.File]::WriteAllBytes($app, [ReleaseDependencyFixture]::Bundle($false, 'win-x64', '', $false))
    Assert-Throws { Assert-SelfContainedReleaseApp -Path $app } 'CoreCLR/JIT'
    [IO.File]::WriteAllBytes($app, [ReleaseDependencyFixture]::Bundle($false, 'win-x64', '', $true, $true))
    Assert-Throws { Assert-SelfContainedReleaseApp -Path $app } 'duplicate bundle entry'
    [IO.File]::WriteAllBytes($app, [ReleaseDependencyFixture]::Bundle($false, 'win-x64', '', $true, $false, $true))
    Assert-Throws { Assert-SelfContainedReleaseApp -Path $app } 'Invalid'
    [IO.File]::WriteAllBytes($app, [ReleaseDependencyFixture]::Pe())
    Assert-Throws { Assert-SelfContainedReleaseApp -Path $app } 'single-file bundle'

    [IO.File]::WriteAllBytes($app, [ReleaseDependencyFixture]::Bundle())
    $package = Join-Path $testRoot 'full.nupkg'
    $zip = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
    try { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $app, 'lib/net10.0/CycleArc.exe') | Out-Null }
    finally { $zip.Dispose() }
    Assert-PackagedReleaseApp -PackagePath $package -PublishedAppPath $app
    [IO.File]::WriteAllText($app, 'tampered')
    Assert-Throws { Assert-PackagedReleaseApp -PackagePath $package -PublishedAppPath $app } 'differs'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (!$resolved.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

# Production Setup must stay a native engine wrapper, separate from source build preparation.
$setupRoot = Join-Path $repoRoot 'src/CycleArc.Setup'
$project = Get-Content -LiteralPath (Join-Path $setupRoot 'CycleArc.Setup.csproj') -Raw
Assert-True ($project -match '<PublishAot>true</PublishAot>' -and $project -match '<RuntimeIdentifier>win-x64</RuntimeIdentifier>') 'setup Native AOT win-x64 contract'
Assert-True ($project -notmatch '<(ProjectReference|Import|Compile)\b') 'setup does not reference build tooling sources'
$forbidden = '(?i)\b(?:dotnet(?:\.exe)?|pwsh(?:\.exe)?|powershell(?:\.exe)?|vswhere(?:\.exe)?|vs_installer(?:\.exe)?|dotnet-sdk|SetupUiPrerequisites|SetupUiToolchain|DotnetSdk|BuildPrerequisites|Build-Local|dev-run)\b'
foreach ($source in Get-ChildItem -LiteralPath $setupRoot -Filter '*.cs' -File) {
    $code = Get-Content -LiteralPath $source.FullName -Raw
    # Preserve quoted tool names while ignoring explanatory C# comments.
    $code = [regex]::Replace($code, '@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|//[^\r\n]*|/\*[\s\S]*?\*/', {
        param($match)
        if ($match.Value.StartsWith('//') -or $match.Value.StartsWith('/*')) { return '' }
        $match.Value
    })
    Assert-True ($code -notmatch $forbidden) "production Setup source cannot invoke/probe development tools: $($source.Name)"
    foreach ($start in [regex]::Matches($code, 'new\s+ProcessStartInfo\s*\(([^)]*)\)')) {
        Assert-True ($source.Name -ceq 'EngineRunner.cs' -and $start.Groups[1].Value.Trim() -cin @('enginePath', 'launcher')) 'setup process launches remain the embedded engine and installed app'
    }
}
$packageSource = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts/Package.ps1') -Raw
foreach ($guard in @('Assert-SelfContainedReleaseApp -Path $published', 'Assert-NativeReleaseExecutable -Path $engineFull', 'Assert-NativeReleaseExecutable -Path $built', 'Assert-PackagedReleaseApp -PackagePath $result.FullPackage')) {
    Assert-True ($packageSource.Contains($guard)) "packaging retains dependency guard $guard"
}
Write-Host 'PASS: release PE/bundle/dependency guards, package byte identity and production Setup source boundary; no software installed.'
