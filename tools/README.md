# Test client

A stand-in for the glasses, so the host can be tested without wearing the
Spectacles. Connects to the host over WebSocket, shows each display's stream in a
grid, and has buttons to set tiers and request resolution changes.

**Built in M2.** See [../docs/PROTOCOL.md](../docs/PROTOCOL.md) for what it has
to speak.

It is also the reference implementation of the protocol: when the lens
misbehaves, check the same action here first. If it works here and not on the
glasses, the bug is in the lens, not the host.
