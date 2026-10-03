import {DisplayInfo, FrameHeader, HoloConnection, Tier} from "./HoloConnection"
import {HoloPanel} from "./HoloPanel"
import {HoloSettings} from "./HoloSettings"

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
  @hint("Only a fallback. The address is asked for on the glasses and remembered.")
  hostIp: string = "192.168.1.10"

  @input
  @hint("Fallback port. 8800 unless the host says it fell forward to another.")
  hostPort: number = 8800

  @input
  @hint("Virtual monitors to ask the host for. Asked on the glasses the first time.")
  displayCount: number = 2

  @input
  @hint("Ask for the address and display count every launch, not just the first.")
  askEveryLaunch: boolean = false

  @input
  @hint("Optional. Left empty, the module is obtained in code.")
  @allowUndefined
  internetModule!: InternetModule

  @input
  @hint("Optional. Left empty, the module is obtained in code.")
  @allowUndefined
  remoteMediaModule!: RemoteMediaModule

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
  @hint("Optional. Leave empty to use UI Kit's unit plane.")
  @allowUndefined
  panelMesh!: RenderMesh

  @input
  @hint("Optional. Leave empty to use UI Kit's image material.")
  @allowUndefined
  panelMaterial!: Material

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

  /**
   * The modules actually used, after resolution.
   *
   * Preferring `require("LensStudio:…")` over an inspector input means the two
   * module assets never have to be created and wired by hand, which is two
   * fewer setup steps and two fewer things to get wrong. SIK and UI Kit obtain
   * GestureModule, TextInputModule and others the same way.
   *
   * The inputs are kept as an override in case a project wants a specific
   * instance, and because if the require name is ever wrong, wiring the input
   * is the escape hatch rather than a dead end.
   */
  private internet!: InternetModule
  private remoteMedia!: RemoteMediaModule

  /** The mesh and material every panel is built from. */
  private mesh!: RenderMesh
  private material!: Material

  private readonly settings = new HoloSettings()
  private connection: HoloConnection | null = null
  private panels: Map<number, HoloPanel> = new Map()
  private camera: Camera | null = null
  private gazeAccumulator = 0

  onAwake(): void {
    this.createEvent("OnStartEvent").bind(() => this.start())
    this.createEvent("UpdateEvent").bind(() => this.update())
    this.createEvent("OnDestroyEvent").bind(() => this.connection?.close())
  }

  private start(): void {
    // Before anything that can fail, so a failure has somewhere to be shown.
    try {
      this.ensureStatusLabel()
    } catch (e) {
      print(`HoloDisplays: could not create the status label: ${e}`)
    }

    try {
      this.startInner()
    } catch (e) {
      // An uncaught exception here would leave a blank lens and no clue why.
      this.setStatus(`error: ${e}`, false)
      print(`HoloDisplays: start failed: ${e}`)
    }
  }

  private startInner(): void {
    this.setStatus("starting", false)

    if (!this.resolveModules()) return
    if (!this.resolvePanelAssets()) return

    this.camera = this.resolveCamera()
    if (this.camera === null) {
      print("HoloDisplays: no camera found; gaze tiers will not work")
    }

    // Ask on the glasses the first time, or whenever the inspector says to.
    // After that the stored answers are used and the lens just connects.
    if (this.askEveryLaunch || !this.settings.hasHost) {
      this.setStatus("enter host address", false)
      this.settings.askForHost(this.settings.getHostIp(this.hostIp), (ip, port) => {
        if (ip.length > 0) this.settings.setHostIp(ip)
        if (port !== null) this.settings.setPort(port)

        this.settings.askForDisplayCount(
          this.settings.getDisplayCount(this.displayCount),
          (count) => {
            this.settings.setDisplayCount(count)
            this.connectNow()
          }
        )
      })
      return
    }

    this.connectNow()
  }

  private connectNow(): void {
    const ip = this.settings.getHostIp(this.hostIp)
    const port = this.settings.getPort(this.hostPort)
    const url = `ws://${ip}:${port}`
    print(`HoloDisplays: connecting to ${url}`)

    this.connection = new HoloConnection(
      this.internet,
      url,
      {
        onStatus: (text, connected) => this.onStatus(text, connected),
        onDisplays: (list) => this.syncPanels(list),
        onFrame: (header, blob) => this.applyFrame(header, blob),
        onModeChanged: (id, w, h, ok, err) => this.onModeChanged(id, w, h, ok, err)
      },
      (seconds, fn) => this.after(seconds, fn)
    )

    this.connection.connect()
  }

  /**
   * Tells the host how many virtual monitors to have, once connected.
   *
   * Sent on every connect rather than only on change, because the host may have
   * restarted or reloaded its driver since the lens last said anything. It is
   * idempotent on the host side.
   */
  private onStatus(text: string, connected: boolean): void {
    this.setStatus(text, connected)
    if (!connected) return
    this.connection?.sendDisplayCount(this.settings.getDisplayCount(this.displayCount))
  }

  // ---- panels ------------------------------------------------------------

  /** Spawns panels for new displays and removes panels for ones that vanished. */
  private syncPanels(list: DisplayInfo[]): void {
    const seen = new Set<number>()

    list.forEach((info, index) => {
      seen.add(info.id)

      const existing = this.panels.get(info.id)

      if (existing === undefined) {
        const panel = new HoloPanel(
          this.getSceneObject(),
          info,
          this.panelWidthCm,
          this.mesh,
          this.material
        )
        this.panels.set(info.id, panel)
        this.placeOnArc(panel.object, index, list.length)
      } else {
        // An existing panel keeps its position and width. Only the aspect is
        // corrected, since the display's mode may have changed under it.
        existing.configure(info)
      }
    })

    // Remove panels whose display is gone.
    for (const id of Array.from(this.panels.keys())) {
      if (seen.has(id)) continue
      this.panels.get(id)?.destroy()
      this.panels.delete(id)
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
    const position = new vec3(x, 0, z)

    const transform = object.getTransform()
    transform.setLocalPosition(position)

    // Aim each panel back at the user, rather than deriving a Y rotation from
    // the arc angle. Working the angle out by hand means guessing a sign
    // convention, and getting it wrong turns every panel away from the viewer
    // while still looking plausible in code. Pointing at a known target cannot
    // have that bug.
    const toUser = new vec3(-x, 0, -z).normalize()
    transform.setLocalRotation(quat.lookAt(toUser, vec3.up()))
  }

  // ---- frames ------------------------------------------------------------

  private applyFrame(header: FrameHeader, blob: Blob): void {
    const panel = this.panels.get(header.id)
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
      resource = (this.internet as any).makeResourceFromBlob(blob)
    }

    return new Promise<Texture>((resolve, reject) => {
      this.remoteMedia.loadResourceAsImageTexture(
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

    this.panels.forEach((panel, id) => {
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
    const panel = this.panels.get(id)
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
    const panel = this.panels.get(id)
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

  /**
   * Resolves the two modules, preferring the inspector input and falling back
   * to requiring them by name. Returns false, having said why, if either is
   * missing — better a clear line in the log than a null dereference three
   * frames later.
   */
  private resolveModules(): boolean {
    this.internet = this.internetModule ?? HoloDisplays.tryRequire("InternetModule")
    this.remoteMedia = this.remoteMediaModule ?? HoloDisplays.tryRequire("RemoteMediaModule")

    if (this.internet === null || this.internet === undefined) {
      this.setStatus("no InternetModule", false)
      print("HoloDisplays: could not obtain an InternetModule. Add one in the Asset Browser and set it on this component.")
      return false
    }

    if (this.remoteMedia === null || this.remoteMedia === undefined) {
      this.setStatus("no RemoteMediaModule", false)
      print("HoloDisplays: could not obtain a RemoteMediaModule. Add one in the Asset Browser and set it on this component.")
      return false
    }

    return true
  }

  /**
   * Finds the mesh and material panels are built from.
   *
   * Prefers the inspector inputs, then falls back to UI Kit's own unit plane
   * and image material, which are already in the project because the package is
   * installed. The fallback path is an informed guess at how package assets are
   * addressed, so if it is wrong the inputs are the fix — a two-drag repair
   * rather than a dead end, and the log says exactly that.
   */
  private resolvePanelAssets(): boolean {
    this.mesh = this.panelMesh ?? HoloDisplays.tryRequireAsset("SpectaclesUIKit.lspkg/Meshes/Unit Plane.mesh")
    this.material = this.panelMaterial ?? HoloDisplays.tryRequireAsset("SpectaclesUIKit.lspkg/Materials/Image.mat")

    if (this.mesh === null || this.mesh === undefined) {
      this.setStatus("no panel mesh", false)
      print("HoloDisplays: could not load a mesh for the panels. Drag any plane mesh onto the Panel Mesh input.")
      return false
    }

    if (this.material === null || this.material === undefined) {
      this.setStatus("no panel material", false)
      print("HoloDisplays: could not load a material for the panels. Drag any unlit/image material onto the Panel Material input.")
      return false
    }

    return true
  }

  private static tryRequireAsset(path: string): any {
    try {
      return requireAsset(path)
    } catch (e) {
      return null
    }
  }

  private static tryRequire(name: string): any {
    try {
      return require(`LensStudio:${name}`)
    } catch (e) {
      return null
    }
  }

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

  /** Status label created in code, so there is always something on screen. */
  private ownStatus: Text | null = null

  /**
   * Makes a status label if the inspector did not supply one.
   *
   * Without this, a lens that fails to connect shows absolutely nothing — no
   * panels, no error, no sign it is even running — which is the worst possible
   * first-run experience and exactly what happened the first time.
   */
  private ensureStatusLabel(): void {
    if (this.statusText !== null && this.statusText !== undefined) return
    if (this.ownStatus !== null) return

    const object = global.scene.createSceneObject("HoloDisplays Status")
    object.setParent(this.getSceneObject())
    // A little below eye level and a metre out, so it does not sit on top of
    // the panels when they do appear.
    object.getTransform().setLocalPosition(new vec3(0, -25, -this.panelDistanceCm))

    const text = object.createComponent("Component.Text")
    text.text = "starting…"
    text.size = 36
    text.horizontalAlignment = HorizontalAlignment.Center
    this.ownStatus = text
  }

  private setStatus(text: string, connected: boolean): void {
    print(`HoloDisplays: ${text}`)

    const label = (this.statusText !== null && this.statusText !== undefined)
      ? this.statusText
      : this.ownStatus
    if (label === null || label === undefined) return

    label.text = text
    label.textFill.color = connected
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
