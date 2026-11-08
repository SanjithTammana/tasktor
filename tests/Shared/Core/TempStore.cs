using Tasktor.Core.Storage;

namespace Tasktor.Tests.Shared;

/// <summary>A throwaway store root under the temp folder.</summary>
internal sealed class TempStore : IDisposable
{
    public TempStore()
    {
        Root = Path.Combine(Path.GetTempPath(), "tasktor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Layout = new StoreLayout(Root);
    }

    public string Root { get; }

    public StoreLayout Layout { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
