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
CASES = [   # context, what he says, the intent the Captain must reach, the instant clip if there is one
    ("free", "Copy.", "free_ack", None),
    ("free", "Roger that.", "free_ack", None),
    ("free", "Two men down.", "free_kill", "r47_captain_two_down"),
    ("free", "Tango down.", "free_kill", None),
    ("free", "Where are the enemies firing from?", "free_talk", None),
    ("report", "Alpha Lead, Comms is down. He hit a trip mine. He's wounded.", "report_ok", None),
    ("sitrep", "We are behind the container.", "sitrep_partial", None),
    ("sitrep", "Comms is with me. He's hurt but protected.", "sitrep_partial", None),
    ("sitrep", "Requesting backup and a medic.", "sitrep_full", None),
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
"""], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
time.sleep(30)      # the models load in the background
s.bind(("0.0.0.0", HR_PORT))
s.settimeout(0.05)
mid = random.randint(1, 2**31 - 1)
n = 0


def event(ev, data=None):
    global n
    n += 1
    s.sendto(json.dumps({"type": "event", "mid": mid, "n": n, "ev": ev, "actor": "system", "t_ms": n * 100, "hr": 0, "data": data or {}}).encode(), RELAY)


def wait_reply(timeout):
    """-> said, agent, speak message, seconds to the agent message, seconds to the first spoken frame, bytes of speech"""
    t0 = time.time()
    end = t0 + timeout
    said = agent = speak = None
    t_agent = t_voice = None
    audio = bytearray()
    while time.time() < end:
        try:
            raw = s.recv(4096)
        except socket.timeout:
            continue
        if raw[:1] == b"\xb0":
            t_voice = t_voice or time.time() - t0
            audio += raw[4:]
            if raw[2] & 1:
                break
            continue
        m = json.loads(raw.decode())
        if m.get("type") == "said":
            said = m["t"]
        elif m.get("type") == "agent":
            agent, t_agent = m, time.time() - t0
            if not m["tts"]:
                break
        elif m.get("type") == "speak":
            speak = m
            if not m["frames"]:
                break
    return said, agent, speak, t_agent, t_voice, audio


event("mission_start")
time.sleep(1)
ok = True
acks, fulls = [], []
for u, (ctx, text, want, clip) in enumerate(CASES, 1):
    time.sleep(2)
    a = ulaw(speak(text))
    tail = ulaw(np.random.default_rng(u).normal(0, 0.002, 4800).astype(np.float32))      # 0.6 s of quiet: the headset's end of call
    a += tail
    frames = [a[i:i + 240] for i in range(0, len(a), 240)]
    t_start = time.time()
    for i, f in enumerate(frames):
        last = i == len(frames) - 1
        s.sendto(bytes([0xA0, u, (1 if last else 0) | {"report": 0, "sitrep": 1, "free": 2}[ctx] << 1, i & 255]) + f, RELAY)
        time.sleep(max(0, t_start + (i + 1) * 0.03 - time.time()))      # in real time, like the headset
    said, r, sp, t_agent, t_voice, audio = wait_reply(10)
    good = r is not None and r["intent"] == want and (clip is None or r["ack"] == clip)
    if r and r["tts"]:
        good = good and sp is not None and sp["frames"] > 0 and len(audio) > 8000
    ok &= bool(good)
    # measured from his last word: the 0.6 s of quiet is part of it
    ack, full = 0.6 + (t_agent or 99), 0.6 + (t_voice or 0)
    acks.append(ack)
    if t_voice:
        fulls.append(full)
    print(f"{'PASS' if good else 'FAIL'}  said: {text}\n      heard: {said}\n      "
          f"instant {r and r['ack'] or '-'} after {ack:.2f} s" + (f", spoken answer after {full:.2f} s: {sp and sp['reply']}" if t_voice else "") + f"  ({r and r['intent']})")
print(f"from his last word: Copy {min(acks):.2f}-{max(acks):.2f} s, answer {min(fulls):.2f}-{max(fulls):.2f} s (before the link and the 0.4 s playback buffer)")
event("mission_end", {"outcome": "success", "reason": "agent test"})
time.sleep(3)
relay.terminate()
sys.exit(0 if ok else 1)
