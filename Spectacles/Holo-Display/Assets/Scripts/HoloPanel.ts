import {Frame} from "SpectaclesUIKit.lspkg/Scripts/Components/Frame/Frame"
import {DisplayInfo, Tier} from "./HoloConnection"

/**
 * One floating monitor, built entirely in code.
 *
 * Deliberately NOT a @component and NOT a prefab. Lens Studio can create scene
 * objects, mesh visuals and text at run time, so a panel needs no authored
 * asset at all — which means the lens scene is one empty object with
 * HoloDisplays on it, and nothing to wire by hand.
 *
 * What that costs: UI Kit's Frame cannot be attached in code (Lens Studio only
 * exposes a generic `createComponent("ScriptComponent")`, with no way to bind a
 * particular TypeScript class), so these panels do not drag or corner-resize
 * yet. The streaming works; the interaction is the next step.
 *
 * Units are centimetres, so 100 = 1 m.
 */
export class HoloPanel {
  readonly object: SceneObject
  private readonly visual: RenderMeshVisual
  private readonly statusObject: SceneObject
  private readonly statusText: Text

  private _info: DisplayInfo
  private _tier: Tier = "off"
  private _widthCm: number

  /** UI Kit Frame, when the panel came from a prefab that has one. */
  private readonly frame: Frame | null = null

  /** Fires when the user finishes resizing, with the new aspect and width in cm. */
  onResizeReleased: ((aspect: number, widthCm: number) => void) | null = null

  constructor(
    parent: SceneObject,
    info: DisplayInfo,
    widthCm: number,
    mesh: RenderMesh,
    material: Material,
    prefab?: ObjectPrefab
  ) {
    this._info = info
    this._widthCm = widthCm

    if (prefab !== undefined && prefab !== null) {
      // A prefab can carry a UI Kit Frame, which is the only way to get drag
      // and corner-resize: Frame is a TypeScript component and Lens Studio has
      // no way to attach one of those at run time.
      this.object = prefab.instantiate(parent)
      this.object.name = `Panel ${info.id} ${info.name}`
      this.frame = this.object.getComponent(Frame.getTypeName()) as Frame
      if (this.frame !== null && this.frame !== undefined) {
        this.frame.onScalingEnd.add(() => this.handleScalingEnd())
      }
    } else {
      this.object = global.scene.createSceneObject(`Panel ${info.id} ${info.name}`)
      this.object.setParent(parent)
    }

    this.visual = this.findOrCreateVisual(mesh, material)

    // A small label that only shows while a resolution change is in flight.
    this.statusObject = global.scene.createSceneObject("Switching")
    this.statusObject.setParent(this.object)
    this.statusText = this.statusObject.createComponent("Component.Text")
    this.statusText.text = "switching…"
    this.statusText.size = 24
    this.statusObject.enabled = false
    // Sit slightly in front of the panel so it is not z-fighting the image.
    this.statusObject.getTransform().setLocalPosition(new vec3(0, 0, 1))

    this.applyAspect(info.w / info.h)
  }

  get info(): DisplayInfo {
    return this._info
  }

  get tier(): Tier {
    return this._tier
  }

  set tier(value: Tier) {
    this._tier = value
  }

  /** Points this panel at a (possibly changed) display. */
  configure(info: DisplayInfo): void {
    this._info = info
    this.applyAspect(info.w / info.h)
  }

  /**
   * Reuses the prefab's mesh visual if it has one, otherwise makes a plane.
   *
   * A prefab built for this will usually already carry the thing that shows the
   * picture; creating a second one would leave an invisible quad fighting it.
   */
  private findOrCreateVisual(mesh: RenderMesh, material: Material): RenderMeshVisual {
    const existing = this.object.getComponent("Component.RenderMeshVisual") as RenderMeshVisual
    if (existing !== null && existing !== undefined) {
      existing.mainMaterial = existing.mainMaterial.clone()
      return existing
    }

    const visual = this.object.createComponent("Component.RenderMeshVisual")
    visual.mesh = mesh
    // Clone, or every panel shares one material and shows whichever display
    // drew last.
    visual.mainMaterial = material.clone()
    return visual
  }

  /**
   * Sizes the panel to an aspect ratio, keeping its width.
   *
   * With a Frame, size is the Frame's innerSize in centimetres. Without one,
   * the mesh is a unit plane so local scale is the size directly — in both
   * cases there is no hidden base dimension.
   */
  applyAspect(aspect: number, widthCm?: number): void {
    if (widthCm !== undefined) this._widthCm = widthCm
    const w = this._widthCm
    const h = aspect > 0 ? w / aspect : w

    if (this.frame !== null && this.frame !== undefined) {
      this.frame.innerSize = new vec2(w, h)
    } else {
      this.object.getTransform().setLocalScale(new vec3(w, h, 1))
    }
  }

  private handleScalingEnd(): void {
    if (this.frame === null || this.onResizeReleased === null) return
    const size = this.frame.innerSize
    if (size.y <= 0) return
    this._widthCm = size.x
    this.onResizeReleased(size.x / size.y, size.x)
  }

  setTexture(texture: Texture): void {
    // Assigning baseTex is what releases the previous frame; nothing else holds
    // a reference to it. At 15 fps this runs 900 times a minute per panel, so
    // if memory climbs over a long session, look here first.
    this.visual.mainPass.baseTex = texture
  }

  setSwitching(on: boolean): void {
    this.statusObject.enabled = on
  }

  destroy(): void {
    this.object.destroy()
  }

  /**
   * Angle in degrees between a forward vector and this panel, reduced by the
   * panel's angular half-size.
   *
   * Subtracting the half-size is what lets a large panel count as "being looked
   * at" when any part of it is near the centre of view, rather than only its
   * exact middle.
   */
  angleFrom(cameraPosition: vec3, cameraForward: vec3): number {
    const centre = this.object.getTransform().getWorldPosition()
    const toPanel = centre.sub(cameraPosition)
    const distance = toPanel.length
    if (distance < 0.001) return 0

    const cos = Math.max(-1, Math.min(1, toPanel.normalize().dot(cameraForward)))
    const angle = (Math.acos(cos) * 180) / Math.PI

    const scale = this.object.getTransform().getLocalScale()
    const halfDiagonal = Math.sqrt(scale.x * scale.x + scale.y * scale.y) / 2
    const halfAngle = (Math.atan2(halfDiagonal, distance) * 180) / Math.PI

    return Math.max(0, angle - halfAngle)
  }
}
