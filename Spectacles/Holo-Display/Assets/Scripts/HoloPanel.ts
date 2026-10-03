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

  constructor(
    parent: SceneObject,
    info: DisplayInfo,
    widthCm: number,
    mesh: RenderMesh,
    material: Material
  ) {
    this._info = info
    this._widthCm = widthCm

    this.object = global.scene.createSceneObject(`Panel ${info.id} ${info.name}`)
    this.object.setParent(parent)

    this.visual = this.object.createComponent("Component.RenderMeshVisual")
    this.visual.mesh = mesh
    // Clone, or every panel shares one material and shows whichever display
    // drew last.
    this.visual.mainMaterial = material.clone()

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
   * Sizes the panel to an aspect ratio, keeping its width.
   *
   * The mesh is a unit plane, so local scale is the size in centimetres
   * directly — no hidden base dimension to account for.
   */
  applyAspect(aspect: number, widthCm?: number): void {
    if (widthCm !== undefined) this._widthCm = widthCm
    const w = this._widthCm
    const h = aspect > 0 ? w / aspect : w
    this.object.getTransform().setLocalScale(new vec3(w, h, 1))
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
