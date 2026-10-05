# Third-party notices

MusicMachine is MIT licensed; see the repository root `LICENSE`. Its releases preserve the following notices. Some notices are platform-specific; including them together does not mean every library is linked into every build.

- Avalonia 12.1.3, including its Fluent theme and Inter font loader: `Avalonia-MIT.txt`, retrieved from the [exact upstream package commit](https://github.com/AvaloniaUI/Avalonia/blob/8eeda4f6f546165b3f72e63c9f42247abb306905/licence.md)
- Catppuccin Mocha palette: `Catppuccin-MIT.txt`, from the [official palette repository](https://github.com/catppuccin/palette/blob/main/LICENSE)
- IconPacks.Avalonia.Material 2.0.0: `IconPacks-MIT.txt`, from the [exact upstream package commit](https://github.com/MahApps/IconPacks.Avalonia/blob/ff2dff06a942f015d8c1862f090644b3e1d97e78/LICENSE). Icon artwork retains `MaterialDesignIcons-LICENSE.txt` and `Apache-2.0.txt` from the [official Material Design Icons project](https://github.com/Templarian/MaterialDesign/blob/master/LICENSE)
- Inter font: `Inter-OFL.txt`, copied from the [official Inter project](https://github.com/rsms/inter/blob/master/LICENSE.txt). The font itself is covered by the SIL Open Font License, separately from Avalonia's loader code
- .NET 11.0.0-rc.1.26425.128 runtime: `DotNet-MIT.txt` and `DotNet-11-THIRD-PARTY-NOTICES.txt`, copied unchanged from the official `Microsoft.NETCore.App.Runtime.NativeAOT.linux-x64` NuGet package
- System.Formats.Cbor 10.0.0: .NET's MIT license and the package's `System.Formats.Cbor-10-THIRD-PARTY-NOTICES.txt`
- SkiaSharp 3.119.4 desktop / 4.152.1 WebAssembly: `SkiaSharp-MIT.txt` plus versioned `SkiaSharp-*-THIRD-PARTY-NOTICES.txt`, copied unchanged from the official NuGet packages
- HarfBuzzSharp 8.3.1.3 desktop / 14.2.1.201 WebAssembly: `HarfBuzzSharp-MIT.txt` plus versioned `HarfBuzzSharp-*-THIRD-PARTY-NOTICES.txt`, copied unchanged from the official NuGet packages
- ANGLE Windows natives 2.1.27548.20260419: `ANGLE-LICENSE.txt`, copied unchanged from the official Avalonia package
- MicroCom.Runtime 0.11.6: `MicroCom-MIT.txt`, from the [exact upstream package commit](https://github.com/kekekeks/MicroCom/blob/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE)
- Tmds.DBus.Protocol 0.94.1: `Tmds.DBus-MIT.txt`, from the [exact upstream package commit](https://github.com/tmds/Tmds.DBus/blob/b4a7fed0b878f74cb54f7cca84d2889af4e596ba/COPYING)
- SDL2 2.32.10 Windows x64 runtime: `SDL2-zlib.txt`; [official upstream release](https://github.com/libsdl-org/SDL/releases/tag/release-2.32.10). Linux uses the distribution's SDL2 installation
- Quite OK Audio: `QOA-MIT.txt`, also retained beside the audio implementation

The desktop packager and browser build both include this directory. When changing dependency versions, review their upstream licenses and refresh their exact package notices.

## Tracker font

`DejaVu-fonts.txt` records the installed upstream DejaVu/Bitstream Vera notice. The application embeds an ASCII/tracker-symbol subset of DejaVu Sans Mono, produced with FontTools, so the tracker does not depend on platform-installed fonts. Glyph outlines are unchanged; the family name remains DejaVu Sans Mono. Source: https://dejavu-fonts.github.io/.
