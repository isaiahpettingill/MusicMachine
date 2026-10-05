using Avalonia;
using MusicMachine.Desktop;

namespace MusicMachine.Tests;

public sealed class WindowsRenderingTests
{
    [Fact]
    public void NativeWindowsRenderingAvoidsDirectCompositionWithSoftwareFallback()
    {
        var options = Program.WindowsOptions();
        Assert.Equal(new[] { Win32CompositionMode.RedirectionSurface }, options.CompositionMode);
        Assert.Equal(new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software }, options.RenderingMode);
    }
}
