using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Agents;
using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Features.Agents;

/// <summary>
/// U2 <c>AgentHookSource</c> (I1): the <c>tasktor.agent/1</c> files in <c>agents\</c>, keyed by session id. Reads only the
/// state, the folder's last segment and the pid (never prompts or transcripts: the CLI never writes them). Files larger
/// than 64 KB or not in the contract are skipped and counted. Runs off the loop.
/// </summary>
public sealed class AgentHookSource : ITableSource<string, AgentHookFile>
{
    public const int MaxBytes = 64 * 1024;
    private readonly string _dir;

    public AgentHookSource(string dir) => _dir = dir;

    public string Name => "agent-hooks";

    public string Directory => _dir;

    public long Rejected { get; private set; }

    public void Read(ICollection<KeyValuePair<string, AgentHookFile>> into, ColumnSet columns)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (!System.IO.Directory.Exists(_dir))
        {
            return;
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxBytes)
                {
                    Rejected++;
                    continue;
                }

                var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), ContractsJsonContext.Default.AgentHookFile);
                if (file is null || file.Schema != ContractIds.Agent || file.SessionId.Length is 0 or > 200)
                {
                    Rejected++;
                    continue;
                }

                into.Add(new(file.SessionId, file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                Rejected++;
            }
        }
    }

    /// <summary>Remove one session's file (ended or expired). Off the loop.</summary>
    public void Delete(string sessionId)
    {
        try
        {
            File.Delete(Path.Combine(_dir, Storage.StoreLayout.SafeName(sessionId) + ".json"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // gone already, or the hook is writing it right now: the next read expires it again
        }
    }
}
