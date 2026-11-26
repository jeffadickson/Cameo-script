namespace CameoHost;

/// <summary>Runs a sample host resource entirely on a dedicated owner thread.</summary>
public static class OwnerThread
{
    /// <summary>Acquires, uses and disposes a resource before publishing completion.
    /// This waits for synchronous disposal, not for arbitrary background producers to join.</summary>
    /// <typeparam name="TResource">Owner-thread resource.</typeparam>
    /// <typeparam name="TResult">Detached result safe to pass to the caller.</typeparam>
    /// <param name="acquire">Creates the resource on its owner.</param>
    /// <param name="work">Runs owner-affine work and returns a detached result.</param>
    /// <returns>A task representing work and cleanup, including their failures.</returns>
    public static Task<TResult> Run<TResource, TResult>(Func<TResource> acquire, Func<TResource, TResult> work)
        where TResource : IDisposable
    {
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            try
            {
                TResult result;
                using (var resource = acquire())
                {
                    result = work(resource);
                }
                // Publication is the final action: disposal failures must reach the caller,
                // and a successful await must never race synchronous resource cleanup.
                completion.TrySetResult(result);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }, 8 * 1024 * 1024) { IsBackground = true, Name = "Cameo example owner" };
        owner.Start();
        return completion.Task;
    }
}
