# MusicMachine

A music tracker and instrument editor built with Avalonia and .NET 10.

## Install

Download the installer for your platform from [Releases](https://github.com/isaiahpettingill/MusicMachine/releases/latest).

- **Windows:** run the installer.
- **Linux:** run `bash install-musicmachine.sh`. Installs for your user without sudo. Launch from the application menu or with `~/.local/bin/musicmachine`. Remove with `~/.local/bin/musicmachine-uninstall`.

Linux requires SDL2 and X11 or XWayland. On Debian/Ubuntu:

```sh
sudo apt install libsdl2-2.0-0 libx11-6 libice6 libsm6 libfontconfig1
```

## Build

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [just](https://github.com/casey/just).

```sh
just build
just run
just test
```

To build and install on Linux, install the native compiler prerequisites first:

```sh
sudo apt install clang zlib1g-dev
just install
```

Other commands:

```sh
just publish win-x64       # Run on Windows with native compiler tools installed
just install-archive path/to/MusicMachine-linux-x64.tar.gz
just uninstall
```

Without just:

```sh
dotnet build src/MusicMachine.Desktop -c Release
dotnet run --project src/MusicMachine.Desktop
dotnet test tests/MusicMachine.Tests -c Release
```

Windows source builds need SDL2.dll beside the executable for audio playback.

## Browser build

```sh
dotnet workload install wasm-tools
just browser
```

Serve `artifacts/browser/wwwroot` over HTTP.
