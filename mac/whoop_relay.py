"""WHOOP -> Quest heart-rate relay (Stealth Mode, Scenario One).

WHOOP with "Broadcast Heart Rate" ON advertises the standard Bluetooth Heart Rate Service (0x180D). This script
subscribes to it, works out a stress level from the rise over the resting baseline, and streams it to the headset:

    Mac -> Quest  UDP 5005  {"t": <unix s>, "hr": 72, "level": 0-3, "baseline": 68}
    Quest -> Mac  UDP 5006  {"type": "hello"} until it hears us, then {"type": "event", "name": ..., "hr": ...}

Until the headset says hello, packets go to the LAN broadcast address (and localhost for the Unity editor);
after that, straight to the headset. Every sample and every mission event is logged to mac/sessions/.

Debrief app: when the headset sends `mission_start`, a session is opened in Supabase (web-app/.env.local:
NEXT_PUBLIC_SUPABASE_URL, NEXT_PUBLIC_SUPABASE_ANON_KEY, STEALTH_DEVICE_KEY) and from then on the heart rate
(with R-R intervals, for HRV) and the event timeline are pushed once a second through the device-keyed RPCs.
The last 60 s before the start go up as the resting baseline (negative t_ms). The app does all the analysis.

Run from macOS Terminal.app (the Claude shell has no Bluetooth permission):
    python3 mac/whoop_relay.py            # optional: --quest 192.168.x.y
"""
import argparse, asyncio, collections, csv, json, os, socket, statistics, time, urllib.request

from bleak import BleakClient, BleakScanner

HR_SERVICE = "0000180d-0000-1000-8000-00805f9b34fb"
HR_MEASUREMENT = "00002a37-0000-1000-8000-00805f9b34fb"
HR_PORT, EVENT_PORT = 5005, 5006
BASELINE_S = 45          # resting window after connecting
LEVELS = (8, 18, 30)     # bpm above baseline for stress level 1, 2, 3

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "sessions")


def parse_hr(data: bytearray):
    """Heart Rate Measurement (0x2A37): flags bit0 uint16 bpm, bit3 energy field, bit4 R-R intervals (1/1024 s)."""
    flags, i = data[0], 1
    if flags & 0x01:
        bpm = int.from_bytes(data[1:3], "little"); i = 3
    else:
        bpm = data[1]; i = 2
    if flags & 0x08:
        i += 2
    rr = []
    if flags & 0x10:
        while i + 1 < len(data):
            rr.append(round(int.from_bytes(data[i:i + 2], "little") * 1000 / 1024)); i += 2
    return bpm, rr


def load_env():
    env = {}
    path = os.path.join(HERE, "..", "web-app", ".env.local")
    if os.path.exists(path):
        for line in open(path):
            if "=" in line and not line.lstrip().startswith("#"):
                k, v = line.strip().split("=", 1)
                env[k] = v.strip().strip('"')
    return env


class Debrief:
    """Supabase ingest RPCs (same calls as web-app/scripts/simulate-session.ts)."""

    def __init__(self, name):
        env = load_env()
        self.url, self.anon, self.key = env.get("NEXT_PUBLIC_SUPABASE_URL"), env.get("NEXT_PUBLIC_SUPABASE_ANON_KEY"), env.get("STEALTH_DEVICE_KEY")
        self.ok = bool(self.url and self.anon and self.key)
        self.name = name
        self.session = None
        if not self.ok:
            print("debrief upload off: web-app/.env.local missing Supabase settings")

    def rpc(self, fn, **args):
        body = json.dumps({"p_key": self.key, **args}).encode()
        req = urllib.request.Request(f"{self.url}/rest/v1/rpc/{fn}", data=body, method="POST", headers={
            "apikey": self.anon, "Authorization": f"Bearer {self.anon}", "Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=10) as r:
            txt = r.read().decode()
            return json.loads(txt) if txt else None


class Relay:
    def __init__(self, quest, name):
        os.makedirs(OUT, exist_ok=True)
        stamp = time.strftime("%Y%m%d_%H%M%S")
        self.hr_log = csv.writer(open(os.path.join(OUT, f"whoop_{stamp}.csv"), "w", newline="", buffering=1))
        self.hr_log.writerow(["unix_t", "hr", "level", "baseline"])
        self.ev_log = csv.writer(open(os.path.join(OUT, f"events_{stamp}.csv"), "w", newline="", buffering=1))
        self.ev_log.writerow(["unix_t", "quest_t", "event", "hr_at_event", "quest_hr"])
        self.tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.tx.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        self.rx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.rx.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.rx.bind(("0.0.0.0", EVENT_PORT))
        self.rx.setblocking(False)
        self.quest = quest
        self.connected_at = None
        self.window = []
        self.baseline = None
        self.hr = 0
        self.debrief = Debrief(name)
        self.t0 = None                  # wall clock of mission start (t_ms = 0)
        self.recent = collections.deque(maxlen=60)   # (unix_t, bpm, rr) before the start: resting baseline
        self.hr_out, self.ev_out = [], []

    def level(self, hr):
        if self.baseline is None:
            return 0
        rise = hr - self.baseline
        return sum(rise >= x for x in LEVELS)

    def on_hr(self, _, data):
        hr, rr = parse_hr(data)
        if hr <= 0:
            return
        self.hr = hr
        now = time.time()
        if self.t0 is None:
            self.recent.append((now, hr, rr))
        else:
            self.hr_out.append({"t_ms": round((now - self.t0) * 1000), "bpm": hr, "rr_ms": rr or None})
        if self.baseline is None:
            self.window.append(hr)
            if now - self.connected_at >= BASELINE_S:
                self.baseline = round(statistics.median(self.window))
                print(f"baseline set: {self.baseline} bpm")
        lvl = self.level(hr)
        msg = json.dumps({"t": round(now, 3), "hr": hr, "level": lvl, "baseline": self.baseline}).encode()
        targets = [(self.quest, HR_PORT)] if self.quest else [("255.255.255.255", HR_PORT), ("127.0.0.1", HR_PORT)]
        for addr in targets:
            try:
                self.tx.sendto(msg, addr)
            except OSError as e:
                print("send failed", addr, e)
        self.hr_log.writerow([round(now, 3), hr, lvl, self.baseline])
        print(f"HR {hr:3d}  level {lvl}  baseline {self.baseline or '...'}  -> {self.quest or 'broadcast'}")

    async def listen_quest(self):
        while True:
            try:
                data, (ip, _) = self.rx.recvfrom(2048)
            except BlockingIOError:
                await asyncio.sleep(0.05)
                continue
            try:
                m = json.loads(data.decode())
            except ValueError:
                continue
            if m.get("type") == "hello":
                if self.quest != ip:
                    print(f"headset found at {ip} ({m.get('device', '?')})")
                self.quest = ip
            elif m.get("type") == "event":
                self.quest = self.quest or ip
                ev = m.get("ev") or m.get("name")
                self.ev_log.writerow([round(time.time(), 3), m.get("t_ms"), ev, self.hr, m.get("hr")])
                print(f"EVENT {ev} {json.dumps(m.get('data', {}))}  (HR {self.hr})")
                if ev == "mission_start":
                    await self.open_session(m.get("t_ms", 0))
                if self.t0 is not None:
                    self.ev_out.append({"t_ms": int(m.get("t_ms", 0)), "type": ev, "actor": m.get("actor"), "data": m.get("data", {})})
                if ev == "mission_end":
                    await self.flush()
                    await self.close_session(m)

    async def open_session(self, quest_t_ms):
        self.t0 = time.time() - quest_t_ms / 1000
        self.hr_out = [{"t_ms": round((t - self.t0) * 1000), "bpm": b, "rr_ms": rr or None} for t, b, rr in self.recent]
        self.ev_out = []
        if not self.debrief.ok:
            return
        try:
            d = self.debrief
            trainee = await asyncio.to_thread(d.rpc, "upsert_trainee", p_name=d.name, p_resting_hr=self.baseline)
            d.session = await asyncio.to_thread(d.rpc, "start_session", p_trainee=trainee)
            print(f"DEBRIEF session {d.session} for {d.name}")
        except Exception as e:
            print("debrief start failed:", e)

    async def close_session(self, m):
        d = self.debrief
        if d.ok and d.session:
            data = m.get("data", {})
            try:
                await asyncio.to_thread(d.rpc, "end_session", p_session=d.session, p_outcome=data.get("outcome", "failed"),
                                        p_reason=data.get("reason", ""), p_duration_ms=int(m.get("t_ms", 0)))
                print("DEBRIEF session closed")
            except Exception as e:
                print("debrief end failed:", e)
        self.t0, d.session = None, None

    async def flush(self):
        d = self.debrief
        if not (d.ok and d.session):
            return
        ev, hr = self.ev_out, self.hr_out
        self.ev_out, self.hr_out = [], []
        try:
            if ev:
                await asyncio.to_thread(d.rpc, "push_events", p_session=d.session, p_events=ev)
            if hr:
                await asyncio.to_thread(d.rpc, "push_hr", p_session=d.session, p_samples=hr)
        except Exception as e:
            print("debrief push failed (will retry):", e)
            self.ev_out, self.hr_out = ev + self.ev_out, hr + self.hr_out

    async def uploader(self):
        while True:
            await asyncio.sleep(1)
            await self.flush()

    async def run(self):
        asyncio.create_task(self.listen_quest())
        asyncio.create_task(self.uploader())
        while True:
            print("scanning for WHOOP heart-rate broadcast...")
            dev = await BleakScanner.find_device_by_filter(
                lambda d, ad: HR_SERVICE in [u.lower() for u in ad.service_uuids] or "whoop" in (d.name or "").lower(),
                timeout=15)
            last = os.path.join(OUT, ".whoop_address")
            if not dev and os.path.exists(last):
                # a band that is still connected (e.g. to a relay that just quit) stops advertising: dial it directly
                dev = open(last).read().strip()
                print(f"no broadcast seen - trying the last WHOOP directly ({dev})")
            elif not dev:
                print("not found - is Broadcast Heart Rate ON in the WHOOP app?")
                continue
            else:
                open(last, "w").write(dev.address)
            print(f"connecting to {getattr(dev, 'name', 'WHOOP')} {getattr(dev, 'address', dev)}")
            try:
                async with BleakClient(dev) as client:
                    self.connected_at = self.connected_at or time.time()
                    await client.start_notify(HR_MEASUREMENT, self.on_hr)
                    while client.is_connected:
                        await asyncio.sleep(1)
            except Exception as e:
                print("WHOOP link dropped:", e)
            await asyncio.sleep(2)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--quest", help="headset IP (otherwise learned from its hello)")
    ap.add_argument("--name", default="Pulin Chaudhari", help="trainee name shown in the debrief app")
    a = ap.parse_args()
    asyncio.run(Relay(a.quest, a.name).run())
