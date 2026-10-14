# HamRxBridge

Windows 10/11 app that lets your radio steer a remote receiver, so you can listen on a quiet antenna while your own is buried in local RFI.

## What it does

- Start screen: pick KiwiSDR or WebSDR.org.
- Receiver browser: search, sort by distance from your grid square, hide full or offline Kiwis.
- KiwiSDR pro view: native spectrum, waterfall, S-meter, audio, modes, bandwidth, zoom, band buttons.
- Web view: any receiver's own page inside the app (needed for WebSDR.org, which has no open protocol).
- OmniRig CAT: Rig 1 or Rig 2 (for example IC-7300 and TS-480), any rig OmniRig supports.
- Sync modes: Off, Radio to SDR (receiver follows your dial), Two way (pro view only: click the waterfall and your radio tunes too).
- Receiver audio mutes while your radio transmits.

## Honest limits

- Receive only. This does not transmit through anything.
- WebSDR.org pages cannot report tuning back, so the web view is one way (radio to receiver) and reloads the page after you stop turning the dial.
- Remote audio has latency of roughly a second or more. Fine for monitoring and DX hunting, not for contest-speed work.
- The KiwiSDR protocol code was written from the public client protocol and tested only against a simulated server. If a receiver shows "no waterfall data", use Web view and report it.

## Install

1. Install OmniRig (dxatlas.com/omnirig) and configure Rig 1 and Rig 2 for your radios.
2. Get the Microsoft WebView2 Runtime if Windows does not already have it (needed for web view).
3. Download the `HamRxBridge-win-x64` artifact from the GitHub Actions run, unzip, run `HamRxBridge.exe`.

## Build yourself

```
cd HamRxBridge
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
