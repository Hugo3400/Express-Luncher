namespace ExpressLuncher.Core.Updates;

// STUB provisoire : à remplacer par la vraie implémentation.
public sealed class GitHubUpdateService : IUpdateService
{
    public Version CurrentVersion => AppInfo.CurrentVersion;

    public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<UpdateInfo?>(null);

    public Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public void ApplyAndRestart(string packagePath) => throw new NotImplementedException();
}
