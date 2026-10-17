# Feevr

Mod manager and performance tracker for Transport Fever 3.

Download the latest `Feevr.exe` from the Releases page and run it. Once installed, Feevr updates itself when a new version is out.

## Building from source

Needs the .NET 10 SDK.

- Run with sample mods: `dotnet run --project src/Tfmm.App -- --demo`
- Tests: `dotnet test Feevr.slnx`
- Windows exe: `dotnet publish src/Tfmm.App -c Release -r win-x64 -o publish` (single `Feevr.exe`)

Every push to `main` that changes the app builds and publishes a new release automatically.
