using System.Security.Cryptography;
using System.Text;
using MusicMachine.Core;

namespace MusicMachine.App;

public sealed class InstrumentHistory(IProjectStorage storage)
{
    private const string Prefix = "instrument-";
    public Task SaveAsync(Instrument instrument)
    {
        var key = Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instrument.Id)));
        return storage.WriteAsync(key, InstrumentFile.Write(instrument));
    }
    public async Task<List<Instrument>> LoadAsync()
    {
        var result = new List<Instrument>();
        foreach (var key in await storage.ListAsync(Prefix))
        {
            var data = await storage.ReadAsync(key);
            if (data is null) continue;
            try { result.Add(InstrumentFile.Read(data)); }
            catch (IOException) { /* A damaged entry must not hide other saved instruments. */ }
        }
        return result.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
