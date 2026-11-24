# HapticBridge

HapticBridge brings DualSense haptics from compatible Windows games to supported Android controllers using Apollo and Artemis. See the [project page](https://xuhk12426.github.io/HapticBridge/) for setup and compatibility information.

## Compatibility

HapticBridge should work with any Windows game that supports DualSense haptics and any Razer Kishi controller with advanced haptics that supports Audio to Haptics on Android. The tables below show what I’ve verified so far.

### Games

| Windows game | Hands-on result |
| --- | --- |
| DEATH STRANDING 2: ON THE BEACH | Game audio and haptics worked |
| Horizon Forbidden West | Game audio and haptics worked |

Both results were recorded on HapticBridge 1.3.2 using Apollo, Artemis, and the Razer Kishi V2 Pro. These games have yet to be retested on the exact 1.4.0 installer build.

### Controllers

| Controller | Haptics status |
| --- | --- |
| Razer Kishi V2 Pro | Tested through Android with Artemis and Audio to Haptics |
| Razer Kishi Ultra | Unverified |
| Razer Kishi V3 Pro | Unverified |

Controller input and haptic feedback are separate checks. If your controller or game is unlisted, use HapticBridge’s built-in haptics test to check the controller, then test audio and haptics in the game.

## Build from source

Use the .NET 10 SDK on Windows x64.

```powershell
dotnet test tests/HapticBridge.Tests -c Release
dotnet publish src/HapticBridge.UI -c Release -r win-x64 --self-contained false -o build/portable
```

Run `build/portable/HapticBridge.exe` with the Windows x64 .NET 10 Desktop Runtime. Use **Check PC setup** in HapticBridge for separately acquired dependencies. The optional custom Apollo host is a separate integration.

The installer scripts require Inno Setup 7.1.1 and the exact staged payload described by the versioned SHA-256 manifest. A fresh local publish can differ from the release payload.

First-party source is licensed under MIT. Third-party applications retain their own licenses. HapticBridge is an independent project.
