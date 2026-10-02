# Lens scene setup

The TypeScript is written and typechecked. Lens Studio's editor cannot be
driven from outside, so the scene wiring is yours. This is the exact list.

Project: `Spectacles/Holo-Display/Holo-Display.esproj` (Lens Studio 5.15.4).
Scripts are already in `Assets/Scripts/`:

| File | Role |
| --- | --- |
| `HoloConnection.ts` | WebSocket client and protocol. Not a component. |
| `HoloPanel.ts` | One floating monitor. Goes on the panel prefab root. |
| `HoloDisplays.ts` | Controller. Goes on one scene object. |

---

## 1. Project settings

**Project Settings → enable Experimental APIs.**

Required for `ws://`. The lens becomes unpublishable, which is the accepted
trade for v1 — see [SETUP.md §3.2](SETUP.md).

## 2. Asset modules

In the Asset Browser, **+ → Add Module** for both:

- **Internet Module**
- **Remote Media Module**

Leave the default names.

## 3. The panel prefab

Build this once; the controller instantiates one per display.

1. In the Scene Hierarchy, create an empty **Scene Object**, name it `Panel`.
2. Add component **Frame** (Spectacles UI Kit) to it. Set:

   | Property | Value | Why |
   | --- | --- | --- |
   | Inner Size | `80, 50` | centimetres; the controller overwrites this per display's aspect |
   | Allow Translation | on | drag to move |
   | Allow Scaling | on | corner resize |
   | Allow Non Uniform Scaling | **on** | monitors are not square; corners must change aspect |
   | Auto Scale Content | on | the image follows the frame |
   | Minimum Size | `20, 12` | |
   | Maximum Size | `300, 200` | a 3 m panel is already absurd |
   | Appearance | `Large` | far-field interaction |

3. As a **child** of `Panel`, add a **Scene Object** named `Screen` with an
   **Image** component.
   - Set the Frame's **Content** input to this `Screen` object.
   - Give the Image any material that has a `baseTex` (the default Image
     material is fine). The script clones it per panel so panels do not share a
     texture.
4. As another child, add a **Text** object named `Switching`, text
   `switching…`. Leave it enabled; the script hides it on start and shows it
   only while a resolution change is in flight.
5. Add component **HoloPanel** (`Assets/Scripts/HoloPanel.ts`) to `Panel` and
   wire:

   | Input | Set to |
   | --- | --- |
   | Frame | the Frame component on `Panel` |
   | Image | the Image on `Screen` |
   | Switching Label | the `Switching` object |

6. Drag `Panel` into the Asset Browser to make it a **Prefab**. Delete the
   instance from the hierarchy — the controller spawns them.

## 4. The controller

1. Create an empty Scene Object named `HoloDisplays`.
2. Add component **HoloDisplays** (`Assets/Scripts/HoloDisplays.ts`).
3. Wire the inputs:

   | Input | Value |
   | --- | --- |
   | Host Ip | **your laptop's LAN IP**, shown in large text in the host window |
   | Host Port | `8880`, or whatever the host says it bound |
   | Internet Module | the Internet Module asset |
   | Remote Media Module | the Remote Media Module asset |
   | Camera Object | your scene's Camera (optional; it searches if empty) |
   | Status Text | a Text object somewhere visible (optional but useful) |
   | Panel Prefab | the `Panel` prefab from step 3 |
   | Panel Width Cm | `80` |
   | Panel Distance Cm | `100` |
   | Arc Degrees | `50` |
   | Full Degrees | `25` |
   | Low Degrees | `45` |
   | Hysteresis Degrees | `5` |
   | Pixels Per Metre | `2400` |

**Host Ip must be the LAN address, not `127.0.0.1`.** Lens Studio itself holds
`127.0.0.1:8880` while open; the glasses need the routable address anyway.

## 5. Run it

1. Start the host (`host/open_in_godot.bat`, then Play).
2. Read the IP:port from the host window.
3. **Send to Device** in Lens Studio.

Expected: the status text goes `connecting` → `connected` → `N displays`, and a
panel appears per streamed virtual monitor on a gentle arc about 1 m away.

---

## What each milestone looks like when it works

- **M4** — one panel showing a virtual monitor, updating live.
- **M5** — one panel per display; drag moves them, corners resize them.
- **M6** — turn your head and the host's rows change `full` → `low` → `off`.
  Panels you look away from keep their last frame, frozen. They must never go
  blank.
- **M7** — resize a panel and release: Windows changes that monitor's real
  resolution, then the panel snaps to the new aspect exactly.

## Notes on the implementation

**Frame pairing.** The host sends a JSON header then the JPEG as the next
binary message. `HoloConnection` holds the header and pairs it with whatever
binary arrives next, so the lens never slices a binary blob. A binary message
with no pending header is dropped.

**Acks are sent after the texture is on screen**, not on arrival. The host
allows one frame in flight per display, so acking early would defeat the
backpressure and let latency grow. A decode failure still acks, otherwise that
display stalls until the host's 500 ms timeout.

**Blob to texture** uses `DynamicResource.createWithBuffer(await blob.bytes())`,
falling back to `internetModule.makeResourceFromBlob` on older runtimes —
that call is deprecated as of Lens Scripting 362.

**Gaze** uses head direction, not eye tracking, and subtracts each panel's
angular half-size so a large panel counts as visible when any part of it is near
the centre of view. Hysteresis widens whichever band a panel is already in, so
glancing past an edge does not flip it back and forth. Tiers are sent only on
change.

**Resize** scores modes by aspect first (log-ratio, so 16:9 does not become
portrait), then by how close the mode's width is to
`panelWidthMetres * pixelsPerMetre`. If the best match is the current mode it
sends nothing and just tidies the aspect.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Status stuck on `connecting` | Experimental APIs off, wrong IP, firewall, or AP isolation |
| Connects then immediately drops | Pointed at `127.0.0.1` and reached Lens Studio instead of the host |
| Panels appear but stay black | Host has no display ticked, or all panels are `off` — look at one |
| Panels never drop to `off` | Camera Object not set and no camera found; check the log |
| Resize does nothing | That mode is not in VDD's resolution list ([SETUP.md §1.5](SETUP.md)) |
| Memory grows over a long session | Check that old textures are being released; see `HoloPanel.setTexture` |
