using System.Runtime.InteropServices.JavaScript;
using MusicMachine.App;

namespace MusicMachine.Browser;

internal sealed partial class BrowserProjectStorage : IProjectStorage
{
    // Local file picker permissions do not survive as a reopenable path. Store the clean CBOR instead.
    public bool UsesProjectSnapshots => true;
    public async Task<byte[]?> ReadAsync(string key)
    {
        var value = await Read(key);
        return value is null ? null : Convert.FromBase64String(value);
    }
    public Task WriteAsync(string key, byte[] bytes) => Write(key, Convert.ToBase64String(bytes));
    public Task DeleteAsync(string key) => Delete(key);
    public async Task<string[]> ListAsync(string prefix) => (await List(prefix)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    [JSImport("projectStorage.read", "musicmachine")]
    private static partial Task<string?> Read(string key);
    [JSImport("projectStorage.write", "musicmachine")]
    private static partial Task Write(string key, string base64);
    [JSImport("projectStorage.remove", "musicmachine")]
    private static partial Task Delete(string key);
    [JSImport("projectStorage.list", "musicmachine")]
    private static partial Task<string> List(string prefix);
}
