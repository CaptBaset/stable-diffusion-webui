# HamRxBridge  By AP2FLY

Windows 10/11 app: listen on a quiet antenna anywhere in the world, steered by your own radio. Built for operators whose home receive is buried in local RFI.

## What it does

- **KiwiSDR pro view**: our own colourful spectrum, waterfall, S-meter and audio (native connection, compressed audio).
- **WebSDR live control**: the receiver's own page inside the app, tuned live by this app's panel and by your radio, and read back when you tune on the page.
- **KiwiSDR web page** option, also under live control.
- **One control panel for all**: SDRuno style VFO (mouse wheel on any digit), colour coded modes, filter presets, steps, band buttons, S-meter.
- **Radio control**: OmniRig (Rig 1 or 2), or built in CAT with no extra software: Icom CI-V (IC-7300, IC-705, IC-7610 ...) and Kenwood/Yaesu (TS-480, TS-590, FT-991 ...). Auto-detect included.
- **Sync**: Off, Radio to SDR, or Two way. Receiver mutes while you transmit.
- **Receiver browser**: 800+ KiwiSDRs, distance from your grid square, favourites, offline cache.

## Safety

The built in CAT only reads the radio and sets frequency and mode. It never sends a transmit command. RTS and DTR are held low (except Kenwood RTS/CTS flow control when you enable it).

## Install

Download the zip from the latest release, unzip, run `HamRxBridge.exe`. No installer.
Needs the Microsoft WebView2 Runtime for web pages (already on most Windows 10/11 PCs).

## Verified so far

- Kiwi protocol checked against the official kiwiclient and KiwiSDR server source, and tested against a strict simulated server (paths, handshake order, waterfall maths, compressed audio identical to the reference decoder).
- Direct CAT tested against simulated IC-7300 (CI-V with echo and transceive) and TS-480 (IF/FA/MD) radios.
- Not yet tested on real hardware or live receivers. Logs are in `%APPDATA%\HamRxBridge\log.txt`.
