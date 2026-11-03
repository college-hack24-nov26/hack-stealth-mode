"""Fake headset for the Captain agent: speaks real sentences (macOS say, over gunfire-level noise) through the real voice
protocol to its own private relay on spare ports (a relay already running for real is never touched: nothing reaches the debrief
app or the WHOOP), and checks his answers.

    python3 mac/test_agent.py

Exit code 0 = every answer was right.
"""
import json, os, random, socket, subprocess, sys, tempfile, time

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
HR_PORT, EVENT_PORT = 5015, 5016
RELAY = ("127.0.0.1", EVENT_PORT)
CASES = [   # context, what he says, the clip the Captain must answer with
    ("report", "Alpha Lead, Comms is down. He hit a trip mine. He's wounded.", "r23_captain_drag"),
    ("sitrep", "We are behind the container.", "r32_captain_ask_comms"),
    ("sitrep", "Comms is with me. He's hurt but protected.", "r33_captain_ask_backup"),
    ("sitrep", "Requesting backup and a medic.", "r30_captain_copy_all"),
]


def ulaw(x):
    s = np.clip((x * 32767).astype(np.int32), -32635, 32635)
    sign = np.where(s < 0, 0x80, 0)
    s = np.abs(s) + 0x84
    exp = np.clip(np.floor(np.log2(s)).astype(np.int32) - 7, 0, 7)
    return (~(sign | exp << 4 | (s >> (exp + 3)) & 0x0F) & 0xFF).astype(np.uint8).tobytes()


def speak(text):
    with tempfile.TemporaryDirectory() as td:
        wav = os.path.join(td, "v.wav")
        subprocess.run(["say", "-v", "Rishi", "-o", wav, "--data-format=LEI16@8000", text], check=True)
        raw = open(wav, "rb").read()
    x = np.frombuffer(raw[raw.index(b"data") + 8:], "<i2").astype(np.float32) / 32768
    return np.clip(x * 0.7 + np.random.default_rng(1).normal(0, 0.01, len(x)), -1, 1)


s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
relay = subprocess.Popen([sys.executable, "-c", f"""
import sys, asyncio, argparse
sys.path.insert(0, {HERE!r})
import whoop_relay as w
w.HR_PORT, w.EVENT_PORT = {HR_PORT}, {EVENT_PORT}
a = argparse.Namespace(quest=None, name="agent test", simulate=True, dry_run=True, no_ble=True, watchdog=30)
asyncio.run(w.Relay(a).run())
"""], stdout=subprocess.DEVNULL)
time.sleep(10)      # the model loads in the background
s.bind(("0.0.0.0", HR_PORT))
s.settimeout(0.05)
mid = random.randint(1, 2**31 - 1)
n = 0


def event(ev, data=None):
    global n
    n += 1
    s.sendto(json.dumps({"type": "event", "mid": mid, "n": n, "ev": ev, "actor": "system", "t_ms": n * 100, "hr": 0, "data": data or {}}).encode(), RELAY)


def wait_reply(timeout):
    end = time.time() + timeout
    while time.time() < end:
        try:
            m = json.loads(s.recv(4096).decode())
        except socket.timeout:
            continue
        if m.get("type") == "agent":
            return m
    return None


event("mission_start")
time.sleep(1)
ok = True
for u, (ctx, text, want) in enumerate(CASES, 1):
    a = ulaw(speak(text))
    frames = [a[i:i + 240] for i in range(0, len(a), 240)]
    for i, f in enumerate(frames):
        last = i == len(frames) - 1
        s.sendto(bytes([0xA0, u, (1 if last else 0) | (1 if ctx == "sitrep" else 0) << 1, i & 255]) + f, RELAY)
        time.sleep(0.03)        # in real time, like the headset
    t0 = time.time()
    r = wait_reply(10)
    good = r is not None and r["clip"] == want
    ok &= good
    print(f"{'PASS' if good else 'FAIL'}  {time.time() - t0:.1f} s  said: {text}\n      heard: {r and r['heard']}\n      clip: {r and r['clip']} (want {want})")
event("mission_end", {"outcome": "success", "reason": "agent test"})
time.sleep(1)
relay.terminate()
sys.exit(0 if ok else 1)
