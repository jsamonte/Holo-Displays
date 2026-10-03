// Loading this module is what makes the system keyboard exist. Without it,
// requestKeyboard is silently a no-op: the lens sits on "enter host address"
// with nothing to type into, which is exactly what happened on the first run.
// UI Kit's own TextInputField requires it the same way, at file scope.
require("LensStudio:TextInputModule") // eslint-disable-line @typescript-eslint/no-require-imports

/**
 * Host address and display count, asked for on the glasses and remembered.
 *
 * The address cannot live in the inspector: a lens is built once and run
 * against whichever machine is to hand, and every machine has a different LAN
 * IP. So it is entered through the Spectacles system keyboard on first run and
 * kept in persistent storage after that.
 *
 * This also avoids needing any spatial interaction. The system keyboard is
 * provided by the OS, so asking a question costs nothing in the scene — no
 * buttons, no colliders, no components that cannot be created at run time.
 */

const KEY_HOST = "holo.hostIp"
const KEY_PORT = "holo.hostPort"
const KEY_COUNT = "holo.displayCount"

export class HoloSettings {
  private get store(): GeneralDataStore {
    return global.persistentStorageSystem.store
  }

  // ---- stored values -----------------------------------------------------

  getHostIp(fallback: string): string {
    const v = this.store.getString(KEY_HOST)
    return v !== null && v !== undefined && v.length > 0 ? v : fallback
  }

  setHostIp(value: string): void {
    this.store.putString(KEY_HOST, value)
  }

  getPort(fallback: number): number {
    const v = this.store.getInt(KEY_PORT)
    return v > 0 ? v : fallback
  }

  setPort(value: number): void {
    this.store.putInt(KEY_PORT, value)
  }

  getDisplayCount(fallback: number): number {
    const v = this.store.getInt(KEY_COUNT)
    return v > 0 ? v : fallback
  }

  setDisplayCount(value: number): void {
    this.store.putInt(KEY_COUNT, value)
  }

  /** True when an address has been entered at least once on this device. */
  get hasHost(): boolean {
    const v = this.store.getString(KEY_HOST)
    return v !== null && v !== undefined && v.length > 0
  }

  // ---- asking ------------------------------------------------------------

  /**
   * Opens the system keyboard and calls back with what was typed.
   *
   * `onReturnKeyPressed` is the commit, not `onKeyboardStateChanged` — the
   * keyboard can close for reasons that are not the user accepting, and
   * treating a dismissal as confirmation would silently store a half-typed
   * address.
   */
  /** True once a keyboard has actually opened, so callers can detect "never came". */
  keyboardAppeared = false

  private ask(
    prompt: string,
    initial: string,
    numeric: boolean,
    onDone: (text: string) => void
  ): void {
    const options = new TextInputSystem.KeyboardOptions()
    options.initialText = initial
    options.enablePreview = true
    options.keyboardType = numeric
      ? TextInputSystem.KeyboardType.Num
      : TextInputSystem.KeyboardType.Text

    let current = initial

    options.onTextChanged = (text: string) => {
      current = text
    }

    options.onReturnKeyPressed = () => {
      onDone(current.trim())
    }

    options.onKeyboardStateChanged = (open: boolean) => {
      if (open) this.keyboardAppeared = true
    }

    options.onError = (code: number, description: string) => {
      print(`HoloDisplays: keyboard error ${code}: ${description}`)
    }

    print(`HoloDisplays: ${prompt}`)

    try {
      global.textInputSystem.requestKeyboard(options)
    } catch (e) {
      print(`HoloDisplays: requestKeyboard threw: ${e}`)
    }
  }

  /** Asks for the host address, e.g. 192.168.1.42 or 192.168.1.42:8800. */
  askForHost(initial: string, onDone: (ip: string, port: number | null) => void): void {
    this.ask("enter the host IP shown in the host window", initial, false, (text) => {
      // Accept "1.2.3.4" or "1.2.3.4:8800" — people will type what they see on
      // screen, and the host displays IP:port together.
      const parts = text.split(":")
      const ip = parts[0].trim()
      const port = parts.length > 1 ? parseInt(parts[1], 10) : NaN
      onDone(ip, isNaN(port) ? null : port)
    })
  }

  /** Asks how many virtual monitors to ask the host for. */
  askForDisplayCount(initial: number, onDone: (count: number) => void): void {
    this.ask("how many virtual displays?", `${initial}`, true, (text) => {
      const n = parseInt(text, 10)
      if (!isNaN(n) && n >= 0 && n <= 8) onDone(n)
      else print(`HoloDisplays: ignoring display count "${text}" — expected 0 to 8`)
    })
  }
}
