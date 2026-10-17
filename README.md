# Junction

Mod manager and performance tracker for Transport Fever 3.

Download the latest `Junction.exe` from the Releases page and run it. Once installed, Junction updates itself when a new version is out.

## Building from source

Needs the .NET 10 SDK.

- Run with sample mods: `dotnet run --project src/Tfmm.App -- --demo`
- Tests: `dotnet test Junction.slnx`
- Windows exe: `dotnet publish src/Tfmm.App -c Release -r win-x64 -o publish` (single `Junction.exe`)

Every push to `main` that changes the app builds and publishes a new release automatically.
