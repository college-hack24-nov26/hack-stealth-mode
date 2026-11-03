"""Fake headset for testing mac/whoop_relay.py without a Quest. Speaks the real protocol (see the relay's docstring).

    python3 mac/test_relay.py mission --seconds 40   # start, events (1 duplicate, 1 lost then late), end
    python3 mac/test_relay.py silent                 # start, then go quiet: the relay's watchdog must end the mission

Checks that every event is acked, heart-rate packets arrive, and the mission end is acked. Exit code 0 = pass.
"""
import argparse, json, random, socket, sys, time

RELAY = ("127.0.0.1", 5006)

ap = argparse.ArgumentParser()
ap.add_argument("scenario", choices=["mission", "silent"])
ap.add_argument("--seconds", type=float, default=40)
a = ap.parse_args()

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
sock.bind(("0.0.0.0", 5005))
sock.setblocking(False)
mid, t0 = random.randint(1, 2**31 - 1), time.time()
acked, hr_packets, hr_levels = set(), [], []


def send(obj):
    sock.sendto(json.dumps(obj).encode(), RELAY)


def pump():
    while True:
        try:
            data, _ = sock.recvfrom(4096)
        except BlockingIOError:
            return
        m = json.loads(data.decode())
        if m.get("type") == "ack":
            acked.add(m["n"])
        elif "hr" in m:
            hr_packets.append(m["hr"]); hr_levels.append(m.get("level"))


def ev(n, name, actor="system", data=None):
    send({"type": "event", "mid": mid, "n": n, "ev": name, "actor": actor,
          "t_ms": int((time.time() - t0) * 1000), "hr": 0, "data": data or {}})


def wait(seconds):
    end = time.time() + seconds
    nb = 0
    while time.time() < end:
        pump()
        if time.time() >= nb and a.scenario == "mission":
            nb = time.time() + 2
            send({"type": "beat", "mid": mid})
        time.sleep(0.05)


ev(1, "mission_start"); wait(1)
if a.scenario == "silent":
    print("headset goes silent now; relay should end the mission by itself")
    sys.exit(0)

ev(2, "phase", data={"id": "briefing"}); wait(0.5)
ev(3, "radio", "captain", {"transcript": "test line", "end_ms": 3000}); wait(0.3)
ev(3, "radio", "captain", {"transcript": "test line", "end_ms": 3000})           # duplicate: the ack was "lost"
# n=4 is withheld (packet lost); 5 arrives first
ev(5, "shot", "engineer", {"hit": True}); wait(a.seconds)
ev(6, "mission_end", "system", {"outcome": "failed", "reason": "relay test (fake headset)"}); wait(1.5)
ev(4, "enemy_down", "engineer", {"enemy": "Enemy-1"})                           # the lost packet shows up late
wait(5)

problems = []
for n in range(1, 7):
    if n not in acked:
        problems.append(f"event {n} was never acked")
if len(hr_packets) < min(10, a.seconds * 0.5):
    problems.append(f"only {len(hr_packets)} heart-rate packets arrived")
print(f"acked {sorted(acked)}; heart-rate packets received {len(hr_packets)} (levels {sorted(set(hr_levels))}); "
      f"HR range {min(hr_packets or [0])}-{max(hr_packets or [0])}")
print("FAIL: " + "; ".join(problems) if problems else "PASS: every event acked, heart rate streamed")
sys.exit(1 if problems else 0)
