# HapticBridge

Bring game-authored DualSense haptics from compatible Windows games to supported Android-connected controllers, such as the Razer Kishi V2 Pro, using Apollo and Artemis.

Instead of deriving vibrations from the ordinary game soundtrack alone, HapticBridge lets compatible games recognize an audio-capable virtual DualSense and generate their own dedicated haptic feedback. The Android controller's Audio to Haptics feature provides the physical feedback.

**[Download HapticBridge](https://github.com/Xuhk12426/HapticBridge/releases) · [Project website](https://xuhk12426.github.io/HapticBridge/)**

## How it works

### DualSense Haptics (native path, recommended)

HapticBridge 1.4.0 manages the controller relay and PC listening audio directly, replacing much of the original external-tools workflow.

- Apollo and Artemis forward controller input from Android to a virtual DualShock 4 on the PC.
- HapticBridge reads that controller's HID input and relays it directly to VIIPER, which presents an audio-capable virtual USB DualSense to Windows.
- Compatible games generate their DualSense feedback through the virtual controller. HapticBridge automatically sets the virtual DualSense audio endpoint volume to maximum.
- HapticBridge receives the DualSense speaker PCM and uses Windows WASAPI to play it through the selected PC speakers or headphones.
- Enable Audio to Haptics on the Android device to experience feedback through the supported controller.

The native path does not require Voicemeeter Banana, VB-CABLE, SoundSync, or a running DS4Windows process. The matching VIIPER backend and its USB/IP driver are still required; they are provided through the supported DS4Windows distribution.

### External tools (alternative path)

The original workflow is also available for users who need it. It uses DS4Windows with VIIPER for virtual DualSense emulation, Voicemeeter Banana and VB-CABLE for audio routing, and SoundSync to deliver the haptic audio to Android.

## Getting started

1. Download and install HapticBridge from the GitHub releases page.
2. Open **Check PC setup** and follow the prompts to install or verify the required dependencies.
3. Start Apollo, pair Artemis on your Android device, attach your controller, and enable Audio to Haptics.
4. Start a stream in Artemis. Close DS4Windows if it is running, then open **DualSense Haptics** in HapticBridge, enable haptics, and press a controller button.
5. Run the built-in haptics test before starting a compatible game.

For playing on the PC display without streaming game visuals, the optional Controller + Audio mode requires a compatible Apollo host build.

Windows 11 x64 is the primary target. A separate Windows 10 22H2 x64 compatibility test build is available. I have also verified installation, setup, and working haptics with 1.4.0 on an Intel Mac running Windows through Boot Camp.

## Compatibility

HapticBridge is intended to work with Windows games that support DualSense haptics and Razer Kishi controllers with advanced haptics and Audio to Haptics support on Android. The tables below show what I’ve verified so far; untested combinations are not guaranteed.

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
