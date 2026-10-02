import {DisplayInfo, FrameHeader, HoloConnection, Tier} from "./HoloConnection"
import {HoloPanel} from "./HoloPanel"

/**
 * Top-level controller: one panel per virtual monitor, gaze-driven stream
 * tiers, and resolution changes on release.
 *
 * Put this on one SceneObject in the scene. Everything else is spawned.
 *
 * Units are centimetres (100 = 1 m), which is what Lens Studio and UI Kit's
 * Frame.innerSize both use.
 */
@component
export class HoloDisplays extends BaseScriptComponent {
  // ---- connection --------------------------------------------------------

  @input
  @hint("Laptop's LAN IP. The host shows this in large text. NOT 127.0.0.1.")
  hostIp: string = "192.168.1.10"

  @input
  @hint("Host port. 8880 unless the host says it fell forward to another.")
  hostPort: number = 8880

  @input internetModule!: InternetModule
  @input remoteMediaModule!: RemoteMediaModule

  @input
  @hint("Camera the gaze test uses. Leave empty to find the main camera.")
  @allowUndefined
  cameraObject!: SceneObject

  @input
  @hint("Text showing connection state.")
  @allowUndefined
  statusText!: Text

  // ---- panels ------------------------------------------------------------

  @input
  @hint("Prefab with a HoloPanel component on its root.")
  panelPrefab!: ObjectPrefab

  @input
  @hint("Panel width in centimetres. 80 = 0.8 m.")
  panelWidthCm: number = 80

  @input
  @hint("Distance from the user in centimetres. 100 = 1 m.")
  panelDistanceCm: number = 100

  @input
  @hint("Total spread of the panel arc, in degrees.")
  arcDegrees: number = 50

  // ---- gaze tiers --------------------------------------------------------

  @input
  @hint("Within this many degrees of centre: stream at full rate.")
  fullDegrees: number = 25

  @input
  @hint("Within this many degrees: stream slowly. Beyond it: stop streaming.")
  lowDegrees: number = 45

  @input
  @hint("Extra degrees needed to leave a tier, so glancing does not flip it.")
  hysteresisDegrees: number = 5

  // ---- resize ------------------------------------------------------------

  @input
  @hint("Pixels per metre of panel. 2400 makes a 0.8 m panel about 1920 px.")
  pixelsPerMetre: number = 2400

  // ---- internals ---------------------------------------------------------

  private connection: HoloConnection | null = null
  private panels: Map<number, SceneObject> = new Map()
  private components: Map<number, HoloPanel> = new Map()
  private camera: Camera | null = null
  private gazeAccumulator = 0

  onAwake(): void {
    this.createEvent("OnStartEvent").bind(() => this.start())
    this.createEvent("UpdateEvent").bind(() => this.update())
    this.createEvent("OnDestroyEvent").bind(() => this.connection?.close())
  }

  private start(): void {
    this.camera = this.resolveCamera()
    if (this.camera === null) {
      print("HoloDisplays: no camera found; gaze tiers will not work")
    }

    const url = `ws://${this.hostIp}:${this.hostPort}`
    print(`HoloDisplays: connecting to ${url}`)

    this.connection = new HoloConnection(
      this.internetModule,
      url,
      {
        onStatus: (text, connected) => this.setStatus(text, connected),
        onDisplays: (list) => this.syncPanels(list),
        onFrame: (header, blob) => this.applyFrame(header, blob),
        onModeChanged: (id, w, h, ok, err) => this.onModeChanged(id, w, h, ok, err)
      },
      (seconds, fn) => this.after(seconds, fn)
    )

    this.connection.connect()
  }

  // ---- panels ------------------------------------------------------------

  /** Spawns panels for new displays and removes panels for ones that vanished. */
  private syncPanels(list: DisplayInfo[]): void {
    const seen = new Set<number>()

    list.forEach((info, index) => {
      seen.add(info.id)

      let panel = this.components.get(info.id)
      const isNew = panel === undefined
      if (panel === undefined) {
        const object = this.panelPrefab.instantiate(this.getSceneObject())
        object.name = `Panel ${info.id} ${info.name}`

        const component = object.getComponent(HoloPanel.getTypeName()) as HoloPanel
        if (component === null || component === undefined) {
          print(`HoloDisplays: panel prefab has no HoloPanel component`)
          object.destroy()
          return
        }

        component.onResizeReleased = (aspect, widthCm) =>
          this.requestResize(info.id, aspect, widthCm)

        this.panels.set(info.id, object)
        this.components.set(info.id, component)
        panel = component

        this.placeOnArc(object, index, list.length)
      }

      // New panels get the configured width; existing ones keep whatever width
      // the user dragged them to, and only their aspect is corrected.
      panel.configure(info, isNew ? this.panelWidthCm : undefined)
    })

    // Remove panels whose display is gone.
    for (const id of Array.from(this.components.keys())) {
      if (seen.has(id)) continue
      this.panels.get(id)?.destroy()
      this.panels.delete(id)
      this.components.delete(id)
    }

    this.setStatus(`${list.length} display${list.length === 1 ? "" : "s"}`, true)
  }

  /** Lays panels out on a gentle arc at eye height, facing the user. */
  private placeOnArc(object: SceneObject, index: number, total: number): void {
    const spread = total > 1 ? this.arcDegrees : 0
    const step = total > 1 ? spread / (total - 1) : 0
    const degrees = -spread / 2 + step * index
    const radians = (degrees * Math.PI) / 180

    const x = Math.sin(radians) * this.panelDistanceCm
    const z = -Math.cos(radians) * this.panelDistanceCm

    const transform = object.getTransform()
    transform.setLocalPosition(new vec3(x, 0, z))
    // Turn each panel to face the user rather than all facing the same way.
    transform.setLocalRotation(quat.fromEulerAngles(0, radians, 0))
  }

  // ---- frames ------------------------------------------------------------

  private applyFrame(header: FrameHeader, blob: Blob): void {
    const panel = this.components.get(header.id)
    if (panel === undefined) return

    this.blobToTexture(blob)
      .then((texture) => {
        panel.setTexture(texture)
        // Ack only now: the host's one-frame-in-flight rule measures time to
        // on-screen, not time to arrival.
        this.connection?.sendAck(header.id, header.seq)
      })
      .catch((e) => {
        print(`HoloDisplays: could not decode frame for ${header.id}: ${e}`)
        // Ack anyway, or the host stalls on this display until its timeout.
        this.connection?.sendAck(header.id, header.seq)
      })
  }

  /**
   * Blob to Texture.
   *
   * makeResourceFromBlob is deprecated as of Lens Scripting 362 in favour of
   * DynamicResource.createWithBuffer, so prefer that and fall back for older
   * runtimes.
   */
  private async blobToTexture(blob: Blob): Promise<Texture> {
    let resource: DynamicResource

    const dynamic = (global as any).DynamicResource
    if (dynamic !== undefined && dynamic.createWithBuffer !== undefined) {
      const bytes = await blob.bytes()
      resource = dynamic.createWithBuffer(bytes)
    } else {
      resource = (this.internetModule as any).makeResourceFromBlob(blob)
    }

    return new Promise<Texture>((resolve, reject) => {
      this.remoteMediaModule.loadResourceAsImageTexture(
        resource,
        (texture: Texture) => resolve(texture),
        (error: string) => reject(error)
      )
    })
  }

  // ---- gaze tiers --------------------------------------------------------

  private update(): void {
    this.gazeAccumulator += getDeltaTime()
    if (this.gazeAccumulator < 0.1) return // ~10 Hz is plenty
    this.gazeAccumulator = 0
    this.updateTiers()
  }

  /**
   * Picks a tier per panel from head direction, with hysteresis so a panel near
   * a threshold does not oscillate.
   *
   * Head direction, not eye tracking: it is steadier, and the point is to stop
   * streaming things behind you rather than to track a glance.
   */
  private updateTiers(): void {
    if (this.camera === null || this.connection === null || !this.connection.isOpen) return

    const transform = this.camera.getSceneObject().getTransform()
    const position = transform.getWorldPosition()
    const forward = transform.back // a property, not a method; cameras look down -Z

    this.components.forEach((panel, id) => {
      const angle = panel.angleFrom(position, forward)
      const tier = this.tierFor(angle, panel.tier)
      if (tier === panel.tier) return

      panel.tier = tier
      this.connection!.sendVisibility(id, tier)
    })
  }

  private tierFor(angle: number, current: Tier): Tier {
    const h = this.hysteresisDegrees

    // Widen the band you are already in, so leaving costs more than entering.
    const full = current === "full" ? this.fullDegrees + h : this.fullDegrees
    const low = current === "off" ? this.lowDegrees - h : this.lowDegrees + (current === "low" ? h : 0)

    if (angle <= full) return "full"
    if (angle <= low) return "low"
    return "off"
  }

  // ---- resize ------------------------------------------------------------

  /**
   * Turns a released panel size into a resolution request.
   *
   * Closest aspect ratio first, because a panel that looks 16:9 should not
   * become a portrait monitor. Among modes of similar aspect, the one nearest
   * the panel's physical size in pixels.
   */
  private requestResize(id: number, aspect: number, widthCm: number): void {
    const panel = this.components.get(id)
    const info = panel?.info
    if (panel === undefined || info === undefined || info === null) return
    if (info.modes.length === 0) return

    const wantedPixels = (widthCm / 100) * this.pixelsPerMetre

    let best: [number, number] | null = null
    let bestScore = Number.MAX_VALUE

    for (const mode of info.modes) {
      const modeAspect = mode[0] / mode[1]
      const aspectError = Math.abs(Math.log(modeAspect / aspect))
      const sizeError = Math.abs(mode[0] - wantedPixels) / Math.max(1, wantedPixels)

      // Aspect dominates; size breaks ties between similar shapes.
      const score = aspectError * 10 + sizeError
      if (score < bestScore) {
        bestScore = score
        best = mode
      }
    }

    if (best === null) return
    if (best[0] === info.w && best[1] === info.h) {
      // Already there: just tidy the aspect and send nothing.
      panel.applyAspect(info.w / info.h)
      return
    }

    panel.setSwitching(true)
    this.connection?.sendResize(id, best[0], best[1])
  }

  private onModeChanged(id: number, w: number, h: number, ok: boolean, err?: string): void {
    const panel = this.components.get(id)
    if (panel === undefined) return

    panel.setSwitching(false)

    if (ok) {
      panel.applyAspect(w / h)
    } else {
      print(`HoloDisplays: resize of ${id} failed: ${err}`)
      // Put the panel back to the aspect the display actually has.
      const info = panel.info
      if (info !== null) panel.applyAspect(info.w / info.h)
    }
  }

  // ---- helpers -----------------------------------------------------------

  private resolveCamera(): Camera | null {
    if (this.cameraObject !== null && this.cameraObject !== undefined) {
      const camera = this.cameraObject.getComponent("Component.Camera") as Camera
      if (camera !== null) return camera
    }
    return this.findCameraInScene()
  }

  private findCameraInScene(): Camera | null {
    const count = global.scene.getRootObjectsCount()
    for (let i = 0; i < count; i++) {
      const root = global.scene.getRootObject(i)
      const camera = this.searchForCamera(root)
      if (camera !== null) return camera
    }
    return null
  }

  private searchForCamera(object: SceneObject): Camera | null {
    const camera = object.getComponent("Component.Camera") as Camera
    if (camera !== null) return camera
    for (let i = 0; i < object.getChildrenCount(); i++) {
      const found = this.searchForCamera(object.getChild(i))
      if (found !== null) return found
    }
    return null
  }

  private setStatus(text: string, connected: boolean): void {
    print(`HoloDisplays: ${text}`)
    if (this.statusText === null || this.statusText === undefined) return
    this.statusText.text = text
    this.statusText.textFill.color = connected
      ? new vec4(0.32, 0.81, 0.4, 1)
      : new vec4(1, 0.42, 0.42, 1)
  }

  /** One-shot delay, used for reconnect backoff. */
  private after(seconds: number, fn: () => void): void {
    const event = this.createEvent("DelayedCallbackEvent")
    event.bind(() => fn())
    event.reset(seconds)
  }
}
