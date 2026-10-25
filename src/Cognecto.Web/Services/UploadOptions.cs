namespace Cognecto.Web.Services;

/// <summary>Settings from the "Upload" section of appsettings.json.</summary>
public class UploadOptions
{
    public const string Section = "Upload";

    /// <summary>Largest accepted CSV. 20,000 rows with Hindi names is about 9.5 MB.</summary>
    public int MaxFileMegabytes { get; set; } = 20;

    /// <summary>
    /// How many uploads are parsed and saved at the same time. The rest wait their turn, so a burst
    /// from every district at once queues for a few seconds instead of exhausting server memory.
    /// </summary>
    public int MaxConcurrentUploads { get; set; } = 8;

    public long MaxFileBytes => MaxFileMegabytes * 1024L * 1024L;
}

/// <summary>Limits how many uploads are processed at once, across all requests.</summary>
public sealed class UploadGate(int maxConcurrent)
{
    private readonly SemaphoreSlim _slots = new(maxConcurrent, maxConcurrent);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        return new Slot(_slots);
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                slots.Release();
        }
    }
}
