/**
 * WebSocket client for the host, speaking the protocol in docs/PROTOCOL.md.
 *
 * Kept free of scene knowledge on purpose: it deals in messages and hands
 * decoded frames to a callback. HoloDisplays owns the panels.
 *
 * The one subtlety is frame pairing. The host sends a JSON header and then the
 * JPEG as the very next binary message, so this remembers the last header and
 * pairs it with whatever binary arrives next. That is why the lens never has to
 * slice a binary blob to find where a header ends.
 */

export type DisplayInfo = {
  id: number
  name: string
  w: number
  h: number
  modes: [number, number][]
}

export type FrameHeader = {
  t: "frame"
  id: number
  seq: number
  w: number
  h: number
}

export type Tier = "full" | "low" | "off"

export type ConnectionCallbacks = {
  onStatus: (text: string, connected: boolean) => void
  onDisplays: (list: DisplayInfo[]) => void
  onFrame: (header: FrameHeader, blob: Blob) => void
  onModeChanged: (id: number, w: number, h: number, ok: boolean, err?: string) => void
}

export class HoloConnection {
  private socket: WebSocket | null = null
  private pendingHeader: FrameHeader | null = null
  private closedByUs = false

  /** Reconnect backoff in seconds, doubling to a ceiling. */
  private retryDelay = 1
  private static readonly MAX_RETRY = 10

  constructor(
    private readonly internetModule: InternetModule,
    private readonly url: string,
    private readonly callbacks: ConnectionCallbacks,
    private readonly scheduleRetry: (seconds: number, fn: () => void) => void
  ) {}

  connect(): void {
    this.closedByUs = false
    this.callbacks.onStatus("connecting", false)

    let socket: WebSocket
    try {
      socket = this.internetModule.createWebSocket(this.url)
    } catch (e) {
      this.callbacks.onStatus(`bad url: ${this.url}`, false)
      return
    }

    socket.binaryType = "blob"
    this.socket = socket

    socket.onopen = () => {
      this.retryDelay = 1
      this.callbacks.onStatus("connected", true)
      this.send({t: "hello", client: "spectacles", ver: 1})
    }

    socket.onmessage = async (event: any) => {
      if (typeof event.data === "string") {
        this.handleText(event.data)
        return
      }
      await this.handleBinary(event.data as Blob)
    }

    socket.onerror = () => {
      this.callbacks.onStatus("error", false)
    }

    socket.onclose = (event: any) => {
      this.socket = null
      this.pendingHeader = null
      if (this.closedByUs) return

      this.callbacks.onStatus(`disconnected (${event?.code ?? "?"})`, false)
      const delay = this.retryDelay
      this.retryDelay = Math.min(this.retryDelay * 2, HoloConnection.MAX_RETRY)
      this.scheduleRetry(delay, () => this.connect())
    }
  }

  close(): void {
    this.closedByUs = true
    this.socket?.close()
    this.socket = null
  }

  get isOpen(): boolean {
    return this.socket !== null && this.socket.readyState === 1
  }

  private handleText(text: string): void {
    let msg: any
    try {
      msg = JSON.parse(text)
    } catch (e) {
      print(`HoloDisplays: unparseable message: ${text.substr(0, 120)}`)
      return
    }

    switch (msg.t) {
      case "displays":
        this.callbacks.onDisplays(msg.list as DisplayInfo[])
        break

      case "frame":
        // The JPEG is the next message. Hold onto this until it lands.
        this.pendingHeader = msg as FrameHeader
        break

      case "mode_changed":
        this.callbacks.onModeChanged(msg.id, msg.w, msg.h, msg.ok === true, msg.err)
        break

      default:
        // Unknown types are ignored so the host can add messages without
        // breaking an older lens.
        break
    }
  }

  private async handleBinary(blob: Blob): Promise<void> {
    const header = this.pendingHeader
    this.pendingHeader = null
    if (header === null) return // binary with no header: drop it
    this.callbacks.onFrame(header, blob)
  }

  // ---- outbound ----------------------------------------------------------

  private send(obj: object): void {
    if (!this.isOpen) return
    this.socket!.send(JSON.stringify(obj))
  }

  sendVisibility(id: number, tier: Tier): void {
    this.send({t: "visibility", id, tier})
  }

  /** Sent once the frame is actually on screen, not when it arrives. */
  sendAck(id: number, seq: number): void {
    this.send({t: "ack", id, seq})
  }

  sendResize(id: number, w: number, h: number): void {
    this.send({t: "resize", id, w, h})
  }
}
