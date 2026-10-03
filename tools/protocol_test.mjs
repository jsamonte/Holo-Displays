// Headless protocol check for the host.
//
// The HTML test client needs eyes on it; this one gives a pass/fail from a
// terminal, which is what you want in a build loop or when testing over SSH.
// Uses Node's built-in WebSocket (Node 22+), so there is nothing to install.
//
//   node tools/protocol_test.mjs [ws://host:port]

const URL_ = process.argv[2] ?? 'ws://127.0.0.1:8800';
const RUN_MS = 9000;

const state = {
  displays: null,
  framesByDisplay: new Map(),
  bytesByDisplay: new Map(),
  pending: null,
  badBinary: 0,
  orphanBinary: 0,
  modeChanged: [],
  tierMarks: [],
  gaps: new Map(),          // "id|tier" -> [ms between consecutive frames]
  lastArrival: new Map(),   // id -> {at, phase}
};

/** Smallest gap seen, which is what a tier's rate limit actually bounds. */
const minGap = list => (list?.length ? Math.min(...list) : null);

const problems = [];
const ok = [];

function note(list, msg) { list.push(msg); console.log((list === ok ? '  ok   ' : '  FAIL ') + msg); }

console.log(`connecting to ${URL_}`);
const ws = new WebSocket(URL_);
ws.binaryType = 'arraybuffer';

const started = Date.now();

ws.onopen = () => {
  console.log('open; sending hello\n');
  ws.send(JSON.stringify({ t: 'hello', client: 'protocol-test', ver: 1 }));
};

ws.onerror = () => {
  console.error(`\ncould not connect to ${URL_}`);
  console.error('Is the host running? It listens once the Godot project is playing.');
  process.exit(2);
};

ws.onclose = e => {
  if (Date.now() - started < RUN_MS - 500) {
    console.error(`\nsocket closed early (code ${e.code})`);
    process.exit(2);
  }
};

ws.onmessage = ev => {
  if (typeof ev.data !== 'string') return onBinary(ev.data);

  let msg;
  try { msg = JSON.parse(ev.data); } catch { problems.push('unparseable JSON'); return; }

  if (msg.t === 'displays') {
    const first = state.displays === null;
    state.displays = msg.list;
    console.log(`displays: ${msg.list.length}`);
    for (const d of msg.list) {
      console.log(`  id ${d.id}  ${d.name}  ${d.w}x${d.h}  ${d.modes.length} modes`);
    }
    console.log('');

    if (first) {
      if (msg.list.length === 0) {
        console.log('Host is streaming no displays. Tick one in the host UI,');
        console.log('or install VDD so there is a virtual monitor to stream.\n');
      }
      // Ask for everything at full and see what arrives.
      for (const d of msg.list) {
        ws.send(JSON.stringify({ t: 'visibility', id: d.id, tier: 'full' }));
      }
      state.tierMarks.push({ at: Date.now(), tier: 'full' });

      // Halfway through, drop to low and check the rate actually falls.
      setTimeout(() => {
        console.log('-> switching all displays to tier "low"\n');
        for (const d of msg.list) {
          ws.send(JSON.stringify({ t: 'visibility', id: d.id, tier: 'low' }));
        }
        state.tierMarks.push({ at: Date.now(), tier: 'low' });
      }, RUN_MS / 2);
    }
  } else if (msg.t === 'frame') {
    if (state.pending) state.badBinary++;   // header with no binary after it
    state.pending = msg;
  } else if (msg.t === 'mode_changed') {
    state.modeChanged.push(msg);
    console.log(`mode_changed id ${msg.id} -> ${msg.w}x${msg.h} ok=${msg.ok}${msg.err ? ' ' + msg.err : ''}`);
  }
};

function onBinary(buf) {
  const h = state.pending;
  state.pending = null;
  if (!h) { state.orphanBinary++; return; }

  const bytes = new Uint8Array(buf);
  // JPEG starts FF D8 FF and ends FF D9.
  const jpegOk = bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff;
  if (!jpegOk) state.badBinary++;

  const phase = state.tierMarks.at(-1)?.tier ?? 'full';
  const key = `${h.id}|${phase}`;
  state.framesByDisplay.set(key, (state.framesByDisplay.get(key) ?? 0) + 1);
  state.bytesByDisplay.set(key, (state.bytesByDisplay.get(key) ?? 0) + bytes.length);

  // Record arrival times so the tiers can be checked by the gap between
  // frames rather than by how many arrived. Frame COUNT is not a valid
  // measure of the tier: the host skips unchanged frames, so on a static
  // desktop the count reflects how much the screen moved, not the rate
  // limit. A slow tier can legitimately out-count a fast one, because its
  // longer interval gives the screen more time to change between captures.
  // The gap is the thing the tier actually controls.
  const now = Date.now();
  if (!state.gaps.has(key)) state.gaps.set(key, []);
  const last = state.lastArrival.get(h.id);
  if (last !== undefined && last.phase === phase) state.gaps.get(key).push(now - last.at);
  state.lastArrival.set(h.id, {at: now, phase});

  ws.send(JSON.stringify({ t: 'ack', id: h.id, seq: h.seq }));
}

setTimeout(() => {
  console.log('\n--- results ---');

  if (state.displays === null) {
    note(problems, 'no "displays" message after hello');
  } else {
    note(ok, `received displays (${state.displays.length})`);
  }

  const halfSec = RUN_MS / 2 / 1000;

  if (state.displays?.length) {
    for (const d of state.displays) {
      const full = state.framesByDisplay.get(`${d.id}|full`) ?? 0;
      const low = state.framesByDisplay.get(`${d.id}|low`) ?? 0;
      const kb = ((state.bytesByDisplay.get(`${d.id}|full`) ?? 0) / 1024).toFixed(0);

      const fullGap = minGap(state.gaps.get(`${d.id}|full`));
      const lowGap = minGap(state.gaps.get(`${d.id}|low`));

      console.log(`  display ${d.id}: full ${full} frames (${kb} KB, min gap ${fullGap ?? '-'} ms), ` +
                  `low ${low} frames (min gap ${lowGap ?? '-'} ms)`);

      if (full === 0 && low === 0) {
        note(problems, `display ${d.id} sent no frames at all`);
        continue;
      }

      note(ok, `display ${d.id} streamed`);

      // Tiers are rate LIMITS. Check the floor on the interval, not the count:
      // unchanged frames are skipped, so counts measure screen activity.
      // Allowing 15% slack for scheduling jitter.
      if (fullGap !== null && fullGap < 1000 / 15 * 0.85) {
        note(problems, `display ${d.id}: frames arrived ${fullGap} ms apart at "full", faster than the 15 fps limit`);
      } else if (fullGap !== null) {
        note(ok, `display ${d.id} respected the "full" rate limit (min gap ${fullGap} ms)`);
      }

      if (lowGap !== null && lowGap < 1000 / 2 * 0.85) {
        note(problems, `display ${d.id}: frames arrived ${lowGap} ms apart at "low", faster than the 2 fps limit`);
      } else if (lowGap !== null) {
        note(ok, `display ${d.id} respected the "low" rate limit (min gap ${lowGap} ms)`);
      }
    }
  } else {
    console.log('  (no displays streamed, so nothing to measure)');
  }

  if (state.badBinary) note(problems, `${state.badBinary} frames were not valid JPEG, or a header had no binary`);
  else note(ok, 'every frame was a well-formed JPEG preceded by its header');

  if (state.orphanBinary) note(problems, `${state.orphanBinary} binary messages arrived with no header`);
  else note(ok, 'no orphaned binary messages');

  console.log('');
  if (problems.length) {
    console.log(`FAILED: ${problems.length} problem(s)`);
    process.exit(1);
  }
  console.log('PASSED');
  process.exit(0);
}, RUN_MS);
