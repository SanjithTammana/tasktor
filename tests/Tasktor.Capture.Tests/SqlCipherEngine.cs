using System.Runtime.CompilerServices;
using Tasktor.Core.Storage;

namespace Tasktor.Capture.Tests;

/// <summary>Like tasktor-capture's start: this process binds SQLite to SQLCipher before any database opens.</summary>
internal static class SqlCipherEngine
{
    [ModuleInitializer]
    internal static void Bind() => SqliteDb.UseEngine("e_sqlcipher");
}
