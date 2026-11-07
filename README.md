# Lightspeed Pair

A small Windows app that pairs a Logitech LIGHTSPEED mouse with a USB receiver, for example when you've lost the original receiver and bought a replacement.

This is an independent project. It is not made, endorsed or supported by Logitech.

## Download and run

1. Download `LightspeedPair.exe` from the [Releases](../../releases) page.
2. Double-click it. There's nothing to install, and you don't need .NET or admin rights.

Windows may show "Windows protected your PC", because the app isn't code-signed. To continue, click **More info**, then **Run anyway**. You can also build it from source yourself (see below).

## How to use it

1. Select your mouse from the list.
2. Plug in the receiver, and connect the mouse with its USB cable. The lights turn green when each one is detected.
3. Press **Pair** and follow the instructions in the pairing window. After switching the mouse on, click the left mouse button 3 times while the window is waiting.

Your mouse may stop working during pairing, so the pairing window works from the keyboard: **Enter** continues, **Esc** cancels.

### Lights

| Light | Meaning |
| --- | --- |
| Green | Detected and ready |
| Amber | The app can't check this model, but you can still try pairing |
| Red | Not found. The text next to it says what to do |

### Clearing a receiver

**Clear receiver** removes every device paired with the plugged-in receiver, so it's ready for a different mouse. It asks you to confirm first, then checks the receiver to make sure the pairings are gone. Removed devices stop working wirelessly until you pair them again.

Some receivers may refuse. If so, the app tells you that devices are still paired.

## Compatibility

The app lists only the mice named in the [listing for this replacement receiver](https://www.aliexpress.com/item/1005012759621235.html), which appears on USB as `046D:C547`.

**Only the G502 X LIGHTSPEED has been tested.** The others come from the seller's listing and may not work.

| Mouse | USB ID | Wireless ID | Status |
| --- | --- | --- | --- |
| G502 X LIGHTSPEED | C098 | 409F | Tested with C547 |
| PRO X SUPERLIGHT | C094 | 4093 | Untested |
| PRO X SUPERLIGHT 2 | C09B | 40A9 | Untested |
| PRO X SUPERLIGHT 2 DEX | not known | not known | Untested |
| PRO 2 LIGHTSPEED | not known | not known | Untested |
| G PRO Wireless | C088 | 4079 | Untested |
| G309 LIGHTSPEED | not known | not known | Untested |
| G705 | C096 | 409D | Untested |

The G900 Chaos Spectrum isn't named in the listing and isn't in the app. In testing, this receiver never detected it.

Tried one of these? Please open an issue saying which mouse you used and whether it worked. To add a mouse or an ID, edit `source/Catalog.cs`.

## Troubleshooting

- **"The receiver never saw the mouse"**: quit G HUB from the system tray, charge the mouse, keep it next to the receiver, only switch it on when the app says so, then click the left button 3 times. If it still fails, that receiver may not work with that mouse.
- **No free pairing slots**: press **Clear receiver**, or remove a device using G HUB's Device Pairing Tool, then try again.
- **Reporting a problem**: attach the log, which is at `%LOCALAPPDATA%\LightspeedPair\pairing-log.txt`.

## Build from source

The code is in the `source` folder. You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet publish source -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

The app is created at `dist\LightspeedPair.exe`.

To publish a release, push a tag such as `v1.0.0`. GitHub Actions then builds the app and attaches the `.exe` to a new release.

## Credits

The pairing protocol is documented by these open-source projects: [Solaar](https://github.com/pwr-Solaar/Solaar), [ltunify](https://git.lekensteyn.nl/ltunify/) and [OpenLogi](https://github.com/AprilNEA/OpenLogi). Device IDs come from [libratbag](https://github.com/libratbag/libratbag) and Solaar.

## License

[MIT](LICENSE). Logitech, LIGHTSPEED and product names are trademarks of their owners and are used here only to describe compatibility.
