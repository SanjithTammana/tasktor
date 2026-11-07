using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Tasktor.Tests.Shared;

/// <summary>G-net / G-capture-net: an assembly references no networking assembly and imports no networking DLL.</summary>
internal static class NetGate
{
    public static readonly string[] NetworkDlls = ["ws2_32", "winhttp", "wininet", "urlmon", "wsock32", "httpapi", "webio", "dnsapi"];

    public static (List<string> Assemblies, List<string> Modules) References(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        var asm = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
        var mods = Enumerable.Range(1, md.GetTableRowCount(TableIndex.ModuleRef)).Select(i => md.GetString(md.GetModuleReference(MetadataTokens.ModuleReferenceHandle(i)).Name)).ToList();
        return (asm, mods);
    }

    public static void AssertNoNetwork(string path)
    {
        var (asm, mods) = References(path);
        Xunit.Assert.NotEmpty(asm);
        Xunit.Assert.DoesNotContain(asm, a => a.StartsWith("System.Net", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(mods, m => NetworkDlls.Any(n => Path.GetFileNameWithoutExtension(m).Equals(n, StringComparison.OrdinalIgnoreCase)));
    }
}
