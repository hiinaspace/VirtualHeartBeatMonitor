# VirtualHeartBeatMonitor

Shows your heart rate in VR when sitting still, measured from the motion sensors in your headset and controllers.
Sends it to VRChat over OSC.

## AI Disclosure

This project has heavily relied on AI assisted coding.

## For AI assistants

[docs/for-ai](docs/for-ai/README.md).

## NOT FOR MEDICAL USE
Do not use for medicine or heath.


## What it does

- Measures your heart rate while you sit or stand still (shows "--" while you move or talk).
- Sends the BPM and every heartbeat to VRChat avatar parameters (`HR`, `isHRBeat`, …).
- Shows it on your wrist and in a "Heart rate" tab in the SteamVR dashboard.
- Optional: vibrates your controllers on each beat, measures breathing with a chest tracker.

## How Does it Work?

Ballistocardiography is the study of forces on the body created by the heart.
By analyzing the angular defection of the controllers, headset or trackers, a heart rate can be measured. 
This is done with filters, enveloping and a Fast Fourier Transform. 

TLDR; your body twiches with every heart beat, VR devices can read that and this project analyzes that to produce a heart rate.

## Accuracy 
Testing by counting beats and comparing to the number from VHBM show to be within 5 BPM. 
Using real heart rate monitors to compare is not yet done.
In testing sometimes it will report really high or really low depending on the vibration of the trackers and moving status of each.
Please do not use results for any medical use. 

## Tested Devices
1. Bigscreen Beyond 1
2. Valve Index Controllers
3. VIVE Tracker

## Install

1. Download the latest zip from Releases and unzip it anywhere.
2. Start SteamVR, then run `HeartBeatMonitor.exe`.
3. In VRChat, turn on OSC.
4. Sit still for about 10 seconds.

## Linux (experimental)

The signal processing, Probe, Replay and a headless console monitor (`tools/LiveCli`) run on
Linux with native SteamVR; the window and in-headset panels are Windows only. See
[docs/for-ai/linux.md](docs/for-ai/linux.md).

```sh
dotnet run --project tools/Probe -- check      # what raw IMU your devices expose
dotnet run --project tools/LiveCli -- --beats  # heart rate in the terminal, OSC to VRChat
```

## Diagnostics report

Click **Create diagnostics report** (window, tray menu, or the SteamVR dashboard tab) and sit
still for one minute. A zip appears on your Desktop: send it to me. It contains your device
list, one minute of motion data and the app's logs. Serial numbers are masked.

## License

MIT, see [LICENSE](LICENSE). Uses the OpenVR SDK (BSD-3-Clause, Valve Corporation), included in `lib/openvr`.
