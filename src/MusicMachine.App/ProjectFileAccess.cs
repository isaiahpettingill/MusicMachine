using Avalonia.Platform.Storage;

namespace MusicMachine.App;

// Host-neutral picker boundary. A browser selection supplies streams, not a durable filesystem grant.
public sealed record ProjectFileSelection(string Name, string? LocalPath, Func<Task<Stream>> OpenReadAsync, Func<Task<Stream>> OpenWriteAsync)
{
    internal static ProjectFileSelection From(IStorageFile file) => new(file.Name, file.TryGetLocalPath(), file.OpenReadAsync, file.OpenWriteAsync);
}
public sealed record ProjectFileAccess(Func<Task<ProjectFileSelection?>> OpenAsync, Func<string, Task<ProjectFileSelection?>> SaveAsync);
