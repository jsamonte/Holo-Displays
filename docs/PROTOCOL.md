# Holo-Displays protocol

WebSocket, `ws://` on the local network. Host listens on **port 8800** by
default (configurable in the host UI).

> `SPEC.md` said 8765. The project uses **8800**. If you change it, change it in
> the host UI and in the lens inspector.

> **Lens Studio also listens on 127.0.0.1:8800 and 127.0.0.1:8880** while it is
> open — both, so swapping between them changes nothing. The host
> binds `0.0.0.0`, so Windows sends `127.0.0.1:8800` to Lens Studio and the
> laptop's **LAN address** to the host. The glasses connect over the LAN
> address, so this works — but a test client on the laptop must use the LAN IP,
> not `127.0.0.1`, or it will reach Lens Studio and fail the handshake. If 8800
> is fully occupied the host walks forward to 8881, 8882… and shows the port it
> actually bound.

Keep this file in sync with the code. The host's message handling lives in
[host/scripts/](../host/scripts/); the lens side in
[Spectacles/Holo-Display/Assets/Scripts/](../Spectacles/Holo-Display/Assets/Scripts/).

## Shape of the connection

- One client at a time in v1. A second connection is accepted but the first is
  dropped.
- Control messages are **JSON text frames**.
- Video frames are **a JSON header text frame immediately followed by one binary
  frame** holding the JPEG bytes.

WebSocket guarantees ordering, so the receiver just remembers the last header it
saw and pairs it with the next binary message. This is deliberate: it means the
lens never has to slice or parse a binary blob to find where the header ends.

```
host ──► {"t":"frame","id":2,"seq":481,"w":1920,"h":1080}   (text)
host ──► <JPEG bytes>                                        (binary)
```

If a binary message ever arrives with no pending header, drop it.

## Display ids

`id` is the **Windows display number** parsed from the device name
`\\.\DISPLAY2` → `2`. It is stable while the monitor exists. It is *not* the
Godot screen index; the host keeps that mapping internally.

---

## Host to lens

### `displays`

Sent on connect, and again whenever anything changes: a mode switch, a monitor
added or removed, or the user ticking a stream checkbox in the host UI.

```json
{
  "t": "displays",
  "list": [
    {
      "id": 2,
      "name": "VDD 1",
      "w": 1920,
      "h": 1080,
      "modes": [[1920,1080],[2560,1440],[1080,1920],[3840,2160]]
    }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `id` | Windows display number |
| `name` | Friendly name for the UI |
| `w`, `h` | Current resolution in pixels |
| `modes` | Every resolution this display can switch to, as `[w,h]` pairs |

Only displays the host is actually streaming appear in `list`. A display that
disappears from `list` should have its panel removed.

### `frame`

```json
{"t":"frame","id":2,"seq":481,"w":1920,"h":1080}
```

Immediately followed by one binary message with the JPEG. `seq` increments per
display and is what you quote back in `ack`. `w`/`h` are the dimensions of
**this JPEG**, which may be smaller than the display's resolution if the host is
downscaling (see the max long edge setting).

### `mode_changed`

```json
{"t":"mode_changed","id":2,"w":2560,"h":1440,"ok":true}
```

On failure:

```json
{"t":"mode_changed","id":2,"w":2560,"h":1440,"ok":false,"err":"DISP_CHANGE_BADMODE (-2)"}
```

A fresh `displays` message follows a successful change.

---

## Lens to host

### `hello`

First message after the socket opens.

```json
{"t":"hello","client":"spectacles","ver":1}
```

The host replies with `displays`. `ver` is the protocol version; it is `1`.

### `visibility`

```json
{"t":"visibility","id":2,"tier":"full"}
```

`tier` is one of:

| Tier | Host behaviour |
| --- | --- |
| `full` | Capture and send at the full fps target (default 15) |
| `low` | Capture and send at the low fps target (default 2) |
| `off` | Do not capture this display at all |

**Send this only when a tier actually changes**, not every frame. A display the
host has never heard about defaults to `off` until the lens says otherwise.

An `off` panel keeps its last frame on screen, frozen. It must never go blank.

### `ack`

```json
{"t":"ack","id":2,"seq":481}
```

Sent once the frame is decoded and on screen — not when it arrives. This is what
makes the backpressure meaningful.

### `set_count`

```json
{"t":"set_count","n":2}
```

Asks the host to open or close virtual monitors — `n` is how many should exist,
0 to 8. The host drives the driver and replies with a fresh `displays` list when
they settle; there is no direct acknowledgement.

The lens sends this on **every connect**, not only when the number changes,
because the host may have restarted since it last heard anything. The host
ignores a request for the count it already has, so reconnecting does not reload
the driver and blink every monitor.

Changing the count reloads the VDD driver, so all virtual monitors disappear and
return, and any resolution set on them reverts to default. Expect a gap of a
couple of seconds and a complete `displays` refresh.

Ignored, with a logged line, if VDD is not installed.

### `resize`

```json
{"t":"resize","id":2,"w":2560,"h":1440}
```

`w`/`h` must be one of that display's advertised `modes`. The host validates and
replies `mode_changed` either way.

---

## Backpressure

**At most one frame in flight per display.** The host will not send the next
frame for a display until either:

- the `ack` for the previous `seq` arrives, or
- 500 ms pass (the ack timeout).

This is the single most important thing for latency. Without it, frames pile up
in the socket's send buffer and the glasses end up showing a view of the desktop
that is seconds old while bandwidth is saturated. With it, a slow link
degrades into a lower frame rate instead of growing lag.

Consequences worth remembering:

- A lens that forgets to `ack` sees 2 fps (one frame per timeout), not a freeze.
- Tiers are a *target*, not a guarantee. The real rate is
  `min(tier fps, 1 / round-trip time)`.
- The ack timeout is per display, so one stalled panel does not hold up others.

## Unchanged-frame skipping

The host hashes each captured frame and skips sending if it matches the last
frame sent for that display. A static monitor therefore costs no bandwidth.

**This means frame count is not a measure of tier.** The tier sets how often the
host *captures*; the hash decides whether anything is *sent*. On a mostly static
desktop a `low` display can legitimately deliver more frames than a `full` one,
because its longer interval gives the screen more time to change between
captures. What a tier actually guarantees is a floor on the gap between frames —
about 67 ms at `full`, 500 ms at `low`. Measure that, not the count.

(`tools/protocol_test.mjs` originally got this wrong and failed a working host.)

This does not apply to the first frame after a tier change — a panel coming back
from `off` always gets a fresh frame, even if the desktop has not changed, so
the lens never waits on an unchanged screen.

## Error handling

- Malformed JSON: ignore the message, log it on the host.
- Unknown `t`: ignore it. This is how the protocol gets extended without
  breaking old clients.
- Unknown `id` in `visibility`, `ack` or `resize`: ignore it. It usually means a
  monitor went away while a message was in flight.
- Socket closes: the lens reconnects with backoff and starts again from `hello`.
  The host forgets all tier state for a disconnected client and stops capturing.

## Version history

| `ver` | Change |
| --- | --- |
| 1 | Initial protocol |
