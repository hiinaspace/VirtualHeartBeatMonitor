# Linux

Status (2026-11-22, fork branch `linux`): builds and unit tests pass on NixOS with .NET 10.
Not yet run against a live headset.

## What runs

| Part | Linux |
|---|---|
| `HeartBeat.Core`, `HeartBeat.Osc`, tests | yes, unchanged |
| `HeartBeat.Tracking` | yes; `OpenVrNative` finds `libopenvr_api.so` (below) |
| `tools/Probe`, `tools/Replay` | yes |
| `tools/LiveCli` (`heartbeat-live`) | new: the app's `LiveSession` and settings, console output and OSC, no overlays |
| `HeartBeat.App` (WinForms, D3D11 overlays) | Windows only |

`libopenvr_api.so` is looked up in `$OPENVR_API_LIB`, next to the executable,
`$VR_OVERRIDE/bin/linux64`, then `~/.local/share/Steam/steamapps/common/SteamVR/bin/linux64`
(and `~/.steam/steam`, `~/.steam/root`). The OpenVR client then picks the runtime from
`~/.config/openvr/openvrpaths.vrpath`, so that file must point at SteamVR, not xrizer or OpenComposite.

The C# `ImuSample_t` (64 bytes) matches Linux: `openvr.h` declares it under `pack(8)`, unlike
`VREvent_t` and the controller state, which the binding already handles.

## Runtimes

- **SteamVR (native Linux)**: the lighthouse driver serves `/devices/lighthouse/<serial>/imu`
  as on Windows (confirmed 2026-11-22 on a Beyond 2: HMD 1014 Hz, Index and Tundra 250 Hz).
  `vrclient.so` needs libuuid, libGL and X11 from outside the Steam runtime, or init fails with
  `Init_VRClientDLLNotFound (102)` on NixOS: run tools through `tools/linux/vr`
  (`tools/linux/vr Probe check`).
- **Monado with its `steamvr_lh` driver**: the lighthouse driver runs inside Monado, but Monado's
  `IVRIOBuffer` (`src/xrt/drivers/steamvr_lh/interfaces/iobuffer.cpp`) is an empty stub, so the
  raw IMU the driver writes is dropped and no client can read it. A patch there is the way to
  get raw IMU under Monado.
- **Monado poses over OpenXR**: `tools/linux/tap` (`tools/xr_pose_tap.py`) opens an
  `XR_EXTX_overlay` session that submits no layers, so the running game keeps the headset, and
  logs every device via `XR_MNDX_xdev_space` (chest tracker included, no actions needed) in
  the `poses_*.csv` format Probe writes. It writes three folders: frame-predicted (90 Hz,
  predictedDisplayTime), frame-now (90 Hz) and poll-now (1 kHz). Replay with `--source poses`;
  with no `imu_*.csv`, the reference falls back to the tracker poses (no longer independent).
  First run, 2026-11-22: the three modes gave the same numbers, and the chest tracker
  peak tracked the pulse-ox (84-88 BPM) at prominence about 64.
- **OpenXR in general** exposes poses and velocities only. `Replay --source poses --resample 90`
  estimates what that costs, using the pose stream Probe records next to the raw IMU.

## Replay on pose data

- `--source poses`: fused devices are fed from `poses_*.csv` (angular velocity rotated into the
  device frame; acceleration from the change in velocity plus gravity). The reference tracker
  still comes from its raw IMU.
- `--resample HZ`: latest sample at each tick of a fixed clock, no anti-alias filter.
  Raw IMU picked at 90 Hz this way folds the 1 kHz noise into the heart band; on synthetic data
  the estimator then mostly abstains ("--").

## Recordings loader

`src/HeartBeat.Core/Recordings/Recording.cs` was missing from the upstream repository: the
`.gitignore` line `recordings/` also matched `src/HeartBeat.Core/Recordings/` on Windows, where
git ignores case. The ignore patterns are now anchored to the repository root. This loader is a
rewrite from `tools/Replay` and `tools/Probe/RecordCommand.cs`, not the original file.
