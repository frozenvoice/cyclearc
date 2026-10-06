#Requires -Version 7.0
# Read-only distribution guards. These inspect artifacts without starting them or probing
# the user's SDK/VS installation. Dynamic loading and real installation still need the
# disposable Windows end-to-end check; an import table alone cannot prove either.
Set-StrictMode -Version Latest

if (!('CycleArc.ReleaseDependencyReader' -as [type])) {
    Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;

namespace CycleArc {
public static class ReleaseDependencyReader {
    // PE import/delay-import layout: https://learn.microsoft.com/windows/win32/debug/pe-format
    public static string Architecture(byte[] bytes) {
        using var pe = new PEReader(new MemoryStream(bytes, false));
        return pe.PEHeaders.CoffHeader.Machine == Machine.I386 ? "win-x86" : "win-x64";
    }
    public static string[] NativeImports(byte[] bytes, bool allowX86 = false) {
        using var pe = new PEReader(new MemoryStream(bytes, false));
        var headers = pe.PEHeaders;
        bool x64 = headers.CoffHeader.Machine == Machine.Amd64 && headers.PEHeader?.Magic == PEMagic.PE32Plus;
        bool x86 = allowX86 && headers.CoffHeader.Machine == Machine.I386 && headers.PEHeader?.Magic == PEMagic.PE32;
        if (!x64 && !x86)
            throw new InvalidDataException("Release executable must be Windows x64 PE32+.");
        if (headers.PEHeader.CorHeaderTableDirectory.RelativeVirtualAddress != 0
            || headers.PEHeader.CorHeaderTableDirectory.Size != 0)
            throw new InvalidDataException("Release native executable has a managed CLR header.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadImports(pe, headers.PEHeader.ImportTableDirectory, false, names);
        ReadImports(pe, headers.PEHeader.DelayImportTableDirectory, true, names);
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ReadImports(PEReader pe, DirectoryEntry directory, bool delay, HashSet<string> names) {
        if (directory.RelativeVirtualAddress == 0 && directory.Size == 0) return;
        int stride = delay ? 32 : 20;
        if (directory.RelativeVirtualAddress <= 0 || directory.Size < stride)
            throw new InvalidDataException("Invalid PE import directory.");
        var data = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent();
        int bound = Math.Min(directory.Size, data.Length);
        for (int offset = 0; offset <= bound - stride; offset += stride) {
            var descriptor = data.AsSpan(offset, stride);
            bool empty = true;
            foreach (byte value in descriptor) if (value != 0) { empty = false; break; }
            if (empty) return;
            if (delay && BitConverter.ToUInt32(descriptor) != 1)
                throw new InvalidDataException("Unsupported PE delay import address format.");
            uint rva = BitConverter.ToUInt32(descriptor.Slice(delay ? 4 : 12));
            if (rva == 0 || rva > int.MaxValue) throw new InvalidDataException("Invalid PE import name RVA.");
            var text = pe.GetSectionData((int)rva).GetContent();
            int length = 0;
            while (length < text.Length && length < 260 && text[length] != 0) length++;
            if (length == 0 || length == text.Length || length == 260)
                throw new InvalidDataException("Invalid PE import name.");
            string name = Encoding.ASCII.GetString(text.AsSpan(0, length));
            if (name.IndexOfAny(new [] {'/', '\\', ':'}) >= 0)
                throw new InvalidDataException("PE imports must use plain DLL names.");
            names.Add(name);
        }
        throw new InvalidDataException("PE import directory has no terminator.");
    }

    public static bool HasStaticCoreClr(byte[] bytes) {
        // Windows singlefilehost links CoreCLR/JIT into the host rather than bundling
        // coreclr.dll/clrjit.dll. Check their documented exports, not arbitrary strings.
        // https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/apphost/static/singlefilehost.def
        using var pe = new PEReader(new MemoryStream(bytes, false));
        var directory = pe.PEHeaders.PEHeader.ExportTableDirectory;
        if (directory.RelativeVirtualAddress <= 0 || directory.Size < 40) return false;
        var descriptor = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, 40);
        uint count = BitConverter.ToUInt32(descriptor.AsSpan().Slice(24));
        uint table = BitConverter.ToUInt32(descriptor.AsSpan().Slice(32));
        if (count == 0 || count > 10000 || table == 0 || table > int.MaxValue)
            throw new InvalidDataException("Invalid PE export name table.");
        var pointers = pe.GetSectionData((int)table).GetContent(0, checked((int)count * 4));
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++) {
            uint rva = BitConverter.ToUInt32(pointers.AsSpan().Slice(index * 4));
            if (rva == 0 || rva > int.MaxValue) throw new InvalidDataException("Invalid PE export name RVA.");
            var text = pe.GetSectionData((int)rva).GetContent();
            int length = 0;
            while (length < text.Length && length < 260 && text[length] != 0) length++;
            if (length == 0 || length == text.Length || length == 260)
                throw new InvalidDataException("Invalid PE export name.");
            names.Add(Encoding.ASCII.GetString(text.AsSpan(0, length)));
        }
        return names.Contains("g_CLREngineMetrics") && names.Contains("CLRJitAttachState")
            && names.Contains("DotNetRuntimeInfo");
    }

    public sealed class BundleEntry {
        public long Offset, Size, CompressedSize;
        public byte Type;
        public string Name;
    }
    public sealed class Bundle {
        public string RuntimeConfig, Deps;
        public BundleEntry[] Entries;
    }

    // .NET's v6 single-file manifest is defined by the SDK host model:
    // https://github.com/dotnet/runtime/tree/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle
    public static Bundle ReadBundle(byte[] bytes) {
        NativeImports(bytes); // Also checks the apphost's architecture and absence of a CLR header.
        byte[] marker = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
        int signature = bytes.AsSpan().IndexOf(marker);
        if (signature < 8) throw new InvalidDataException("Published app has no .NET single-file bundle.");
        long header = BitConverter.ToInt64(bytes, signature - 8);
        if (header <= signature || header >= bytes.LongLength)
            throw new InvalidDataException("Published app has an invalid bundle header offset.");
        using var reader = new BinaryReader(new MemoryStream(bytes, false), Encoding.UTF8);
        reader.BaseStream.Position = header;
        uint major = reader.ReadUInt32(), minor = reader.ReadUInt32();
        if (major != 6 || minor != 0) throw new InvalidDataException("Unsupported .NET bundle version.");
        int count = reader.ReadInt32();
        if (count <= 0 || count > 10000) throw new InvalidDataException("Invalid bundle entry count.");
        reader.ReadString(); // Extraction ID.
        long depsOffset = reader.ReadInt64(), depsSize = reader.ReadInt64();
        long configOffset = reader.ReadInt64(), configSize = reader.ReadInt64();
        reader.ReadUInt64(); // Extraction flags.
        var entries = new List<BundleEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < count; index++) {
            var entry = new BundleEntry { Offset = reader.ReadInt64(), Size = reader.ReadInt64(),
                CompressedSize = reader.ReadInt64(), Type = reader.ReadByte(), Name = reader.ReadString() };
            if (string.IsNullOrWhiteSpace(entry.Name) || !paths.Add(entry.Name)
                || entry.CompressedSize < 0 || entry.Size <= 0 || entry.Offset < 0
                || entry.Offset > header || (entry.CompressedSize == 0 ? entry.Size : entry.CompressedSize) > header - entry.Offset)
                throw new InvalidDataException("Invalid or duplicate bundle entry.");
            entries.Add(entry);
        }
        string Json(long offset, long size, byte type) {
            if (size <= 0 || size > 16 * 1024 * 1024 || offset < 0 || offset > header || size > header - offset
                || !entries.Any(e => e.Type == type && e.Offset == offset && e.Size == size && e.CompressedSize == 0))
                throw new InvalidDataException("Missing or invalid embedded runtime configuration/deps.");
            return Encoding.UTF8.GetString(bytes, checked((int)offset), checked((int)size));
        }
        return new Bundle { RuntimeConfig = Json(configOffset, configSize, 4),
            Deps = Json(depsOffset, depsSize, 3), Entries = entries.ToArray() };
    }
}
}
'@
}

function Assert-NativeReleaseExecutable {
    param([Parameter(Mandatory)][string]$Path, [switch]$AllowBundledCoreClr, [switch]$AllowX86)
    $ErrorActionPreference = 'Stop'
    $bytes = [IO.File]::ReadAllBytes($Path)
    $imports = [CycleArc.ReleaseDependencyReader]::NativeImports($bytes, [bool]$AllowX86)
    if (!$AllowBundledCoreClr -and [CycleArc.ReleaseDependencyReader]::HasStaticCoreClr($bytes)) {
        throw "Native setup '$Path' is a CoreCLR host; the release wrapper must be Native AOT."
    }
    # These are inbox Windows DLLs, including the Universal CRT supplied by Windows 10+.
    # An unexpected external/VC redistributable/CLR/tool DLL fails closed for review.
    $windowsDlls = @('advapi32.dll', 'bcrypt.dll', 'combase.dll', 'comctl32.dll', 'comdlg32.dll', 'crypt32.dll', 'gdi32.dll',
        'iphlpapi.dll', 'kernel32.dll', 'msvcrt.dll', 'ncrypt.dll', 'normaliz.dll', 'ntdll.dll',
        'ole32.dll', 'oleaut32.dll', 'propsys.dll', 'psapi.dll', 'rpcrt4.dll', 'secur32.dll', 'shell32.dll', 'shlwapi.dll',
        'ucrtbase.dll', 'user32.dll', 'version.dll', 'winhttp.dll', 'winmm.dll', 'ws2_32.dll')
    foreach ($import in $imports) {
        if ($import -notin $windowsDlls -and $import -notmatch '^(api-ms-win-|ext-ms-win-)[a-z0-9-]+\.dll$') {
            throw "Native release executable '$Path' imports non-inbox dependency '$import'; bundle it or establish its runtime requirement before shipping."
        }
    }
    [pscustomobject]@{ Path = [IO.Path]::GetFullPath($Path); Architecture = [CycleArc.ReleaseDependencyReader]::Architecture($bytes); Imports = @($imports); ManagedClrHeader = $false }
}

function Assert-SelfContainedReleaseApp {
    param([Parameter(Mandatory)][string]$Path)
    $ErrorActionPreference = 'Stop'
    $bytes = [IO.File]::ReadAllBytes($Path)
    $bundle = [CycleArc.ReleaseDependencyReader]::ReadBundle($bytes)
    Assert-NativeReleaseExecutable -Path $Path -AllowBundledCoreClr | Out-Null
    $runtime = ConvertFrom-Json -InputObject $bundle.RuntimeConfig -AsHashtable -Depth 30
    $options = $runtime['runtimeOptions']
    if (!$options -or $options.ContainsKey('framework') -or $options.ContainsKey('frameworks')) {
        throw 'Published app requests an installed .NET framework; release must be self-contained.'
    }
    $included = @($options['includedFrameworks'] | ForEach-Object { $_['name'] })
    if ('Microsoft.NETCore.App' -notin $included -or 'Microsoft.WindowsDesktop.App' -notin $included) {
        throw 'Published app must include the .NET and Windows Desktop runtimes.'
    }
    $deps = ConvertFrom-Json -InputObject $bundle.Deps -AsHashtable -Depth 100
    if ([string]$deps['runtimeTarget']['name'] -notmatch '/win-x64$') {
        throw 'Published app dependencies do not target win-x64.'
    }
    if (![CycleArc.ReleaseDependencyReader]::HasStaticCoreClr($bytes)) {
        throw 'Published app host does not contain the statically linked CoreCLR/JIT runtime.'
    }
    foreach ($required in @('System.Private.CoreLib.dll', 'PresentationFramework.dll', 'PresentationNative_cor3.dll', 'wpfgfx_cor3.dll')) {
        $entry = @($bundle.Entries | Where-Object { $_.Name -ceq $required })
        $expectedType = if ($required -in @('PresentationNative_cor3.dll', 'wpfgfx_cor3.dll')) { 2 } else { 1 }
        if ($entry.Count -ne 1 -or $entry[0].Type -ne $expectedType) {
            throw "Published app does not bundle required runtime payload '$required'."
        }
    }
    [pscustomobject]@{ Path = [IO.Path]::GetFullPath($Path); Architecture = 'win-x64'; SelfContained = $true; BundledFiles = $bundle.Entries.Count }
}

function Assert-PackagedReleaseApp {
    param([Parameter(Mandatory)][string]$PackagePath, [Parameter(Mandatory)][string]$PublishedAppPath)
    $ErrorActionPreference = 'Stop'
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $apps = @($archive.Entries | Where-Object { $_.FullName -cmatch '^lib/[^/]+/CycleArc\.exe$' })
        if ($apps.Count -ne 1) { throw 'Full package must contain exactly one lib/*/CycleArc.exe.' }
        $stream = $apps[0].Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -cne (Get-FileHash -LiteralPath $PublishedAppPath -Algorithm SHA256).Hash) {
            throw 'Packaged CycleArc.exe differs from the verified self-contained publish.'
        }
    }
    finally { $archive.Dispose() }
}
