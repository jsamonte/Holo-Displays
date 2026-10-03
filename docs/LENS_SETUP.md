# Lens scene setup

Three steps. The panels build themselves in code, so there is no prefab to
assemble and nothing to wire.

Project: `Spectacles/Holo-Display/Holo-Display.esproj` (Lens Studio 5.15.4).
Scripts are already in `Assets/Scripts/`:

| File | Role |
| --- | --- |
| `HoloConnection.ts` | WebSocket client and protocol. Not a component. |
| `HoloPanel.ts` | One floating monitor. Plain class, built at run time. |
| `HoloDisplays.ts` | The only component you place in the scene. |

---

## 1. Turn on Experimental APIs

**Project Settings → enable Experimental APIs.**

Required for `ws://`. Snap's docs: *"Using insecure connections (`ws`) requires
enabling Experimental APIs. While these Lenses are suitable for testing
purposes, they cannot be published."*

So the lens runs on your own Spectacles via Send to Device, and cannot be
published. Publishing needs `wss://`, which needs a certificate the glasses
trust — a separate problem, discussed at the end.

## 2. Add one Scene Object

1. Create an empty **Scene Object**. Call it `HoloDisplays`.
2. Add the component **HoloDisplays** (`Assets/Scripts/HoloDisplays.ts`).

That is the whole scene. No prefab, no modules, no materials.

## 3. Set two fields

| Input | Value |
| --- | --- |
| **Host Ip** | the host machine's **LAN IP**, shown in large text in the host window |
| **Host Port** | `8800` |

Everything else can stay at its default:

| Input | Default | Note |
| --- | --- | --- |
| Internet Module / Remote Media Module | empty | obtained in code |
| Panel Mesh / Panel Material | empty | UI Kit's unit plane and image material |
| Camera Object | empty | the scene's camera is found automatically |
| Status Text | empty | optional, but handy — any Text object |
| Panel Width Cm | 80 | 0.8 m wide |
| Panel Distance Cm | 100 | 1 m away |
| Arc Degrees | 50 | spread across several displays |
| Full / Low / Hysteresis Degrees | 25 / 45 / 5 | gaze tiers |
| Pixels Per Metre | 2400 | 0.8 m panel ≈ 1920 px |

**Host Ip must be the LAN address, not `127.0.0.1`.** Lens Studio holds both
`127.0.0.1:8800` and `:8880` while open, and the glasses need a routable address
regardless.

## 4. Run it

1. Start the host and read the IP:port from its window.
2. **Send to Device.**

Expect: status goes `connecting` → `connected` → `N displays`, and a panel
appears per streamed monitor on a gentle arc about 1 m away, showing the
desktop live.

With no virtual monitors installed, start the host with
`HOLO_STREAM_PRIMARY=1` and it will offer the built-in display instead. That is
enough to see the whole thing work.

**Use a hotspot, not school or office wifi.** Those almost always run AP
isolation, which blocks device-to-device traffic silently and looks exactly like
a firewall problem.

---

## What works now, and what does not

**Working:** connection, reconnect with backoff, one panel per display laid out
on an arc, live streaming, gaze tiers with hysteresis, frozen last frame when a
panel goes `off`, and the resolution-change protocol.

**Not yet: drag and corner-resize.** UI Kit's `Frame` is what provides those,
and it cannot be attached from code — Lens Studio exposes only a generic
`createComponent("ScriptComponent")`, with no way to bind a particular
TypeScript class, and SIK and UI Kit never do it either. Panels are currently
fixed where they spawn.

That is the one thing a prefab would buy, and it is why M5 and M7 are not done.
Adding it later means building a prefab with a `Frame` and having `HoloDisplays`
clone it instead of constructing panels — `HoloPanel` is already shaped for it,
and `requestResize` is already written and waiting.

## If something is wrong

| Symptom | Likely cause |
| --- | --- |
| Status stuck on `connecting` | Experimental APIs off, wrong IP, firewall, or AP isolation |
| Connects then drops immediately | Pointed at `127.0.0.1` and reached Lens Studio instead of the host |
| Log says `no panel mesh` / `no panel material` | My guess at UI Kit's asset paths was wrong. Drag any plane mesh and any unlit material onto **Panel Mesh** and **Panel Material** — that is what those inputs are for |
| Log says `no InternetModule` / `no RemoteMediaModule` | The require name differs on your Lens Studio version. Add the module in the Asset Browser and set the matching input |
| Panels appear but stay black | Host has nothing ticked to stream, or every panel is `off` — look at one |
| Panels face away or are edge-on | See the note below |
| Panels never drop to `off` | No camera found; check the log |
| Memory climbs over a long session | Look at `HoloPanel.setTexture` first |

**Panel facing is the main thing I could not verify without running it.**
`placeOnArc` aims each panel with `quat.lookAt` at the user rather than deriving
a rotation from the arc angle, because hand-deriving that means guessing a sign
convention, and getting it wrong turns every panel away while looking perfectly
reasonable in the source. If they come up backwards, negate the vector:

```ts
const toUser = new vec3(x, 0, z).normalize()   // was -x, -z
```

## Notes on the implementation

**Frame pairing.** The host sends a JSON header then the JPEG as the next
binary message. `HoloConnection` holds the header and pairs it with whatever
binary arrives next, so the lens never slices a blob. Binary with no pending
header is dropped.

**Acks go out after the texture is on screen**, not on arrival. The host allows
one frame in flight per display, so acking early would defeat the backpressure.
A failed decode still acks, or that display stalls until the host's 500 ms
timeout.

**Blob to texture** uses `DynamicResource.createWithBuffer(await blob.bytes())`,
falling back to `internetModule.makeResourceFromBlob` — deprecated as of Lens
Scripting 362 — on older runtimes.

**Gaze** uses head direction, not eye tracking, and subtracts each panel's
angular half-size so a large panel counts as visible when any part of it is near
the centre of view. Hysteresis widens whichever band a panel is already in, so a
glance past an edge does not flip the tier. Tiers are sent only on change.

**Panels are a unit plane scaled in centimetres**, so local scale is the panel
size directly, with no hidden base dimension.

## On publishing this lens

Two things block it, one easy and one not.

**Entering the host address at run time: solvable.** Spectacles has a System AR
Keyboard (`TextInputSystem`, with `Num` and `Url` keyboard types), so the lens
can ask for the address and remember it instead of carrying it in the inspector.

**`wss://`: the real obstacle.** Publishing requires a secure connection, and a
secure connection requires a certificate the glasses trust. You cannot get a
publicly trusted certificate for a private LAN IP — no CA issues them. Snap's
docs do not say whether self-signed certificates are accepted, which usually
means they are not, and that is worth testing before designing around it.

If self-signed is rejected, the options are a cloud relay with a real
certificate (works anywhere, but it is a server to run and your screen contents
travel through it) or a public DNS name with a real certificate resolving to a
private address, the trick Plex uses.
