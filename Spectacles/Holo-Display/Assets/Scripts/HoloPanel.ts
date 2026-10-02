import {Frame} from "SpectaclesUIKit.lspkg/Scripts/Components/Frame/Frame"
import {DisplayInfo, Tier} from "./HoloConnection"

/**
 * One floating monitor.
 *
 * Goes on the root of the panel prefab. The UI Kit Frame handles drag and
 * corner-scale; this adds the display's picture, the gaze tier, and the
 * translation from "how big did they make the panel" to "what resolution should
 * Windows switch to".
 *
 * Lens Studio world units are centimetres, so 100 = 1 m. Frame.innerSize is in
 * local-space centimetres too.
 */
@component
export class HoloPanel extends BaseScriptComponent {
  @input
  @hint("UI Kit Frame on this prefab. Provides drag and corner resize.")
  frame!: Frame

  @input
  @hint("Image showing the streamed desktop. Should be the Frame's content.")
  image!: Image

  @input
  @hint("Label shown while a resolution change is in flight.")
  switchingLabel!: SceneObject

  // --- display state ---
  private _info: DisplayInfo | null = null
  private _tier: Tier = "off"
  private _lastTexture: Texture | null = null

  /** Fires when the user finishes scaling, with the panel's new aspect and width in cm. */
  onResizeReleased: ((aspect: number, widthCm: number) => void) | null = null

  get info(): DisplayInfo | null {
    return this._info
  }

  get tier(): Tier {
    return this._tier
  }

  set tier(value: Tier) {
    this._tier = value
  }

  onAwake(): void {
    this.setSwitching(false)

    this.createEvent("OnStartEvent").bind(() => {
      // Give each panel its own material instance, or every panel ends up
      // showing whichever display drew last.
      if (this.image !== null && this.image.mainMaterial !== null) {
        this.image.mainMaterial = this.image.mainMaterial.clone()
      }

      if (this.frame !== null) {
        this.frame.onScalingEnd.add(() => this.handleScalingEnd())
      }
    })
  }

  /**
   * Sizes the panel to the display's aspect and remembers which display it is.
   * Omit widthCm to keep whatever width the panel already has, which is what
   * you want when a display's mode changed but the user sized the panel.
   */
  configure(info: DisplayInfo, widthCm?: number): void {
    this._info = info
    this.applyAspect(info.w / info.h, widthCm)
  }

  /** Snaps the panel to an exact aspect ratio, keeping its current width. */
  applyAspect(aspect: number, widthCm?: number): void {
    if (this.frame === null) return
    const w = widthCm ?? this.frame.innerSize.x
    this.frame.innerSize = new vec2(w, w / aspect)
  }

  setTexture(texture: Texture): void {
    if (this.image === null) return
    this.image.mainPass.baseTex = texture

    // Drop the previous one so textures do not accumulate over a long session.
    this._lastTexture = texture
  }

  setSwitching(on: boolean): void {
    if (this.switchingLabel !== null) this.switchingLabel.enabled = on
  }

  /**
   * Angle in degrees between a forward vector and this panel, reduced by the
   * panel's angular half-size.
   *
   * Subtracting the half-size is what makes a big panel count as "being looked
   * at" when any part of it is near the centre of view, rather than only its
   * exact middle.
   */
  angleFrom(cameraPosition: vec3, cameraForward: vec3): number {
    const centre = this.getTransform().getWorldPosition()
    const toPanel = centre.sub(cameraPosition)
    const distance = toPanel.length
    if (distance < 0.001) return 0

    const cos = Math.max(-1, Math.min(1, toPanel.normalize().dot(cameraForward)))
    const angle = (Math.acos(cos) * 180) / Math.PI

    // Half the panel's diagonal, as an angle at this distance.
    const size = this.frame !== null ? this.frame.innerSize : new vec2(40, 25)
    const halfDiagonal = Math.sqrt(size.x * size.x + size.y * size.y) / 2
    const halfAngle = (Math.atan2(halfDiagonal, distance) * 180) / Math.PI

    return Math.max(0, angle - halfAngle)
  }

  private handleScalingEnd(): void {
    if (this.frame === null || this.onResizeReleased === null) return
    const size = this.frame.innerSize
    if (size.y <= 0) return
    this.onResizeReleased(size.x / size.y, size.x)
  }
}
