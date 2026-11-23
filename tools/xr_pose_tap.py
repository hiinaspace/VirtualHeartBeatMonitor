"""Headless-ish OpenXR pose tap for Monado: an EXTX_overlay session that submits no
layers (VRChat keeps the headset) and logs every Monado xdev's pose + velocity via
XR_MNDX_xdev_space, in Probe's poses_<i>_<Kind>.csv format so tools/Replay can read it.

Writes three recordings side by side, to separate what the app sees from what is sampled:
  <out>-frame-predicted  locate at the frame's predictedDisplayTime (what a game sees), ~90 Hz
  <out>-frame-now        locate at "now" once per frame, ~90 Hz
  <out>-poll-now         locate at "now" from a 1000 Hz poll thread

Run: uv run --with pyopenxr --with glfw --with PyOpenGL python -I tools/xr_pose_tap.py --seconds 120
"""
import argparse
import ctypes
import json
import os
import sys
import threading
import time
from ctypes import POINTER, Structure, byref, c_char, c_int, c_uint32, c_uint64, c_void_p
from datetime import datetime, timezone
from pathlib import Path

import glfw
import xr

# ---- XR_MNDX_xdev_space (Monado preview header; not in pyopenxr) ----
XDEV_EXT = "XR_MNDX_xdev_space"
TYPE_CREATE_XDEV_LIST_INFO = 1000444002
TYPE_GET_XDEV_INFO = 1000444003
TYPE_XDEV_PROPERTIES = 1000444004
TYPE_CREATE_XDEV_SPACE_INFO = 1000444005


class CreateXDevListInfo(Structure):
    _fields_ = [("type", c_int), ("next", c_void_p)]


class GetXDevInfo(Structure):
    _fields_ = [("type", c_int), ("next", c_void_p), ("id", c_uint64)]


class XDevProperties(Structure):
    _fields_ = [("type", c_int), ("next", c_void_p), ("name", c_char * 256),
                ("serial", c_char * 256), ("canCreateSpace", c_uint32)]


class CreateXDevSpaceInfo(Structure):
    _fields_ = [("type", c_int), ("next", c_void_p), ("xdevList", c_uint64),
                ("id", c_uint64), ("offset", xr.Posef)]


def proc(instance, name, restype, *argtypes):
    fn = xr.PFN_xrVoidFunction()
    xr.raw_functions.xrGetInstanceProcAddr(instance, name.encode(), byref(fn))
    return ctypes.CFUNCTYPE(restype, *argtypes)(ctypes.cast(fn, c_void_p).value)


def check(res, what):
    if res != 0:
        raise RuntimeError(f"{what} failed: XrResult {res}")


# ---- GLX binding from a hidden GLFW window ----
GLX_FBCONFIG_ID = 0x8013


def glx_binding():
    glfw.init_hint(glfw.PLATFORM, glfw.PLATFORM_X11)
    if not glfw.init():
        raise RuntimeError("glfw init failed (needs DISPLAY / Xwayland)")
    glfw.window_hint(glfw.VISIBLE, glfw.FALSE)
    win = glfw.create_window(64, 64, "xr_pose_tap", None, None)
    glfw.make_context_current(win)
    libgl = ctypes.CDLL("libGLX.so.0")
    dpy = glfw.get_x11_display()
    ctx = glfw.get_glx_context(win)
    libgl.glXGetCurrentDrawable.restype = ctypes.c_ulong
    drawable = libgl.glXGetCurrentDrawable()
    fbid = c_int()
    libgl.glXQueryContext.argtypes = [c_void_p, c_void_p, c_int, POINTER(c_int)]
    libgl.glXQueryContext(dpy, ctx, GLX_FBCONFIG_ID, byref(fbid))
    n = c_int()
    attrs = (c_int * 3)(GLX_FBCONFIG_ID, fbid.value, 0)
    libgl.glXChooseFBConfig.restype = POINTER(c_void_p)
    libgl.glXChooseFBConfig.argtypes = [c_void_p, c_int, POINTER(c_int), POINTER(c_int)]
    cfgs = libgl.glXChooseFBConfig(dpy, 0, attrs, byref(n))
    if not n.value:
        raise RuntimeError("no GLX FBConfig for the context")
    fbconfig = cfgs[0]

    class XVisualInfo(Structure):
        _fields_ = [("visual", c_void_p), ("visualid", ctypes.c_ulong)]
    libgl.glXGetVisualFromFBConfig.restype = POINTER(XVisualInfo)
    libgl.glXGetVisualFromFBConfig.argtypes = [c_void_p, c_void_p]
    vis = libgl.glXGetVisualFromFBConfig(dpy, fbconfig).contents
    F = dict(xr.GraphicsBindingOpenGLXlibKHR._fields_)
    return win, xr.GraphicsBindingOpenGLXlibKHR(
        x_display=ctypes.cast(dpy, F["x_display"]),
        visualid=vis.visualid,
        glx_fbconfig=ctypes.cast(fbconfig, F["glx_fbconfig"]),
        glx_drawable=drawable,
        glx_context=ctypes.cast(ctx, F["glx_context"]),
    )


KINDS_BY_SERIAL = {}  # filled from --kinds or name heuristics


def kind_for(name, serial):
    if serial in KINDS_BY_SERIAL:
        return KINDS_BY_SERIAL[serial]
    low = name.lower()
    if "hmd" in low or "beyond" in low or "headset" in low:
        return "HMD"
    if "left" in low:
        return "Controller-L"
    if "right" in low:
        return "Controller-R"
    if "tracker" in low:
        return "Tracker"
    return None


class Sink:
    """One recording folder: a poses CSV per device."""

    def __init__(self, folder, devices):
        self.folder = folder
        folder.mkdir(parents=True, exist_ok=True)
        self.files = {}
        for i, d in enumerate(devices):
            f = (folder / f"poses_{i}_{d['kind']}.csv").open("w")
            f.write("host_t,valid,result,px,py,pz,qw,qx,qy,qz,vx,vy,vz,wx,wy,wz\n")
            self.files[d["serial"]] = f
        self.rows = 0

    def write(self, serial, t, loc, vel):
        lf, vf = loc.location_flags, vel.velocity_flags
        ok = bool(lf & xr.SPACE_LOCATION_ORIENTATION_VALID_BIT) and bool(lf & xr.SPACE_LOCATION_POSITION_VALID_BIT)
        p, q, v, w = loc.pose.position, loc.pose.orientation, vel.linear_velocity, vel.angular_velocity
        if not (vf & xr.SPACE_VELOCITY_ANGULAR_VALID_BIT):
            ok = False
        self.files[serial].write(
            f"{t:.6f},{int(ok)},{200 if ok else 1},{p.x:.9g},{p.y:.9g},{p.z:.9g},"
            f"{q.w:.9g},{q.x:.9g},{q.y:.9g},{q.z:.9g},{v.x:.9g},{v.y:.9g},{v.z:.9g},"
            f"{w.x:.9g},{w.y:.9g},{w.z:.9g}\n")
        self.rows += 1

    def close(self, meta):
        for f in self.files.values():
            f.close()
        (self.folder / "session.json").write_text(json.dumps(meta, indent=2) + "\n")


class StreamSink:
    """Live mode: one line per valid pose on stdout for LiveCli --poses-stdin
    (serial,kind,t,qw,qx,qy,qz,vx,vy,vz,wx,wy,wz)."""

    def __init__(self, devices):
        self.kinds = {d["serial"]: d["kind"] for d in devices}
        self.rows = 0

    def write(self, serial, t, loc, vel):
        lf, vf = loc.location_flags, vel.velocity_flags
        if not (lf & xr.SPACE_LOCATION_ORIENTATION_VALID_BIT and vf & xr.SPACE_VELOCITY_ANGULAR_VALID_BIT
                and vf & xr.SPACE_VELOCITY_LINEAR_VALID_BIT):
            return
        q, v, w = loc.pose.orientation, vel.linear_velocity, vel.angular_velocity
        sys.stdout.write(f"{serial},{self.kinds[serial]},{t:.6f},{q.w:.9g},{q.x:.9g},{q.y:.9g},{q.z:.9g},"
                         f"{v.x:.9g},{v.y:.9g},{v.z:.9g},{w.x:.9g},{w.y:.9g},{w.z:.9g}\n")
        self.rows += 1

    def close(self, meta):
        sys.stdout.flush()


def ask_pulse(when):
    try:
        s = input(f"Pulse-ox reading {when} (BPM, Enter to skip): ").strip()
        return float(s) if s else None
    except (EOFError, ValueError):
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=120)
    ap.add_argument("--out", default=None, help="folder prefix (default recordings/<stamp>_<label>)")
    ap.add_argument("--label", default="monado-seated")
    ap.add_argument("--note", default="")
    ap.add_argument("--poll-hz", type=float, default=1000)
    ap.add_argument("--kinds", default="", help="SERIAL=Kind,... overrides")
    ap.add_argument("--list", action="store_true", help="list xdevs and exit")
    ap.add_argument("--no-pulse", action="store_true")
    ap.add_argument("--stream", action="store_true",
                    help="no files: print frame-time poses to stdout until killed (pipe into LiveCli --poses-stdin)")
    a = ap.parse_args()
    if a.stream:
        a.no_pulse = True
        a.seconds = float("inf")
        global print
        _print = print
        print = lambda *x, **k: _print(*x, file=sys.stderr, **k)
    for kv in filter(None, a.kinds.split(",")):
        s, k = kv.split("=")
        KINDS_BY_SERIAL[s] = k

    exts = [xr.KHR_OPENGL_ENABLE_EXTENSION_NAME, xr.EXTX_OVERLAY_EXTENSION_NAME, XDEV_EXT,
            xr.KHR_CONVERT_TIMESPEC_TIME_EXTENSION_NAME]
    instance = xr.create_instance(xr.InstanceCreateInfo(
        application_info=xr.ApplicationInfo(application_name="vhbm_pose_tap", engine_name="none",
                                            api_version=xr.Version(1, 0, 0)),
        enabled_extension_names=exts))
    system = xr.get_system(instance, xr.SystemGetInfo(form_factor=xr.FormFactor.HEAD_MOUNTED_DISPLAY))

    xr.get_opengl_graphics_requirements_khr(instance, system)
    win, binding = glx_binding()
    overlay = xr.SessionCreateInfoOverlayEXTX(create_flags=0, session_layers_placement=1)
    binding.next = ctypes.cast(byref(overlay), c_void_p)
    session = xr.create_session(instance, xr.SessionCreateInfo(
        system_id=system, next=ctypes.cast(byref(binding), c_void_p)))

    stage = xr.create_reference_space(session, xr.ReferenceSpaceCreateInfo(
        reference_space_type=xr.ReferenceSpaceType.STAGE, pose_in_reference_space=xr.Posef()))

    # xdev enumeration
    create_list = proc(instance, "xrCreateXDevListMNDX", c_int, c_void_p, c_void_p, POINTER(c_uint64))
    enum_xdevs = proc(instance, "xrEnumerateXDevsMNDX", c_int, c_uint64, c_uint32, POINTER(c_uint32), POINTER(c_uint64))
    get_props = proc(instance, "xrGetXDevPropertiesMNDX", c_int, c_uint64, c_void_p, c_void_p)
    create_space = proc(instance, "xrCreateXDevSpaceMNDX", c_int, c_void_p, c_void_p, POINTER(c_uint64))
    xlist = c_uint64()
    info = CreateXDevListInfo(TYPE_CREATE_XDEV_LIST_INFO, None)
    check(create_list(ctypes.cast(session, c_void_p), ctypes.addressof(info), byref(xlist)), "xrCreateXDevListMNDX")
    count = c_uint32()
    check(enum_xdevs(xlist.value, 0, byref(count), None), "xrEnumerateXDevsMNDX")
    ids = (c_uint64 * count.value)()
    check(enum_xdevs(xlist.value, count.value, byref(count), ids), "xrEnumerateXDevsMNDX")

    devices, seen = [], set()
    for xid in ids:
        props = XDevProperties(TYPE_XDEV_PROPERTIES, None)
        gi = GetXDevInfo(TYPE_GET_XDEV_INFO, None, xid)
        check(get_props(xlist.value, ctypes.addressof(gi), ctypes.addressof(props)), "xrGetXDevPropertiesMNDX")
        name, serial = props.name.decode(), props.serial.decode()
        kind = kind_for(name, serial)
        if a.list:
            print(f"  xdev {xid}: name={name!r} serial={serial!r} space={bool(props.canCreateSpace)} -> {kind}")
        if not kind or not props.canCreateSpace or serial in seen:
            continue
        seen.add(serial)
        si = CreateXDevSpaceInfo(TYPE_CREATE_XDEV_SPACE_INFO, None, xlist.value, xid, xr.Posef())
        sp = c_uint64()
        check(create_space(ctypes.cast(session, c_void_p), ctypes.addressof(si), byref(sp)), f"xrCreateXDevSpaceMNDX {name}")
        devices.append({"kind": kind, "model": name, "serial": serial, "space": ctypes.cast(c_void_p(sp.value), xr.Space)})
    if a.list:
        print("Using:", ", ".join(f"{d['kind']}={d['serial']}" for d in devices))
        return
    if not devices:
        raise SystemExit("no usable xdevs")

    to_xrtime = proc(instance, "xrConvertTimespecTimeToTimeKHR", c_int, c_void_p, c_void_p, POINTER(ctypes.c_int64))

    class Timespec(Structure):
        _fields_ = [("tv_sec", ctypes.c_long), ("tv_nsec", ctypes.c_long)]

    def xr_now():
        ns = time.clock_gettime_ns(time.CLOCK_MONOTONIC)
        ts = Timespec(ns // 1_000_000_000, ns % 1_000_000_000)
        out = ctypes.c_int64()
        check(to_xrtime(ctypes.cast(instance, c_void_p), ctypes.addressof(ts), byref(out)), "xrConvertTimespecTimeToTimeKHR")
        return out.value

    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    prefix = Path(a.out) if a.out else Path("recordings") / f"{stamp}_{a.label}"
    kinds = [{"kind": d["kind"], "serial": d["serial"]} for d in devices]
    if a.stream:
        sinks = {"frame-now": StreamSink(kinds)}
    else:
        sinks = {m: Sink(Path(f"{prefix}-{m}"), kinds) for m in ("frame-predicted", "frame-now", "poll-now")}
    print("Devices:", ", ".join(f"{d['kind']} {d['model']} [{d['serial']}]" for d in devices))

    def locate(d, t):
        vel = xr.SpaceVelocity()
        loc = xr.SpaceLocation(next=ctypes.cast(byref(vel), c_void_p))
        xr.raw_functions.xrLocateSpace(d["space"], stage, t, byref(loc))
        return loc, vel

    pulse_before = None if a.no_pulse else ask_pulse("BEFORE recording")
    t0 = [None]
    stop = threading.Event()
    lock = threading.Lock()

    def poll():
        period = 1.0 / a.poll_hz
        nxt = time.perf_counter()
        while not stop.is_set():
            t = xr_now()
            if t0[0] is not None:
                for d in devices:
                    loc, vel = locate(d, t)
                    with lock:
                        sinks["poll-now"].write(d["serial"], (t - t0[0]) / 1e9, loc, vel)
            nxt += period
            dt = nxt - time.perf_counter()
            if dt > 0:
                time.sleep(dt)
            else:
                nxt = time.perf_counter()

    running, frames = False, 0
    th = threading.Thread(target=poll, daemon=True)
    started = None
    buf = xr.EventDataBuffer()
    try:
        while True:
            while True:
                try:
                    ev = xr.poll_event(instance)
                except xr.EventUnavailable:
                    break
                if ev.type == xr.StructureType.EVENT_DATA_SESSION_STATE_CHANGED:
                    st = ctypes.cast(byref(ev), POINTER(xr.EventDataSessionStateChanged)).contents.state
                    if st == xr.SessionState.READY:
                        xr.begin_session(session, xr.SessionBeginInfo(xr.ViewConfigurationType.PRIMARY_STEREO))
                        running = True
                    elif st in (xr.SessionState.STOPPING, xr.SessionState.EXITING, xr.SessionState.LOSS_PENDING):
                        raise SystemExit(f"session state {st}")
            if not running:
                time.sleep(0.01)
                continue
            fs = xr.wait_frame(session)
            xr.begin_frame(session)
            now = xr_now()
            if t0[0] is None:
                t0[0] = now
                started = time.monotonic()
                if not a.stream:
                    th.start()
                print(f"Streaming poses to stdout" if a.stream else f"Recording {a.seconds:.0f} s to {prefix}-*  (sit still; Ctrl+C stops early)")
            for d in devices:
                if a.stream:
                    sinks["frame-now"].write(d["serial"], (now - t0[0]) / 1e9, *locate(d, now))
                    continue
                loc, vel = locate(d, fs.predicted_display_time)
                with lock:
                    sinks["frame-predicted"].write(d["serial"], (fs.predicted_display_time - t0[0]) / 1e9, loc, vel)
                loc, vel = locate(d, now)
                with lock:
                    sinks["frame-now"].write(d["serial"], (now - t0[0]) / 1e9, loc, vel)
            xr.end_frame(session, xr.FrameEndInfo(display_time=fs.predicted_display_time,
                                                  environment_blend_mode=xr.EnvironmentBlendMode.OPAQUE,
                                                  layers=None))
            frames += 1
            if a.stream:
                sys.stdout.flush()
            el = time.monotonic() - started
            if frames % 450 == 0:
                print(f"  {el:5.0f} / {a.seconds:.0f} s  frames {frames}  ({frames / el:.1f} Hz)  rows {sum(s.rows for s in sinks.values())}")
            if el >= a.seconds:
                break
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        if th.is_alive():
            th.join(1)
    duration = time.monotonic() - started if started else 0
    pulse_after = None if a.no_pulse else ask_pulse("AFTER recording")
    for mode, s in sinks.items():
        meta = {"startedUtc": datetime.now(timezone.utc).isoformat(), "durationSeconds": duration,
                "label": f"{a.label} ({mode})", "note": a.note, "runtime": "Monado via OpenXR overlay + XR_MNDX_xdev_space",
                "sampling": mode, "frameHz": frames / duration if duration else 0, "pollHz": a.poll_hz,
                "pulseBeforeBpm": pulse_before, "pulseAfterBpm": pulse_after, "imuStreams": [],
                "devices": [{"index": i, "kind": d["kind"], "model": d["model"], "serial": d["serial"]}
                            for i, d in enumerate(devices)],
                "columns": {"poses": "host_t: s from XrTime; STAGE space; v: m/s; w: rad/s; result 200 = valid"}}
        s.close(meta)
        print(f"Saved {s.rows} rows to {s.folder}" if hasattr(s, "folder") else f"Streamed {s.rows} poses")
    xr.request_exit_session(session)
    glfw.terminate()


if __name__ == "__main__":
    main()
