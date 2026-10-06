set shell := ["bash", "-eu", "-o", "pipefail", "-c"]

# List available commands.
default:
    @just --list

# Build the desktop application.
build:
    dotnet build src/MusicMachine.Desktop -c Release

# Run the desktop application.
run:
    dotnet run --project src/MusicMachine.Desktop

# Run the .NET tests.
test:
    dotnet test tests/MusicMachine.Tests -c Release

# Publish a standalone native application (requires native compiler tools).
publish rid="linux-x64":
    dotnet publish src/MusicMachine.Desktop -c Release -r "{{rid}}" -o "artifacts/{{rid}}"

# Build and install for the current Linux user.
install: (publish "linux-x64")
    install -m 644 assets/icon/musicmachine.svg artifacts/linux-x64/musicmachine.svg
    tar -czf artifacts/MusicMachine-linux-x64.tar.gz -C artifacts/linux-x64 .
    bash tools/install-linux.sh --archive artifacts/MusicMachine-linux-x64.tar.gz

# Install an existing Linux release archive.
install-archive archive:
    bash tools/install-linux.sh --archive "{{archive}}"

# Remove the current user's Linux installation; songs are retained.
uninstall:
    bash tools/install-linux.sh --uninstall

# Build the browser application (install wasm-tools first).
browser:
    dotnet publish src/MusicMachine.Browser -c Release -o artifacts/browser
