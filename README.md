# HapticBridge

HapticBridge brings DualSense haptics from compatible Windows games to supported Android controllers using Apollo and Artemis. See the [project page](https://xuhk12426.github.io/HapticBridge/) for setup and compatibility information.

## Build from source

Use the .NET 10 SDK on Windows x64.

```powershell
dotnet test tests/HapticBridge.Tests -c Release
dotnet publish src/HapticBridge.UI -c Release -r win-x64 --self-contained false -o build/portable
```

Run `build/portable/HapticBridge.exe` with the Windows x64 .NET 10 Desktop Runtime. Use **Check PC setup** in HapticBridge for separately acquired dependencies. The optional custom Apollo host is a separate integration.

The installer scripts require Inno Setup 7.1.1 and the exact staged payload described by the versioned SHA-256 manifest. A fresh local publish can differ from the release payload.

First-party source is licensed under MIT. Third-party applications retain their own licenses. HapticBridge is an independent project.
