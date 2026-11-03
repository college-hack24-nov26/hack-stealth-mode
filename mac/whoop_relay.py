"""WHOOP -> Quest heart-rate relay (Stealth Mode, Scenario One).

WHOOP with "Broadcast Heart Rate" ON advertises the standard Bluetooth Heart Rate Service (0x180D). This script
subscribes to it, works out a stress level from the rise over the resting baseline, and streams it to the headset:

    Mac -> Quest  UDP 5005  {"t": <unix s>, "hr": 72, "level": 0-3, "baseline": 68}
    Quest -> Mac  UDP 5006  {"type": "hello"} until it hears us, then {"type": "event", "name": ..., "hr": ...}

Until the headset says hello, packets go to the LAN broadcast address (and localhost for the Unity editor);
after that, straight to the headset. Every sample and every mission event is logged to mac/sessions/.

Run from macOS Terminal.app (the Claude shell has no Bluetooth permission):
    python3 mac/whoop_relay.py            # optional: --quest 192.168.x.y
"""
import argparse, asyncio, csv, json, os, socket, statistics, time

from bleak import BleakClient, BleakScanner

HR_SERVICE = "0000180d-0000-1000-8000-00805f9b34fb"
HR_MEASUREMENT = "00002a37-0000-1000-8000-00805f9b34fb"
HR_PORT, EVENT_PORT = 5005, 5006
BASELINE_S = 45          # resting window after connecting
LEVELS = (8, 18, 30)     # bpm above baseline for stress level 1, 2, 3

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "sessions")


def parse_hr(data: bytearray) -> int:
    # Heart Rate Measurement: flags bit 0 -> uint16 value, else uint8
    return int.from_bytes(data[1:3], "little") if data[0] & 0x01 else data[1]


class Relay:
    def __init__(self, quest):
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

    def level(self, hr):
        if self.baseline is None:
            return 0
        rise = hr - self.baseline
        return sum(rise >= x for x in LEVELS)

    def on_hr(self, _, data):
        hr = parse_hr(data)
        if hr <= 0:
            return
        self.hr = hr
        now = time.time()
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
                self.ev_log.writerow([round(time.time(), 3), m.get("t"), m.get("name"), self.hr, m.get("hr")])
                print(f"EVENT {m.get('name')}  (HR {self.hr})")

    async def run(self):
        asyncio.create_task(self.listen_quest())
        while True:
            print("scanning for WHOOP heart-rate broadcast...")
            dev = await BleakScanner.find_device_by_filter(
                lambda d, ad: HR_SERVICE in [u.lower() for u in ad.service_uuids] or "whoop" in (d.name or "").lower(),
                timeout=15)
            if not dev:
                print("not found - is Broadcast Heart Rate ON in the WHOOP app?")
                continue
            print(f"connecting to {dev.name} {dev.address}")
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
    asyncio.run(Relay(ap.parse_args().quest).run())
