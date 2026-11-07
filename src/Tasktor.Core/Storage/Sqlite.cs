using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Tasktor.Core.Storage;

/// <summary>A SQLite error with its result code (never the SQL text's data).</summary>
public sealed class SqliteException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// A minimal SQLite connection over the C API (D-C9): Windows' own <c>winsqlite3.dll</c> on Windows, the system
/// <c>libsqlite3</c> elsewhere (Linux CI uses the real engine). No package, no reflection: Native AOT safe. One connection
/// per thread that owns it (the store writer owns the write side).
/// </summary>
public sealed partial class SqliteDb : IDisposable
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int OpenReadOnly = 0x1;
    private const int OpenReadWrite = 0x2;
    private const int OpenCreate = 0x4;
    private const int OpenFullMutex = 0x10000;
    private static readonly nint Transient = -1;
    private nint _db;

    static SqliteDb() => NativeLibrary.SetDllImportResolver(typeof(SqliteDb).Assembly, Resolve);

    private SqliteDb(nint db) => _db = db;

    private static string? s_engine;
    private static volatile bool s_resolved;

    /// <summary>
    /// Use another SQLite build for this whole process, e.g. SQLCipher (<c>e_sqlcipher</c>) in the capture process for its
    /// encrypted index (C OQ-C22). Call before the first database opens; the native library is bound once per process.
    /// If it cannot be loaded the default engine is used, and <see cref="Open"/> with a key then refuses (fails closed).
    /// </summary>
    public static void UseEngine(string libraryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryName);
        if (s_resolved && s_engine != libraryName)
        {
            throw new InvalidOperationException("SQLite is already bound in this process");
        }

        s_engine = libraryName;
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != "sqlite3")
        {
            return 0;
        }

        s_resolved = true;
        if (s_engine is { } engine && NativeLibrary.TryLoad(engine, assembly, path, out var chosen))
        {
            return chosen;
        }

        foreach (var candidate in OperatingSystem.IsWindows() ? new[] { "winsqlite3.dll", "sqlite3.dll" } : OperatingSystem.IsMacOS() ? ["libsqlite3.dylib"] : ["libsqlite3.so.0", "libsqlite3.so"])
        {
            if (NativeLibrary.TryLoad(candidate, assembly, path, out var h))
            {
                return h;
            }
        }

        return 0;
    }

    /// <param name="key">
    /// A 256-bit database key: the engine must encrypt pages with it (SQLCipher's <c>PRAGMA key</c>, set before anything
    /// else). An engine without a codec throws <see cref="NotSupportedException"/> before a byte is written, so a store
    /// that needs encryption never lands on disk in plain text.
    /// </param>
    public static SqliteDb Open(string path, bool readOnly = false, byte[]? key = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!readOnly && path != ":memory:")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }

        var flags = (readOnly ? OpenReadOnly : OpenReadWrite | OpenCreate) | OpenFullMutex;
        var rc = sqlite3_open_v2(Utf8(path), out var db, flags, 0);
        if (rc != SqliteOk)
        {
            var msg = db != 0 ? Message(db) : "cannot open";
            sqlite3_close_v2(db);
            throw new SqliteException(rc, msg);
        }

        var conn = new SqliteDb(db);
        try
        {
            if (key is not null)
            {
                if (key.Length != 32)
                {
                    throw new ArgumentException("the key is 32 bytes", nameof(key));
                }

                conn.Exec("PRAGMA key = \"x'" + Convert.ToHexString(key) + "'\";");
                string? codec;
                using (var probe = conn.Prepare("PRAGMA cipher_version;"))
                {
                    codec = probe.Step() ? probe.Text(0) : null; // an engine without a codec ignores both pragmas
                }

                if (string.IsNullOrEmpty(codec))
                {
                    throw new NotSupportedException("this SQLite engine cannot encrypt");
                }
            }

            if (!readOnly && path != ":memory:")
            {
                conn.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;"); // a wrong key fails here
            }

            conn.Exec("PRAGMA cache_size=-256;"); // ≤ 256 KB page cache (C's memory layout)
            return conn;
        }
        catch
        {
            conn.Dispose(); // a failed open never leaves the file held (Windows cannot delete or reopen it otherwise)
            throw;
        }
    }

    public long LastInsertRowId => sqlite3_last_insert_rowid(_db);

    public int Changes => sqlite3_changes(_db);

    public void Exec(string sql)
    {
        var rc = sqlite3_exec(_db, Utf8(sql), 0, 0, out var err);
        if (rc != SqliteOk)
        {
            var msg = err != 0 ? Marshal.PtrToStringUTF8(err) ?? "error" : Message(_db);
            if (err != 0)
            {
                sqlite3_free(err);
            }

            throw new SqliteException(rc, msg);
        }
    }

    public SqliteStatement Prepare(string sql)
    {
        var rc = sqlite3_prepare_v2(_db, Utf8(sql), -1, out var stmt, 0);
        if (rc != SqliteOk)
        {
            throw new SqliteException(rc, Message(_db));
        }

        return new SqliteStatement(this, stmt);
    }

    /// <summary>Run <paramref name="work"/> in one transaction; rolls back on an exception.</summary>
    public void Transaction(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Exec("BEGIN IMMEDIATE");
        try
        {
            work();
            Exec("COMMIT");
        }
        catch
        {
            Exec("ROLLBACK");
            throw;
        }
    }

    public long Scalar(string sql, params object?[] args)
    {
        using var s = Prepare(sql);
        s.BindAll(args);
        return s.Step() ? s.Int64(0) : 0;
    }

    public void Run(string sql, params object?[] args)
    {
        using var s = Prepare(sql);
        s.BindAll(args);
        s.Step();
    }

    public void Dispose()
    {
        if (_db != 0)
        {
            sqlite3_close_v2(_db);
            _db = 0;
        }
    }

    internal void Check(int rc)
    {
        if (rc != SqliteOk && rc != SqliteRow && rc != SqliteDone)
        {
            throw new SqliteException(rc, Message(_db));
        }
    }

    private static string Message(nint db) => Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "error";

    internal static byte[] Utf8(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }

    [LibraryImport("sqlite3")]
    private static partial int sqlite3_open_v2(byte[] filename, out nint db, int flags, nint vfs);

    [LibraryImport("sqlite3")]
    private static partial int sqlite3_close_v2(nint db);

    [LibraryImport("sqlite3")]
    private static partial int sqlite3_exec(nint db, byte[] sql, nint callback, nint arg, out nint errmsg);

    [LibraryImport("sqlite3")]
    private static partial void sqlite3_free(nint p);

    [LibraryImport("sqlite3")]
    private static partial nint sqlite3_errmsg(nint db);

    [LibraryImport("sqlite3")]
    private static partial int sqlite3_prepare_v2(nint db, byte[] sql, int bytes, out nint stmt, nint tail);

    [LibraryImport("sqlite3")]
    private static partial long sqlite3_last_insert_rowid(nint db);

    [LibraryImport("sqlite3")]
    private static partial int sqlite3_changes(nint db);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_step(nint stmt);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_reset(nint stmt);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_clear_bindings(nint stmt);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_finalize(nint stmt);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_bind_int64(nint stmt, int index, long value);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_bind_double(nint stmt, int index, double value);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_bind_text(nint stmt, int index, byte[] value, int bytes, nint destructor);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_bind_blob(nint stmt, int index, byte[] value, int bytes, nint destructor);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_bind_null(nint stmt, int index);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_column_type(nint stmt, int col);

    [LibraryImport("sqlite3")]
    internal static partial long sqlite3_column_int64(nint stmt, int col);

    [LibraryImport("sqlite3")]
    internal static partial double sqlite3_column_double(nint stmt, int col);

    [LibraryImport("sqlite3")]
    internal static partial nint sqlite3_column_text(nint stmt, int col);

    [LibraryImport("sqlite3")]
    internal static partial nint sqlite3_column_blob(nint stmt, int col);

    [LibraryImport("sqlite3")]
    internal static partial int sqlite3_column_bytes(nint stmt, int col);

    internal static nint TransientDestructor => Transient;
}

/// <summary>A prepared statement: bind (1-based), step, read columns (0-based), reset for reuse.</summary>
public sealed class SqliteStatement : IDisposable
{
    private readonly SqliteDb _db;
    private nint _stmt;

    internal SqliteStatement(SqliteDb db, nint stmt)
    {
        _db = db;
        _stmt = stmt;
    }

    public SqliteStatement Bind(int i, object? value)
    {
        var rc = value switch
        {
            null => SqliteDb.sqlite3_bind_null(_stmt, i),
            long l => SqliteDb.sqlite3_bind_int64(_stmt, i, l),
            int n => SqliteDb.sqlite3_bind_int64(_stmt, i, n),
            uint u => SqliteDb.sqlite3_bind_int64(_stmt, i, u),
            bool b => SqliteDb.sqlite3_bind_int64(_stmt, i, b ? 1 : 0),
            double d => SqliteDb.sqlite3_bind_double(_stmt, i, d),
            float f => SqliteDb.sqlite3_bind_double(_stmt, i, f),
            string s => BindText(i, s),
            byte[] bytes => SqliteDb.sqlite3_bind_blob(_stmt, i, bytes, bytes.Length, SqliteDb.TransientDestructor),
            _ => throw new ArgumentException("Unsupported SQLite value type " + value.GetType().Name),
        };
        _db.Check(rc);
        return this;
    }

    private int BindText(int i, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        return SqliteDb.sqlite3_bind_text(_stmt, i, bytes, bytes.Length, SqliteDb.TransientDestructor);
    }

    public SqliteStatement BindAll(params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        for (var i = 0; i < values.Length; i++)
        {
            Bind(i + 1, values[i]);
        }

        return this;
    }

    /// <summary>True when a row is available; false when done.</summary>
    public bool Step()
    {
        var rc = SqliteDb.sqlite3_step(_stmt);
        _db.Check(rc);
        return rc == 100;
    }

    /// <summary>Step to completion, then reset and clear bindings for the next use.</summary>
    public void Execute()
    {
        while (Step())
        {
        }

        Reset();
    }

    public void Reset()
    {
        SqliteDb.sqlite3_reset(_stmt);
        SqliteDb.sqlite3_clear_bindings(_stmt);
    }

    public bool IsNull(int col) => SqliteDb.sqlite3_column_type(_stmt, col) == 5;

    public long Int64(int col) => SqliteDb.sqlite3_column_int64(_stmt, col);

    public double Double(int col) => SqliteDb.sqlite3_column_double(_stmt, col);

    public string? Text(int col)
    {
        var p = SqliteDb.sqlite3_column_text(_stmt, col);
        return p == 0 ? null : Marshal.PtrToStringUTF8(p, SqliteDb.sqlite3_column_bytes(_stmt, col));
    }

    public byte[]? Blob(int col)
    {
        var p = SqliteDb.sqlite3_column_blob(_stmt, col);
        if (p == 0)
        {
            return IsNull(col) ? null : [];
        }

        var bytes = new byte[SqliteDb.sqlite3_column_bytes(_stmt, col)];
        Marshal.Copy(p, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        if (_stmt != 0)
        {
            SqliteDb.sqlite3_finalize(_stmt);
            _stmt = 0;
        }
    }
}
