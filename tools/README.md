# Test client

A stand-in for the glasses, so the host can be tested without wearing the
Spectacles. Connects to the host over WebSocket, shows each display's stream in a
grid, and has buttons to set tiers and request resolution changes.

**Built in M2.** See [../docs/PROTOCOL.md](../docs/PROTOCOL.md) for what it has
to speak.

It is also the reference implementation of the protocol: when the lens
misbehaves, check the same action here first. If it works here and not on the
glasses, the bug is in the lens, not the host.

## What is here

| File | What it does |
| --- | --- |
| `test_client.html` | Open in a browser. Shows each display's stream in a grid, with tier buttons and a resolution picker per display. |
| `protocol_test.mjs` | `node tools/protocol_test.mjs ws://<LAN-IP>:8880` — headless pass/fail check of the whole protocol. No dependencies (Node 22+ has WebSocket built in). |

**Use the laptop's LAN IP, not `127.0.0.1`.** Lens Studio holds
`127.0.0.1:8880` while it is open, so a loopback connection reaches Lens Studio
instead of the host and fails the handshake. See `docs/PROTOCOL.md`.

With no virtual monitors installed the host streams nothing, because it never
offers the primary display by default. To exercise the frame path anyway, start
the host with `HOLO_STREAM_PRIMARY=1`.
