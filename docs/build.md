# Build and package

[Home](../README.md) · [System map](architecture.md) · [Testing](testing.md)

## Build from source

Use the .NET 10 SDK on Windows and run these commands in PowerShell from the repository root.

```powershell
dotnet build .\Sada.csproj -c Release
dotnet run --project .\Sada.csproj -c Release
```

This command starts Sada with the tray icon and skips opening the dashboard.

```powershell
dotnet run --project .\Sada.csproj -c Release -- --tray
```

## Build a portable Windows x64 package

```powershell
dotnet publish .\Sada.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -o .\artifacts\win-x64
```

The executable is `artifacts\win-x64\Sada.exe`. For distribution, package it with [the third-party notices](../THIRD-PARTY-NOTICES.md), the `Third-party` directory, and the optional browser extension files below.

- `browser/manifest.json`
- `browser/background.js`
- `browser/watch.js`
- `browser/README.md`

See [Microsoft's single-file deployment guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) for publishing options.
