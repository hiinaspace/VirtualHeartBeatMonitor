# VirtualHeartBeatMonitor: notes for AI assistants

This folder is written for AI coding assistants working on this repository. The top-level
README is for people.

## What the project is

A Windows app (C# / .NET 10) that estimates the user's heart rate from the motion sensors of
SteamVR devices while the user is reasonably still. It mainly uses the headset, with the
controllers and an optional chest tracker. The heart-rate number, one event per heartbeat, and
breathing (chest tracker only) are sent to VRChat over OSC. It also shows a wrist panel and a
SteamVR dashboard tab, can vibrate the controllers on each beat, and has an optional desktop
window.

The physics: each heartbeat makes the body recoil slightly (ballistocardiography). The head and
chest pick up a small 4–20 Hz vibration once per beat. The app takes the envelope of that
vibration and finds its repetition rate with an FFT over 6–12 s windows.

## Read in this order

1. [architecture.md](architecture.md): projects, data flow, threads, where things live.
2. [signal-processing.md](signal-processing.md): the heart-rate, beat-event and breathing
   algorithms, their parameters, and the measured results behind them.
3. [steamvr-and-app.md](steamvr-and-app.md): SteamVR integration (raw IMU, overlays, input,
   auto-start), OSC parameters, settings, logs.
4. [workflows.md](workflows.md): build, test, package, record, replay, and how to use a user's
   diagnostics report to support a new headset.
5. [linux.md](linux.md): Linux status, runtimes (SteamVR, Monado, OpenXR) and pose-based replay.
6. [project-log.md](project-log.md): dated history of decisions, measurements and fixes.
   Detailed; read the relevant section when you need the reasoning behind something.

## Status (2026-11-22)

- Developed and tested on: Bigscreen Beyond 1 + Valve Index controllers + Vive trackers
  (Lighthouse). Every device exposes raw IMU data there.
- **Not yet supported or tested: Steam Frame and other inside-out headsets.** Whether they expose
  raw IMU data is unknown. If they don't, a pose-based fallback (using
  `TrackedDevicePose_t.vAngularVelocity`) has to be built. See workflows.md.
- Accuracy on the owner's recordings, while still: 0.4–0.9 BPM mean difference from a
  chest-tracker reference. The number is shown 96–98% of the time sitting or standing still,
  about 40% with frequent small movements, and rarely while talking (by design it shows "--"
  then).

## Ground rules

- Change signal processing only with evidence. Run `tools/Replay` over the recordings
  (`recordings/`, not in git) and the unit tests before and after, and compare.
- `HeartBeat.Core` must stay free of OpenVR and UI code; it runs both live and on recordings.
- When the user is moving or confidence is low, show "--" and send no beats. Never show a guess.
- The owner prefers short, direct answers and keeps decisions in their own hands: propose,
  don't assume.
