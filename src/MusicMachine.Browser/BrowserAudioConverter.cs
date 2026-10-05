using System.Runtime.InteropServices.JavaScript;

namespace MusicMachine.Browser;

/// <summary>Each conversion owns a killable worker. Canceling a request cannot cancel its successor.</summary>
internal static partial class BrowserAudioConverter
{
    private static int _nextRequest;
    public static async Task<byte[]> ConvertAsync(string name, byte[] source, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        token.ThrowIfCancellationRequested();
        if (source.Length is < 1 or > 32 * 1024 * 1024)
            throw new InvalidDataException("Choose audio of 32 MiB or less.");
        int id = checked(++_nextRequest);
        Task<string> conversion = ConvertToBase64(id, name, source);
        using var registration = token.Register(() => Cancel(id));
        try
        {
            string encoded = await conversion;
            token.ThrowIfCancellationRequested();
            if (encoded.Length > ((1_440_001 * 2 + 4096 + 2) / 3) * 4)
                throw new InvalidDataException("Converted audio exceeds the supported size.");
            return System.Convert.FromBase64String(encoded);
        }
        catch when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }

    [JSImport("conversion.convertBase64", "musicmachine")]
    private static partial Task<string> ConvertToBase64(int id, string name, byte[] bytes);

    [JSImport("conversion.cancel", "musicmachine")]
    private static partial void Cancel(int id);
}
