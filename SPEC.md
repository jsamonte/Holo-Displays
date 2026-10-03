# Spectacles Virtual Displays: Build Spec

Hand this file to Claude Code. Put it in the repo root, then say:
"Read SPEC.md and start Milestone 0. Stop after each milestone so I can test."

---

## What we're building

Virtual monitors for Snap Spectacles. The Windows laptop thinks extra monitors are plugged in (like HDMI). Each one shows up on the glasses as its own floating panel that I can drag, drop, and resize. Resizing a panel changes that monitor's real resolution in Windows, like swapping in a bigger monitor. Only panels I'm looking at (or near) get streamed, to save bandwidth and decode work.

This is extended displays, NOT screen mirroring. The laptop's own keyboard and trackpad are the input; the cursor moves between virtual monitors like normal.

## Three parts

1. **Virtual Display Driver (VDD)**: open source IddCx driver that adds virtual monitors to Windows 10/11. https://github.com/VirtualDrivers/Virtual-Display-Driver (installable with `winget install --id=VirtualDrivers.Virtual-Display-Driver -e`). I install this myself. Claude Code should read the repo's current docs and tell me how to configure monitor count and the list of supported resolutions (its settings XML). Don't guess paths.
2. **Host app**: Godot 4.x .NET (C#), Windows only. Captures each virtual monitor, streams it over WebSocket, and changes monitor resolution on request.
3. **Lens**: Lens Studio 5.x project for Spectacles, TypeScript. One panel per virtual monitor, with drag/resize, gaze-based stream control, and resolution requests.

## Repo layout

```
/host        Godot .NET project
/lens        Lens Studio project (scripts in /lens/Assets/Scripts)
/tools       test client that pretends to be the glasses
/docs        PROTOCOL.md, SETUP.md, notes
SPEC.md
README.md
```

## Ground rules for Claude Code

- Lens Studio and Spectacles APIs change often. Before writing lens code, check the current docs at developers.snap.com (InternetModule, WebSocket, RemoteMediaModule, Spectacles Interaction Kit, Spectacles UI Kit). Don't rely on memory for API names.
- Same for Godot .NET: check the docs for the Godot version I have installed.
- You can't drive the Lens Studio editor. Write the TypeScript scripts and give me exact scene setup steps (what objects, components, and inspector values).
- Ask before anything that needs admin rights, installs drivers, or changes display settings on my primary monitor. Never touch the primary display's resolution.
- Commit at the end of each milestone with a clear message. Stop and tell me what to test.
- Keep it simple first. Get it working, then make it fast.

---

## Protocol (WebSocket, ws:// on the local network)

Write this up properly in `/docs/PROTOCOL.md` and keep it in sync with the code.

Control messages are JSON text. Frames are sent as **a JSON header message immediately followed by one binary message** with the JPEG bytes. WebSocket keeps order, so the lens just remembers the last header. This avoids having to slice binary blobs in the lens.

### Host to lens

- `{"t":"displays","list":[{"id":2,"name":"VDD 1","w":1920,"h":1080,"modes":[[1920,1080],[2560,1440],[1080,1920],...]}]}`
  Sent on connect and whenever anything changes (mode switch, monitor added/removed).
- `{"t":"frame","id":2,"seq":481,"w":1920,"h":1080}` then binary JPEG
- `{"t":"mode_changed","id":2,"w":2560,"h":1440,"ok":true}` (or `ok:false` with `"err"`)

### Lens to host

- `{"t":"hello","client":"spectacles","ver":1}`
- `{"t":"visibility","id":2,"tier":"full"}` where tier is `full`, `low`, or `off`. Only sent when a tier changes.
- `{"t":"ack","id":2,"seq":481}` after a frame is decoded and on screen.
- `{"t":"resize","id":2,"w":2560,"h":1440}` must be one of that display's `modes`.

### Backpressure (important for latency)

At most one frame in flight per display. The host doesn't send the next frame for a display until it gets the `ack` for the previous one (or 500 ms passes). This keeps latency low instead of letting frames pile up in buffers.

---

## Host app (Godot .NET, C#)

### Display discovery
- Enumerate monitors with Godot's `DisplayServer` (count, position, size).
- Use Win32 (`EnumDisplayDevicesW`, `EnumDisplayMonitors`, `GetMonitorInfoW`) via P/Invoke to get each monitor's device name (`\\.\DISPLAYn`) and friendly name, and match them to Godot screen indexes by position/rect.
- Flag which ones are VDD virtual monitors. In the UI, let me tick which monitors to stream. Default: all virtual monitors, never the primary.
- For each streamed monitor, list available modes with `EnumDisplaySettingsW`.

### Capture and encode (Phase 1, simple)
- `DisplayServer.ScreenGetImage(screen)` for each display that needs a frame.
- Optional downscale if the long edge is over a configurable max (default 1920).
- `Image.SaveJpgToBuffer(quality)`, quality slider in UI (default 0.7).
- Measure and show capture ms and encode ms per display. If it's too slow, that's what Phase 2 fixes.
- Skip sending if the frame is identical to the last one sent for that display (cheap hash of a downsampled copy). Still respond to visibility changes right away.

### Tiers
- `full`: target 15 fps (configurable)
- `low`: target 2 fps (configurable)
- `off`: don't capture at all
- Display with no client connected: don't capture.

### WebSocket server
- `TCPServer` listening on port 8765 (configurable), `WebSocketPeer.AcceptStream` for each connection, poll in `_Process`.
- **Gotcha:** raise `OutboundBufferSize` and `InboundBufferSize` on the `WebSocketPeer` before accepting (several MB). The defaults are too small for JPEG frames and sends will fail.
- One client at a time is fine for v1.
- Show the laptop's LAN IP and port big in the UI so I can type it into the lens.

### Resolution changes
- On `resize`, validate the mode is in the list, then `ChangeDisplaySettingsExW` for that device name. Use `CDS_UPDATEREGISTRY | CDS_NORESET`, then apply with a final `ChangeDisplaySettingsExW(null, ...)` call. Check the return code and report it.
- Keep the monitor's position in the desktop layout stable if possible.
- After the change, re-read the display, send `mode_changed` and a fresh `displays` list.
- Debounce: ignore a new resize for the same display until the previous one finishes.

### UI
Simple control panel: server status, IP:port, connected client, and a row per display (name, resolution, tier, fps, KB/s, capture ms, encode ms, stream checkbox). Quality and fps settings.

### Phase 2 (later, only if Phase 1 is too slow)
- DXGI Desktop Duplication per output (e.g. Vortice.Windows NuGet) on worker threads.
- libjpeg-turbo for encoding.
- Use DXGI dirty rects to skip unchanged frames.

---

## Lens (Lens Studio, TypeScript)

### Project setup
- Start from Spectacles Base Template (includes Spectacles Interaction Kit).
- Add Spectacles UI Kit and use its **Frame** component for panels. (SIK's ContainerFrame is deprecated in favor of UI Kit Frame. Confirm in current docs.)
- Add InternetModule and RemoteMediaModule assets.
- Plain `ws://` needs Experimental APIs turned on in Project Settings. That means the lens can't be published, which is fine for personal use. Note this in SETUP.md.
- Host IP and port as inspector inputs for v1.

### Connection
- `internetModule.createWebSocket("ws://IP:8765")`, `binaryType = "blob"`.
- Send `hello` on open. Auto-reconnect with backoff, and show a small status label (connecting / connected / error).

### Panels
- On `displays`, spawn one panel prefab per display (and remove panels for displays that disappeared).
- Initial layout: gentle arc about 1 m away at eye height, aspect ratio matching each display.
- Panel = UI Kit Frame with an image quad as content. Drag to move, corner handles to resize.

### Frames
- On a `frame` header, store it as pending. On the next binary message, `internetModule.makeResourceFromBlob(blob)` then `remoteMediaModule.loadResourceAsImageTexture(...)`, assign the texture to that panel's material, then send `ack`.
- Drop the reference to the old texture so memory doesn't grow. Watch for leaks in a long test.
- Fallback if that path doesn't work or is slow: host sends base64 in the JSON and the lens uses `Base64.decodeTextureAsync`. Make this a protocol option only if needed.

### Gaze-based tiers
- About 10 times a second, for each panel compute the angle between the camera forward vector and the direction to the panel center, minus the panel's angular half-size (so big panels count as visible when any part is near center).
- Thresholds (inspector configurable): under 25 degrees = `full`, under 45 degrees = `low`, else `off`.
- 5 degree hysteresis so glancing near an edge doesn't flip tiers back and forth.
- Send `visibility` only when the tier changes. Panels that are `off` keep their last frame (frozen), never blank.
- Use head direction, not eye tracking.

### Resize to resolution
- While dragging a corner: just scale the panel visually, no messages.
- On release: pick the mode from that display's `modes` whose aspect ratio is closest to the panel's new aspect, and among those the one closest to `panelWidthMeters * pixelsPerMeter` (inspector setting, default so a 0.8 m wide panel is about 1920 px). Send `resize`.
- Show a small "switching..." overlay until `mode_changed` arrives, then snap the panel to the exact new aspect ratio. On `ok:false`, revert the panel size.

---

## Test client (/tools)

A small HTML page (or Python script) that acts like the glasses: connects to the host, shows each display's stream in a grid, and has buttons to set each display's tier and send resize requests. This lets us test the whole host without wearing the Spectacles.

---

## Milestones

Stop after each one and tell me what to test.

- **M0 Scaffold:** repo layout, README, `/docs/PROTOCOL.md`, `/docs/SETUP.md` (VDD install and config steps from the VDD repo's current docs, Godot .NET setup, Lens Studio setup).
  *Done when:* I can follow SETUP.md and have 2 virtual monitors showing in Windows Display Settings.
- **M1 Host capture:** Godot app lists monitors, flags the virtual ones, shows a live preview of one in the UI, and displays capture/encode times.
  *Done when:* I see a virtual monitor's contents updating in the Godot window.
- **M2 Host server + test client:** WebSocket server, full protocol (frames, tiers, acks), test client in /tools.
  *Done when:* the test client shows both virtual monitors live and the tier buttons change fps as expected.
- **M3 Host resolution changes:** `resize` handling, `mode_changed`, updated `displays`.
  *Done when:* clicking a resize button in the test client changes the virtual monitor's resolution in Windows Display Settings.
- **M4 Lens single panel:** connects, shows one display's stream on one panel.
  *Done when:* I see a virtual monitor on the glasses.
- **M5 Lens multi panel + drag/resize:** one Frame per display, move and scale.
- **M6 Lens gaze tiers:** visibility logic with hysteresis, frozen frames for off panels.
  *Done when:* the host UI shows displays dropping to low/off as I turn my head.
- **M7 Lens resize to resolution:** release a resize, the monitor's real resolution changes, panel snaps to the new aspect.
- **M8 Performance (only if needed):** Phase 2 capture/encode, tuning.

## Nice to have later
- Remember panel positions between sessions (Spectacles spatial anchors).
- Pinch on a panel to move the laptop cursor there.
- wss:// with a self-signed cert so the lens doesn't need Experimental APIs.
- Text entry for host IP on the glasses.

---

## Deviations from this spec in the actual repo

These were agreed after the spec was written. `docs/PROTOCOL.md` and
`docs/SETUP.md` are the authority where they differ.

- **Port is 8800**, not 8765. Still configurable.
- **Lens lives at `Spectacles/Holo-Display/`**, not `/lens`. Lens scripts go in
  `Spectacles/Holo-Display/Assets/Scripts/`.
- **Godot is 4.7.2 .NET, `windows_arm64` build.** This is a Snapdragon X Plus
  laptop; the `mono_win64` (x86_64) build runs emulated and cannot see the
  ARM64 .NET SDK. See `docs/SETUP.md`.
- **Virtual monitor count is controlled by the host app at runtime**, over the
  driver's named pipe (`SETDISPLAYCOUNT`), not by hand-editing VDD's XML. The
  XML still owns the resolution list, which has no runtime equivalent.
- **Deliverable is the Lens project + the Godot project only.** No external
  helper tools in the shipped path; `tools/` is development-only. VDD itself
  still has to be installed on the target machine, so the host detects it and
  explains rather than assuming it is there.
