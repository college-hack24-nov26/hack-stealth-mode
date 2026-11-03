"""Stealth Mode relay: WHOOP heart rate + mission events -> debrief app, only while a mission is running.

Leave this running on the Mac (double-click "StealthMode Relay.command", Terminal.app has the Bluetooth permission).
It sits idle: no Bluetooth, no uploads. Everything starts when the Engineer presses the RIGHT TRIGGER in the headset
and ends with the mission:

    idle --(Quest sends mission_start)--> RUNNING --(mission_end | headset silent 30 s | Ctrl-C)--> idle

RUNNING = connect to the WHOOP (standard Bluetooth Heart Rate Service, Broadcast Heart Rate ON in the WHOOP app),
open a session in the debrief app, then once a second upload heart rate (+ R-R intervals for HRV) and the event
timeline. Mission end = upload what is left, close the session, disconnect the WHOOP, back to idle. No manual
start/stop, no run left "in progress".

    Quest -> Mac  {"type":"hello"} (until heard) | {"type":"beat","mid":M} every 2 s |
                  {"type":"event","mid":M,"n":N,"ev":..,"actor":..,"t_ms":..,"data":{..}}
    Mac -> Quest  {"hr":72,"level":0-3,"baseline":68} each second | {"type":"ack","n":N} per event
Two transports carry the same JSON: Bluetooth LE (the headset; this Mac advertises the "StealthRelay" GATT service, so the
venue Wi-Fi does not matter) and UDP 5006/5005 over Wi-Fi (the Unity editor, or a headset on a friendly network). Replies go
back over the channel the message came in on.

Events carry a per-mission sequence number n: the relay acks each one (the Quest resends until acked), drops
duplicates, and waits for gaps before closing, so nothing is lost or doubled. Debrief credentials are read from
web-app/.env.local. Logs: mac/sessions/relay.log, whoop_*.csv, events_*.csv.

    python3 mac/whoop_relay.py [--name "Pulin Chaudhari"]
    --simulate   fake heart rate instead of the WHOOP (tests)      --dry-run   no uploads (tests)      --no-ble   Wi-Fi only
"""
import argparse, asyncio, csv, json, math, os, random, signal, socket, statistics, time, urllib.error, urllib.request

HR_SERVICE = "0000180d-0000-1000-8000-00805f9b34fb"
HR_MEASUREMENT = "00002a37-0000-1000-8000-00805f9b34fb"
HR_PORT, EVENT_PORT = 5005, 5006
BASELINE_S = 45          # resting window: the first seconds of the mission (briefing)
LEVELS = (8, 18, 30)     # bpm above baseline for stress level 1, 2, 3

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "sessions")
os.makedirs(OUT, exist_ok=True)
LOG = os.path.join(OUT, "relay.log")
ADDR_CACHE = os.path.join(OUT, ".whoop_address")


def log(msg):
    line = f"{time.strftime('%H:%M:%S')}  {msg}"
    print(line, flush=True)
    try:
        with open(LOG, "a") as f:
            f.write(line + "\n")
    except OSError:
        pass


def parse_hr(data):
    """Heart Rate Measurement (0x2A37): flags bit0 uint16 bpm, bit3 energy field, bit4 R-R intervals (1/1024 s)."""
    flags = data[0]
    if flags & 0x01:
        bpm, i = int.from_bytes(data[1:3], "little"), 3
    else:
        bpm, i = data[1], 2
    if flags & 0x08:
        i += 2
    rr = []
    if flags & 0x10:
        while i + 1 < len(data):
            rr.append(round(int.from_bytes(data[i:i + 2], "little") * 1000 / 1024)); i += 2
    return bpm, rr


# ---------------------------------------------------------------- Bluetooth LE (Mac = peripheral, Quest = central)
BLE_SVC = "5A1E0001-6A7E-4B1C-9D2F-2B6F5AD5E001"
BLE_TO_MAC = "5A1E0002-6A7E-4B1C-9D2F-2B6F5AD5E001"      # Quest writes events here
BLE_TO_QUEST = "5A1E0003-6A7E-4B1C-9D2F-2B6F5AD5E001"    # Mac notifies heart rate + acks here
try:
    import objc
    import CoreBluetooth as CB
    from Foundation import NSObject, NSRunLoop, NSDate, NSData
    HAVE_BLE = True
except Exception:       # no pyobjc: Wi-Fi only
    HAVE_BLE = False

if HAVE_BLE:
    class _BleDelegate(NSObject):
        def initWithLink_(self, link):
            self = objc.super(_BleDelegate, self).init()
            self.link = link
            return self

        def peripheralManagerDidUpdateState_(self, mgr):
            self.link.on_state(mgr)

        def peripheralManager_didAddService_error_(self, mgr, svc, err):
            self.link.on_service_added(mgr, err)

        def peripheralManagerDidStartAdvertising_error_(self, mgr, err):
            self.link.on_advertising(err)

        def peripheralManager_central_didSubscribeToCharacteristic_(self, mgr, central, ch):
            self.link.on_subscribe(True, central)

        def peripheralManager_central_didUnsubscribeFromCharacteristic_(self, mgr, central, ch):
            self.link.on_subscribe(False, central)

        def peripheralManager_didReceiveWriteRequests_(self, mgr, reqs):
            self.link.on_writes(mgr, reqs)

        def peripheralManagerIsReadyToUpdateSubscribers_(self, mgr):
            self.link.flush()


class MacBle:
    """The Mac end of the Bluetooth link. Needs the Bluetooth permission Terminal.app already has for the WHOOP."""

    def __init__(self, on_message, on_link):
        self.on_message, self.on_link = on_message, on_link      # on_message(dict); on_link(bool)
        self.subscribed = False
        self.pending = []
        self.notify_char = None
        self.mgr = None
        self.delegate = None

    def on_state(self, mgr):
        if mgr.state() != 5:        # 5 = powered on
            log(f"Bluetooth not ready for the headset link (state {mgr.state()}) - Wi-Fi only until it is on")
            return
        self.notify_char = CB.CBMutableCharacteristic.alloc().initWithType_properties_value_permissions_(
            CB.CBUUID.UUIDWithString_(BLE_TO_QUEST), CB.CBCharacteristicPropertyNotify, None, CB.CBAttributePermissionsReadable)
        write_char = CB.CBMutableCharacteristic.alloc().initWithType_properties_value_permissions_(
            CB.CBUUID.UUIDWithString_(BLE_TO_MAC), CB.CBCharacteristicPropertyWriteWithoutResponse | CB.CBCharacteristicPropertyWrite,
            None, CB.CBAttributePermissionsWriteable)
        svc = CB.CBMutableService.alloc().initWithType_primary_(CB.CBUUID.UUIDWithString_(BLE_SVC), True)
        svc.setCharacteristics_([self.notify_char, write_char])
        mgr.addService_(svc)

    def on_service_added(self, mgr, err):
        if err:
            log(f"Bluetooth service failed: {err}")
            return
        mgr.startAdvertising_({CB.CBAdvertisementDataServiceUUIDsKey: [CB.CBUUID.UUIDWithString_(BLE_SVC)],
                               CB.CBAdvertisementDataLocalNameKey: "StealthRelay"})

    def on_advertising(self, err):
        log("Bluetooth: advertising as StealthRelay - the headset can connect" if not err else f"Bluetooth advertising failed: {err}")

    def on_subscribe(self, up, central):
        self.subscribed = up
        if not up:
            self.pending.clear()
        log("headset connected over Bluetooth" if up else "headset Bluetooth link closed")
        self.on_link(up)

    def on_writes(self, mgr, reqs):
        for r in reqs:
            try:
                msg = json.loads(bytes(r.value() or b"").decode())
            except ValueError:
                continue
            self.on_message(msg)
        mgr.respondToRequest_withResult_(reqs[0], 0)     # only needed for write-with-response

    def start(self):
        self.delegate = _BleDelegate.alloc().initWithLink_(self)
        self.mgr = CB.CBPeripheralManager.alloc().initWithDelegate_queue_(self.delegate, None)

    def notify(self, obj):
        """Send one JSON message to the headset (queued if the radio is busy)."""
        if not self.subscribed:
            return False
        raw = json.dumps(obj).encode()
        self.pending.append(NSData.dataWithBytes_length_(raw, len(raw)))
        self.flush()
        return True

    def flush(self):
        while self.pending and self.mgr.updateValue_forCharacteristic_onSubscribedCentrals_(self.pending[0], self.notify_char, None):
            self.pending.pop(0)
        del self.pending[:-200]         # a long stall must not grow without bound

    async def run(self):
        self.start()
        while True:                      # CoreBluetooth delivers its callbacks through the main run loop: give it slices
            NSRunLoop.currentRunLoop().runUntilDate_(NSDate.dateWithTimeIntervalSinceNow_(0.01))
            await asyncio.sleep(0.01)


class RpcError(Exception):
    def __init__(self, fn, code, body):
        super().__init__(f"{fn}: HTTP {code} {body[:200]}")
        self.code, self.body = code, body


class Debrief:
    """Supabase ingest RPCs (same calls as web-app/scripts/simulate-session.ts)."""

    def __init__(self, name):
        env = {}
        path = os.path.join(HERE, "..", "web-app", ".env.local")
        if os.path.exists(path):
            for line in open(path):
                if "=" in line and not line.lstrip().startswith("#"):
                    k, v = line.strip().split("=", 1)
                    env[k] = v.strip().strip('"')
        self.url, self.anon, self.key = env.get("NEXT_PUBLIC_SUPABASE_URL"), env.get("NEXT_PUBLIC_SUPABASE_ANON_KEY"), env.get("STEALTH_DEVICE_KEY")
        self.ok = bool(self.url and self.anon and self.key)
        self.name = name

    def rpc(self, fn, **args):
        body = json.dumps({"p_key": self.key, **args}).encode()
        req = urllib.request.Request(f"{self.url}/rest/v1/rpc/{fn}", data=body, method="POST", headers={
            "apikey": self.anon, "Authorization": f"Bearer {self.anon}", "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                txt = r.read().decode()
                return json.loads(txt) if txt else None
        except urllib.error.HTTPError as e:      # the server understood and refused: never worth retrying as-is
            raise RpcError(fn, e.code, e.read().decode()) from None


class Mission:
    def __init__(self, mid, t0):
        self.mid, self.t0 = mid, t0
        self.via = "udp"                # the channel the headset started the mission on; heart rate goes back the same way
        self.last_rx = time.time()
        self.seen = set()
        self.hr_out, self.ev_out = [], []
        self.session = None
        self.running, self.ended = True, False
        self.task = None
        self.lock = asyncio.Lock()
        self.window, self.baseline, self.hr = [], None, 0
        self.bad_rr = self.n_hr = self.n_ev = 0
        stamp = time.strftime("%Y%m%d_%H%M%S")
        self.hr_f = open(os.path.join(OUT, f"whoop_{stamp}.csv"), "w", newline="", buffering=1)
        self.ev_f = open(os.path.join(OUT, f"events_{stamp}.csv"), "w", newline="", buffering=1)
        self.hr_csv, self.ev_csv = csv.writer(self.hr_f), csv.writer(self.ev_f)
        self.hr_csv.writerow(["unix_t", "mission_ms", "hr", "level", "baseline"])
        self.ev_csv.writerow(["unix_t", "mission_ms", "event", "actor", "hr_at_event"])


class Relay:
    def __init__(self, a):
        self.a = a
        self.debrief = None if a.dry_run else Debrief(a.name)
        self.tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.tx.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        self.rx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.rx.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.rx.bind(("0.0.0.0", EVENT_PORT))
        self.rx.setblocking(False)
        self.quest = a.quest
        self.mission = None
        self.ble = None
        if HAVE_BLE and not a.no_ble:
            self.ble = MacBle(lambda m: asyncio.ensure_future(self.on_packet(("ble", None), m)), lambda up: None)
        elif not a.no_ble:
            log("pyobjc not installed: Bluetooth link off (pip install pyobjc-framework-CoreBluetooth)")

    # ------------------------------------------------------------ network in
    async def listen(self):
        loop = asyncio.get_running_loop()
        while True:
            data, (ip, _) = await loop.sock_recvfrom(self.rx, 8192)
            try:
                m = json.loads(data.decode())
            except ValueError:
                continue
            try:
                await self.on_packet(("udp", ip), m)
            except Exception as e:      # one bad packet must never take the relay down
                log(f"packet error: {e!r}")

    def reply(self, src, obj):
        """Send one JSON message to the headset over the channel it used."""
        if src[0] == "ble":
            if self.ble:
                self.ble.notify(obj)
            return
        try:
            self.tx.sendto(json.dumps(obj).encode(), (src[1], HR_PORT))
        except OSError:
            pass

    def ack(self, src, n):
        if n is not None:
            self.reply(src, {"type": "ack", "n": n})

    async def on_packet(self, src, m):
        ip = src[1]
        kind, mi = m.get("type"), self.mission
        mine = mi is not None and m.get("mid") == mi.mid
        if mine:
            mi.last_rx = time.time()
            if ip:
                self.quest = ip
        if kind == "hello":
            if ip and ip != self.quest:
                log(f"headset found at {ip} ({m.get('device', '?')})")
            if ip:
                self.quest = ip
            return
        if kind != "event":
            return
        ev, n = m.get("ev"), m.get("n")
        if ev == "mission_start" and not mine:
            if mi:
                await self.end_mission(mi, "failed", "aborted: a new mission started")
            if ip:
                self.quest = ip
            mi = self.start_mission(m, src)
        elif not mine:
            self.ack(src, n)            # late resend from a mission that is already closed: silence it
            return
        self.ack(src, n)
        if n in mi.seen:
            return                      # duplicate (the ack was lost): already stored
        mi.seen.add(n)
        t_ms = int(m.get("t_ms", 0))
        mi.ev_out.append({"t_ms": t_ms, "type": ev, "actor": m.get("actor"), "data": m.get("data", {})})
        mi.ev_csv.writerow([round(time.time(), 3), t_ms, ev, m.get("actor"), mi.hr])
        if ev != "shot":
            log(f"event  {ev:<12} {m.get('actor') or '':<12} t={t_ms / 1000:7.1f}s  HR {mi.hr or '-'}")
        if ev == "mission_end":
            asyncio.create_task(self.finish(mi, m))

    # ------------------------------------------------------------ mission lifecycle
    def start_mission(self, m, src):
        mi = Mission(m.get("mid"), time.time() - int(m.get("t_ms", 0)) / 1000)
        mi.via = src[0]
        self.mission = mi
        log("=" * 60)
        where = "over Bluetooth" if src[0] == "ble" else f"over Wi-Fi ({src[1]})"
        log(f"MISSION START  (trigger pressed)  headset {where}  {'[simulated HR]' if self.a.simulate else 'connecting WHOOP'}")
        mi.task = asyncio.create_task(self.sim_loop(mi) if self.a.simulate else self.ble_loop(mi))
        return mi

    async def finish(self, mi, m):
        last = int(m.get("n", 0))
        for _ in range(40):             # the Quest resends anything unacked every 0.6 s; give it up to 4 s
            if all(i in mi.seen for i in range(1, last)):
                break
            await asyncio.sleep(0.1)
        else:
            log(f"mission_end: events still missing {sorted(set(range(1, last)) - mi.seen)} - closing anyway")
        d = m.get("data", {})
        await self.end_mission(mi, d.get("outcome", "failed"), d.get("reason", ""), int(m.get("t_ms", 0)))

    async def end_mission(self, mi, outcome, reason, t_ms=None):
        if mi.ended:
            return
        mi.ended = mi.running = False
        if self.mission is mi:
            self.mission = None
        if mi.task:
            try:
                await asyncio.wait_for(mi.task, 8)      # leaving the BLE context disconnects the WHOOP
            except Exception:
                mi.task.cancel()
        await self.flush(mi)
        d = self.debrief
        if d and d.ok and mi.session:
            try:
                await asyncio.to_thread(d.rpc, "end_session", p_session=mi.session, p_outcome=outcome, p_reason=reason,
                                        p_duration_ms=t_ms if t_ms and t_ms > 0 else int((time.time() - mi.t0) * 1000))
            except Exception as e:
                log(f"end_session failed: {e}")
        mi.hr_f.close(); mi.ev_f.close()
        log(f"MISSION END  {outcome}: {reason or '-'}  | heart-rate rows {mi.n_hr}, events {mi.n_ev}, "
            f"bad R-R values dropped {mi.bad_rr}  | WHOOP disconnected, relay idle")
        log("=" * 60)

    async def watchdog(self):
        while True:
            await asyncio.sleep(1)
            mi = self.mission
            if mi and time.time() - mi.last_rx > self.a.watchdog:
                log(f"headset silent for {self.a.watchdog:.0f}s - ending the mission")
                await self.end_mission(mi, "failed", "aborted: headset link lost")

    # ------------------------------------------------------------ heart rate
    def level(self, mi, hr):
        return 0 if mi.baseline is None else sum(hr - mi.baseline >= x for x in LEVELS)

    def ingest(self, mi, hr, raw_rr):
        if hr <= 0 or mi.ended:
            return
        rr = [v for v in raw_rr if 250 <= v <= 2500]    # a garbled R-R field must never poison an upload batch
        mi.bad_rr += len(raw_rr) - len(rr)
        now = time.time(); mi.hr = hr
        mi.hr_out.append({"t_ms": round((now - mi.t0) * 1000), "bpm": hr, "rr_ms": rr or None})
        if mi.baseline is None:
            mi.window.append(hr)
            if now - mi.t0 >= BASELINE_S:
                mi.baseline = round(statistics.median(mi.window))
                log(f"resting baseline {mi.baseline} bpm")
        lvl = self.level(mi, hr)
        obj = {"t": round(now, 3), "hr": hr, "level": lvl, "baseline": mi.baseline}
        if mi.via == "ble":
            if self.ble:
                self.ble.notify(obj)
        else:
            msg = json.dumps(obj).encode()
            for addr in ([(self.quest, HR_PORT)] if self.quest else [("255.255.255.255", HR_PORT), ("127.0.0.1", HR_PORT)]):
                try:
                    self.tx.sendto(msg, addr)
                except OSError as e:
                    log(f"send to headset failed {addr}: {e}")
        mi.hr_csv.writerow([round(now, 3), round((now - mi.t0) * 1000), hr, lvl, mi.baseline])
        log(f"HR {hr:3d}  level {lvl}  baseline {mi.baseline or '...'}")

    async def sim_loop(self, mi):
        t = 0
        while mi.running:
            hr = int(72 + 16 * max(0, math.sin(t / 20)) ** 2 + random.gauss(0, 1))
            rr = [round(60000 / hr) + random.randint(-20, 20)] + ([41000] if t % 7 == 3 else [])   # garbage on purpose
            self.ingest(mi, hr, rr)
            t += 1
            await asyncio.sleep(1)

    async def ble_loop(self, mi):
        from bleak import BleakClient, BleakScanner     # imported here: the simulator needs no Bluetooth permission
        cached_failed = False
        while mi.running:
            cached = open(ADDR_CACHE).read().strip() if os.path.exists(ADDR_CACHE) else None
            dev = cached if cached and not cached_failed else None
            if dev is None:
                log("scanning for the WHOOP...")
                found = await BleakScanner.find_device_by_filter(
                    lambda d, ad: HR_SERVICE in [u.lower() for u in ad.service_uuids] or "whoop" in (d.name or "").lower(), timeout=10)
                if found:
                    dev = found.address
                    open(ADDR_CACHE, "w").write(dev)
                elif cached:
                    dev = cached        # a band still held by a dead connection does not advertise: dial it directly
                else:
                    log("WHOOP not found - is Broadcast Heart Rate ON in the WHOOP app?")
                    continue
            try:
                log(f"connecting to the WHOOP ({dev})")
                async with BleakClient(dev, timeout=12) as client:
                    await client.start_notify(HR_MEASUREMENT, lambda _, data: self.ingest(mi, *parse_hr(data)))
                    log("WHOOP connected")
                    cached_failed = False
                    while mi.running and client.is_connected:
                        await asyncio.sleep(0.3)
                log("WHOOP disconnected")
            except Exception as e:
                log(f"WHOOP link: {e}")
                cached_failed = dev == cached
                await asyncio.sleep(1.5)

    # ------------------------------------------------------------ uploads
    async def uploader(self):
        while True:
            await asyncio.sleep(1)
            if self.mission:
                await self.flush(self.mission)

    async def flush(self, mi):
        async with mi.lock:
            ev, hr = mi.ev_out, mi.hr_out
            mi.ev_out, mi.hr_out = [], []
            d = self.debrief
            if d is None or not d.ok:           # dry run / no credentials: count only
                mi.n_ev += len(ev); mi.n_hr += len(hr)
                return
            if mi.session is None:
                try:
                    trainee = await asyncio.to_thread(d.rpc, "upsert_trainee", p_name=d.name)
                    mi.session = await asyncio.to_thread(d.rpc, "start_session", p_trainee=trainee)
                    log(f"debrief session opened: {mi.session}  ({d.name})")
                except Exception as e:
                    log(f"debrief session not opened yet ({e}); will retry")
                    mi.ev_out, mi.hr_out = ev + mi.ev_out, hr + mi.hr_out
                    return
            if ev and not await self.push(mi, "push_events", "p_events", ev):
                mi.ev_out = ev + mi.ev_out
            if hr and not await self.push(mi, "push_hr", "p_samples", hr):
                mi.hr_out = hr + mi.hr_out

    async def push(self, mi, fn, key, rows):
        """True = handled (stored, or refused for good and dropped). False = no network, keep the rows and retry."""
        d = self.debrief
        for attempt in (0, 1):
            try:
                await asyncio.to_thread(d.rpc, fn, p_session=mi.session, **{key: rows})
                if fn == "push_events": mi.n_ev += len(rows)
                else: mi.n_hr += len(rows)
                return True
            except RpcError as e:
                if fn == "push_hr" and attempt == 0 and any(r.get("rr_ms") for r in rows):
                    log(f"{fn} refused ({e}); retrying without R-R intervals")
                    rows = [{**r, "rr_ms": None} for r in rows]
                    continue
                log(f"{fn} refused for good, dropping {len(rows)} rows: {e}")
                return True
            except Exception as e:
                log(f"{fn}: no network ({e}); will retry")
                return False
        return True

    # ------------------------------------------------------------ main
    async def run(self):
        stop = asyncio.Event()
        asyncio.get_running_loop().add_signal_handler(signal.SIGINT, stop.set)
        coros = [self.listen(), self.uploader(), self.watchdog()] + ([self.ble.run()] if self.ble else [])
        tasks = [asyncio.create_task(c) for c in coros]
        log("Stealth Mode relay ready. Idle until the Engineer presses the RIGHT TRIGGER in the headset. (Ctrl-C to quit)")
        await stop.wait()
        if self.mission:
            await self.end_mission(self.mission, "failed", "aborted: relay stopped")
        for t in tasks:
            t.cancel()


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--quest", help="headset IP (otherwise learned from its packets)")
    ap.add_argument("--name", default="Pulin Chaudhari", help="trainee name shown in the debrief app")
    ap.add_argument("--simulate", action="store_true", help="fake heart rate instead of the WHOOP (tests)")
    ap.add_argument("--dry-run", action="store_true", help="no uploads to the debrief app (tests)")
    ap.add_argument("--no-ble", action="store_true", help="Wi-Fi only (no Bluetooth link to the headset)")
    ap.add_argument("--watchdog", type=float, default=30, help="seconds of headset silence that end a mission")
    a = ap.parse_args()
    asyncio.run(Relay(a).run())
