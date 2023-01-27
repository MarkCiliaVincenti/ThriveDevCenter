namespace ThriveDevCenter.Server.Services;

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public interface ILocalTempFileLocks
{
    /// <summary>
    ///   Gets a temporary file path. Note that the receiver should lock the returned string while using the path
    ///   to avoid multiple places using the same temporary folder at once
    /// </summary>
    /// <param name="suffix">Suffix to add to the temporary folder</param>
    /// <returns>The final result path</returns>
    public string GetTempFilePath(string suffix);

    /// <summary>
    /// <inheritdoc cref="AsyncKeyedLocker{string}.LockAsync(string)"/>
    /// </summary>
    /// <param name="path">The file path</param>
    /// <returns>A disposable value.</returns>
    public ValueTask<IDisposable> LockAsync(string path);

    /// <summary>
    /// <inheritdoc cref="AsyncKeyedLocker{string}.LockAsync(string, CancellationToken)"/>
    /// </summary>
    /// <param name="path">The file path</param>
    /// <param name="cancellationToken">A cancellation token</param>
    /// <returns>A disposable value.</returns>
    public ValueTask<IDisposable> LockAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// <inheritdoc cref="AsyncKeyedLocker{string}.LockAsync(string, TimeSpan, CancellationToken)"/>
    /// </summary>
    /// <param name="path">The file path</param>
    /// <param name="timeout">The time limit to wait for</param>
    /// <param name="cancellationToken">A cancellation token</param>
    /// <returns>A disposable value of type <see cref="AsyncKeyedLockTimeoutReleaser{string}"/>.</returns>
    public ValueTask<AsyncKeyedLockTimeoutReleaser<string>> LockAsync(string path, TimeSpan timeout, CancellationToken cancellationToken);
}

public class LocalTempFileLocks : ILocalTempFileLocks
{
    private readonly string baseTempFilePath;
    private readonly AsyncKeyedLocker<string> asyncKeyedLocker = new(o =>
    {
        o.PoolSize = 20;
        o.PoolInitialFill = 1;
    });

    public LocalTempFileLocks(ILogger<LocalTempFileLocks> logger, IConfiguration configuration)
    {
        string path = configuration["TempFileStorage:Path"] ?? string.Empty;

        if (string.IsNullOrEmpty(path))
        {
            path = "/tmp/ThriveDevCenter";
        }

        baseTempFilePath = Path.GetFullPath(path);

        Directory.CreateDirectory(baseTempFilePath);
        logger.LogInformation("Temporary files base path: {BaseTempFilePath}", baseTempFilePath);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<IDisposable> LockAsync(string path)
    {
        return asyncKeyedLocker.LockAsync(path);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<IDisposable> LockAsync(string path, CancellationToken cancellationToken)
    {
        return asyncKeyedLocker.LockAsync(path, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<AsyncKeyedLockTimeoutReleaser<string>> LockAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return asyncKeyedLocker.LockAsync(path, timeout, cancellationToken);
    }

    public string GetTempFilePath(string suffix)
    {
        if (suffix.Length < 1 || suffix.StartsWith('/'))
            throw new ArgumentException("Path suffix is empty or starts with a slash");

        return Path.Join(baseTempFilePath, suffix);
    }
}
