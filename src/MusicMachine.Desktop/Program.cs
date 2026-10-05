using Avalonia;
using MusicMachine.Core;
using MusicMachine.Audio;
using MusicMachine.App.Updating;
namespace MusicMachine.Desktop;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] is "--apply-update" or "--resume-update")
            {
                if (args.Length != 2) throw new ArgumentException("The update command requires one plan file.");
                if (args[0] == "--apply-update") return UpdateInstaller.RunHelper(args[1]);
                UpdateHost.ResumePlan = Path.GetFullPath(args[1]);
                args = [];
            }
            if (args.Length == 1 && args[0] is "--help" or "-h") { Console.WriteLine("MusicMachine [file.song] | --render-demo file.wav | --demo-song file.song | --export file.song output.wav|output.qoa|output.flac"); return 0; }
            if (args.Length == 2 && args[0] == "--demo-song") { SongFile.Save(args[1], DemoSong.Create()); return 0; }
            if (args.Length == 2 && args[0] == "--render-demo") { OfflineExporter.WriteWav(args[1], DemoSong.Create()); return 0; }
            if (args.Length == 3 && args[0] == "--export")
            {
                var song = SongFile.Load(args[1]);
                if (Path.GetExtension(args[2]).Equals(".qoa", StringComparison.OrdinalIgnoreCase)) OfflineExporter.WriteQoa(args[2], song);
                else if (Path.GetExtension(args[2]).Equals(".wav", StringComparison.OrdinalIgnoreCase)) OfflineExporter.WriteWav(args[2], song);
                else if (Path.GetExtension(args[2]).Equals(".flac", StringComparison.OrdinalIgnoreCase)) OfflineExporter.WriteFlac(args[2], song);
                else throw new ArgumentException("Headless export supports .wav, .qoa or .flac destinations.");
                return 0;
            }
            UpdateHost.DesktopEnabled = true;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<MusicMachine.App.App>().UsePlatformDetect().With(new X11PlatformOptions { WmClass = "MusicMachine" }).WithInterFont().LogToTrace();
}
