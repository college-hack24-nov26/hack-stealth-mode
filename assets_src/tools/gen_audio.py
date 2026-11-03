"""Radio lines (macOS en-IN voices + radio DSP) and combat SFX (numpy synthesis) for the opening.

Output: Stealth Mode/Assets/StealthMode/Resources/Audio/*.wav and Audio/Radio/*.wav
Run: python3 assets_src/tools/gen_audio.py
"""
import os, subprocess, tempfile, wave
import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode", "Resources", "Audio")
SR = 44100
rng = np.random.default_rng(42)

# voice, speaking rate, pitch factor (resample), lines
CAST = {
    "captain": ("Aman", 168, 0.94),
    "sniper1": ("Rishi", 160, 0.97),
    "sniper2": ("Aman", 176, 1.06),
    "comms": ("Rishi", 182, 1.08),
}
LINES = [
    ("r01_captain", "captain", "Alpha team, this is Alpha Lead. Doc and I are in position at the rear. All call signs, hold fire."),
    ("r02_captain", "captain", "Engineer, Comms. Your targets: watchtower two, and the door guard on the right. Wait for Sniper One."),
    ("r03_sniper1", "sniper1", "Sniper One. Tango, rear left, near the back door. I have the shot."),
    ("r04_captain_mark", "captain", "Sniper One, on my mark. Three. Two. One. Mark."),
    ("r05_sniper1_down", "sniper1", "Sniper One. Tango down."),
    ("r06_captain_engage", "captain", "Engineer, engage! Engage!"),
    ("r07_sniper2_runner", "sniper2", "Sniper Two! Runner, front door, left side. He's inside! He's inside!"),
    ("r08_comms_tower", "comms", "Comms. Left tower is down."),
    ("r09_sniper2_down", "sniper2", "Sniper Two. Tango down."),
    ("r10_captain_hold", "captain", "They know we're here. Engineer, Comms, hold that cover. Doc, with me. Moving to the back door."),
    # beat 2
    ("r11_comms_backdoor", "comms", "Alpha Lead, Comms. All six tangos are out front, on us. Back door is clear. Go now. Take the device."),
    ("r12_captain_copy", "captain", "Copy, Comms. Doc, on me. Moving."),
    ("r13_captain_in", "captain", "Alpha Lead. We're in. Moving to the device."),
    # beat 3
    ("r14_sniper2_down2", "sniper2", "Sniper Two. Tango down. Five left out front."),
    ("r15_sniper1_rain", "sniper1", "Sniper One. Heavy rain, coming in fast. I've lost my sight picture. I can't see a thing."),
    ("r16_sniper2_blind", "sniper2", "Sniper Two. Same here. Can't see past the fence. We're blind. Engineer, Comms, you are on your own up front."),
    # beat 4
    ("r17_comms_corner", "comms", "Engineer, Comms. Snipers are out. It's on us now. I'm low, you go high."),
    ("r18_comms_yours_generator", "comms", "Engineer, yours is behind the generator. I'll take the left."),
    ("r18_comms_yours_barrels", "comms", "Engineer, yours is behind the barrels. I'll take the left."),
    ("r18_comms_yours_door", "comms", "Engineer, yours is in the doorway. I'll take the left."),
    ("r18_comms_yours_crates", "comms", "Engineer, yours is behind the crates. I'll take the next one."),
    ("r18_comms_yours_tower", "comms", "Engineer, yours is by the tower. I'll take the left."),
    ("r19_comms_down", "comms", "Comms. Tango down."),
    ("r19_comms_down4", "comms", "Comms. Tango down. Four left."),
    ("r19_comms_down3", "comms", "Comms. Tango down. Three left."),
    ("r20_comms_second", "comms", "Comms. Got him too. Three left. Engineer, stay on your rifle!"),
    # beat 5
    ("r21_comms_moving", "comms", "Engineer, Comms. I'm moving up. Cover me!"),
]

# Comms' own voice in the air only (no radio): short shouts, and his voice after the blast has killed his radio
AIR = [
    ("v_comms_goodhit", "Good hit.", 182, 1.08),
    ("v_comms_moving", "Moving!", 200, 1.10),
    ("v_comms_help_1", "Engineer! Help!", 190, 1.12),
    ("v_comms_help_2", "Engineer, help!", 200, 1.14),
    ("v_comms_help_3", "Engineer! Help me!", 195, 1.12),
]


def write(path, x, sr=SR):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    x = np.clip(x, -1, 1)
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(sr)
        w.writeframes((x * 32767).astype("<i2").tobytes())


def read(path):
    with wave.open(path) as w:
        n = w.getnframes(); sr = w.getframerate(); ch = w.getnchannels()
        x = np.frombuffer(w.readframes(n), "<i2").astype(np.float32) / 32768
    if ch > 1:
        x = x.reshape(-1, ch).mean(1)
    return x, sr


def lp(x, fc):
    a = np.exp(-2 * np.pi * fc / SR); y = np.empty_like(x); s = 0.0
    for i, v in enumerate(x):
        s = (1 - a) * v + a * s; y[i] = s
    return y


def fft_band(x, lo, hi):
    X = np.fft.rfft(x); f = np.fft.rfftfreq(len(x), 1 / SR)
    X[(f < lo) | (f > hi)] *= 0.02
    return np.fft.irfft(X, len(x)).astype(np.float32)


def env(n, attack, decay):
    t = np.arange(n) / SR
    return np.minimum(1, t / max(attack, 1e-4)) * np.exp(-t / decay)


def reverb(x, rt, wet, pre=0.01):
    n = int(SR * rt * 1.5)
    t = np.arange(n) / SR
    ir = rng.normal(0, 1, n) * np.exp(-6.9 * t / rt)
    ir[: int(pre * SR)] = 0
    ir = fft_band(ir.astype(np.float32), 120, 6000)
    ir /= np.abs(ir).sum() ** 0.5 * 6
    y = np.convolve(x, ir)[: len(x) + n]
    out = np.zeros(len(y), np.float32); out[: len(x)] += x
    return out + wet * y / (np.abs(y).max() + 1e-6) * np.abs(x).max()


def norm(x, peak=0.95):
    return x / (np.abs(x).max() + 1e-9) * peak


# ------------------------------------------------------------------ radio
def radio_line(name, who, text):
    voice, rate, pitch = CAST[who]
    with tempfile.TemporaryDirectory() as td:
        aiff = os.path.join(td, "v.aiff"); wav = os.path.join(td, "v.wav")
        subprocess.run(["say", "-v", voice, "-r", str(rate), "-o", aiff, text], check=True)
        # resample trick for a distinct timbre per speaker, then mono 44.1k
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", aiff, "-af",
                        f"asetrate=22050*{pitch},aresample={SR}", "-ac", "1", wav], check=True)
        x, _ = read(wav)
    x = norm(x, 0.8)
    if who == "comms":
        # Comms kneels two metres from the Engineer: his own voice is heard in the air too, unfiltered
        os.makedirs(os.path.join(OUT, "Voice"), exist_ok=True)
        write(os.path.join(OUT, "Voice", name + ".wav"), norm(x, 0.9))
    x = fft_band(x, 380, 3100)                      # handset band
    x = np.tanh(x * 3.2) / np.tanh(3.2)             # transmitter overdrive
    x = fft_band(x, 300, 3400)
    hiss = fft_band(rng.normal(0, 1, len(x)).astype(np.float32), 800, 6000) * 0.035
    x = norm(x, 0.85) + hiss
    pre = np.zeros(int(0.06 * SR), np.float32)
    beep_t = np.arange(int(0.07 * SR)) / SR
    beep = 0.25 * np.sin(2 * np.pi * 1400 * beep_t) * env(len(beep_t), 0.002, 0.05)
    tail_n = int(0.22 * SR)
    tail = fft_band(rng.normal(0, 1, tail_n).astype(np.float32), 500, 7000) * 0.28 * env(tail_n, 0.003, 0.08)
    write(os.path.join(OUT, "Radio", name + ".wav"), np.concatenate([pre, beep.astype(np.float32), x, tail]))


# ------------------------------------------------------------------ SFX
def ak_close():
    n = int(SR * 0.9)
    t = np.arange(n) / SR
    crack = rng.normal(0, 1, n) * env(n, 0.0003, 0.012)
    body = fft_band((rng.normal(0, 1, n) * env(n, 0.001, 0.05)).astype(np.float32), 80, 5000)
    thump = np.sin(2 * np.pi * (70 + 40 * np.exp(-t / 0.02)) * t) * env(n, 0.001, 0.07)
    mech = np.zeros(n); i0 = int(0.035 * SR)
    mech[i0:i0 + 400] = rng.normal(0, 0.3, 400) * np.exp(-np.arange(400) / 60)
    x = 1.0 * crack + 0.8 * body + 1.2 * thump + mech
    x = np.tanh(norm(x) * 2.2)
    return norm(reverb(x.astype(np.float32), 0.7, 0.35))[: int(SR * 1.2)]


def ak_distant(dist_lp=2600, tail=1.4):
    x = ak_close()
    x = fft_band(x, 60, dist_lp)
    pad = np.zeros(int(SR * tail), np.float32)
    x = np.concatenate([x, pad])
    x = reverb(x, tail, 0.6, pre=0.05)
    return norm(x, 0.8)


def sniper_distant():
    n = int(SR * 3.5)
    t = np.arange(n) / SR
    crack = np.zeros(n); crack[:int(0.003 * SR)] = np.linspace(1, -1, int(0.003 * SR))
    boom_at = int(0.22 * SR)
    boom = np.zeros(n)
    m = n - boom_at
    tb = np.arange(m) / SR
    boom[boom_at:] = fft_band((rng.normal(0, 1, m) * env(m, 0.004, 0.18)).astype(np.float32), 40, 1800) + 0.8 * np.sin(2 * np.pi * 55 * tb) * env(m, 0.004, 0.25)
    x = 0.5 * fft_band(crack.astype(np.float32), 900, 9000) + boom
    x = reverb(x.astype(np.float32), 2.4, 0.8, pre=0.1)
    return norm(x, 0.85)


def bullet_crack():
    n = int(SR * 0.35)
    x = np.zeros(n, np.float32)
    k = int(0.0015 * SR)
    x[:k] = np.linspace(1, -1, k)
    x += fft_band((rng.normal(0, 1, n) * env(n, 0.0002, 0.006)).astype(np.float32), 2000, 12000) * 0.6
    x = reverb(x, 0.25, 0.25)
    return norm(x, 0.9)


def impact_metal(seed):
    r = np.random.default_rng(seed)
    n = int(SR * 0.6); t = np.arange(n) / SR
    x = sum(r.uniform(0.3, 1) * np.sin(2 * np.pi * f * t + r.uniform(0, 6)) * np.exp(-t / r.uniform(0.04, 0.18))
            for f in r.uniform(900, 5200, 6))
    x = x + r.normal(0, 1, n) * env(n, 0.0002, 0.006) * 1.5
    return norm(reverb(x.astype(np.float32), 0.3, 0.2), 0.8)


def impact_dirt(seed):
    r = np.random.default_rng(seed)
    n = int(SR * 0.35)
    x = fft_band((r.normal(0, 1, n) * env(n, 0.0005, 0.03)).astype(np.float32), 80, 2400)
    return norm(x, 0.7)


def wind_loop(sec=24):
    n = int(SR * sec)
    w = np.cumsum(rng.normal(0, 1, n)).astype(np.float32); w -= np.convolve(w, np.ones(4000) / 4000, "same")
    w = fft_band(w, 60, 1400)
    gust = 0.55 + 0.45 * np.interp(np.arange(n), np.linspace(0, n, 13), rng.uniform(0.2, 1, 13))
    x = norm(w * gust, 0.6)
    fade = int(SR * 2)
    x[:fade] = x[:fade] * np.linspace(0, 1, fade) + x[-fade:] * np.linspace(1, 0, fade)
    return x[:-fade]


def rain_loop(sec=20):
    """Steady heavy rain on open ground: dense hiss with slow swell, seamless loop."""
    n = int(SR * sec)
    hiss = fft_band(rng.normal(0, 1, n).astype(np.float32), 1200, 11000)
    body = fft_band(rng.normal(0, 1, n).astype(np.float32), 200, 1200) * 0.55
    swell = 0.75 + 0.25 * np.interp(np.arange(n), np.linspace(0, n, 15), rng.uniform(0, 1, 15))
    x = norm((hiss + body) * swell, 0.55)
    fade = int(SR * 1.5)
    x[:fade] = x[:fade] * np.linspace(0, 1, fade) + x[-fade:] * np.linspace(1, 0, fade)
    return x[:-fade]


def rain_metal_loop(sec=16):
    """Rain drumming on a steel container roof overhead: thousands of tiny ringing hits, a deep tin boom underneath."""
    n = int(SR * sec)
    x = np.zeros(n, np.float32)
    for _ in range(int(sec * 260)):
        i = int(rng.uniform(0, n - 4000)); f = rng.uniform(380, 1500); a = rng.uniform(0.15, 1) ** 2
        k = np.arange(2600) / SR
        x[i:i + 2600] += (a * np.sin(2 * np.pi * f * k) * np.exp(-k / rng.uniform(0.004, 0.02))).astype(np.float32)
    x += fft_band(rng.normal(0, 1, n).astype(np.float32), 2500, 9000) * 0.12
    boom = fft_band(rng.normal(0, 1, n).astype(np.float32), 70, 260) * 0.9
    x = norm(x + boom * np.interp(np.arange(n), np.linspace(0, n, 9), rng.uniform(0.3, 1, 9)), 0.6)
    fade = int(SR * 1.2)
    x[:fade] = x[:fade] * np.linspace(0, 1, fade) + x[-fade:] * np.linspace(1, 0, fade)
    return x[:-fade]


def thunder(seed, sec=9):
    """Distant thunder: a crack that is already a rumble, rolling and dying away over several seconds."""
    r = np.random.default_rng(seed)
    n = int(SR * sec)
    t = np.arange(n) / SR
    low = fft_band(r.normal(0, 1, n).astype(np.float32), 25, 140)
    mid = fft_band(r.normal(0, 1, n).astype(np.float32), 120, 700)
    rolls = np.zeros(n, np.float32)
    for k in range(r.integers(5, 9)):
        at = r.uniform(0, sec * 0.7); rolls += (np.exp(-np.abs(t - at) / r.uniform(0.25, 0.9)) * r.uniform(0.3, 1)).astype(np.float32)
    x = (low * 1.0 + mid * 0.45) * rolls * np.exp(-t / (sec * 0.38))
    x *= np.minimum(1, t / 0.25)
    return norm(x.astype(np.float32), 0.85)


def alarm(sec=8):
    n = int(SR * sec); t = np.arange(n) / SR
    f = 650 + 450 * (0.5 - 0.5 * np.cos(2 * np.pi * t / 2.0))
    ph = np.cumsum(2 * np.pi * f / SR)
    x = np.sign(np.sin(ph)) * 0.5 + np.sin(2 * ph) * 0.3
    x = fft_band(x.astype(np.float32), 300, 2500)
    return norm(reverb(x, 1.2, 0.5), 0.7)[:n]


# ------------------------------------------------------------------ beat 4-5: blast, tinnitus, dead radio, Comms' shouts
def air_line(name, text, rate, pitch):
    """Comms' own voice in the open air (no radio). The calls for help are shouted: driven hard, words clear."""
    with tempfile.TemporaryDirectory() as td:
        aiff = os.path.join(td, "v.aiff"); wav = os.path.join(td, "v.wav")
        subprocess.run(["say", "-v", "Rishi", "-r", str(rate), "-o", aiff, text], check=True)
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", aiff, "-af",
                        f"asetrate=22050*{pitch},aresample={SR}", "-ac", "1", wav], check=True)
        x, _ = read(wav)
    x = norm(x, 0.8)
    shout = name.startswith("v_comms_help")
    if shout:
        x = fft_band(x, 180, 7000)
        x = np.tanh(x * 2.2) / np.tanh(2.2)                                   # full voice, pushed
    return norm(reverb(x.astype(np.float32), 0.45, 0.15), 0.95 if shout else 0.8)


def blast_close():
    """Fragmentation mine ~6 m away behind the container corner: the crack, a chest-hitting boom that clips,
    the echo off the warehouse, steel fragments ringing off Shipment-1, then dirt and gravel raining down."""
    n = int(SR * 5); t = np.arange(n) / SR
    x = np.zeros(n, np.float32)
    k = int(0.004 * SR); x[:k] += np.linspace(1, -1, k) * 2
    x += fft_band((rng.normal(0, 1, n) * env(n, 0.0004, 0.06)).astype(np.float32), 200, 6000) * 1.4
    x += fft_band((rng.normal(0, 1, n) * env(n, 0.002, 0.45)).astype(np.float32), 18, 220) * 2.5
    x += (np.sin(2 * np.pi * (30 + 35 * np.exp(-t / 0.08)) * t) * env(n, 0.002, 0.6) * 1.8).astype(np.float32)
    for d, g in ((0.11, 0.35), (0.32, 0.25), (0.7, 0.12)):                   # warehouse + containers slap it back
        i = int(d * SR); x[i:] += fft_band(x[: n - i].copy(), 40, 1800) * g
    for _ in range(4):                                                         # fragments on the steel beside him
        i = int(rng.uniform(0.03, 0.14) * SR); m = impact_metal(int(rng.integers(100, 999)))
        x[i:i + len(m)] += m[: n - i] * rng.uniform(0.4, 0.8)
    for _ in range(90):                                                        # dirt and gravel coming back down
        at = 0.35 + rng.exponential(0.6)
        if at > 4.6: continue
        i = int(at * SR); m = impact_dirt(int(rng.integers(100, 999)))[: int(SR * 0.12)]
        x[i:i + len(m)] += m[: n - i] * rng.uniform(0.08, 0.35) * np.exp(-(at - 0.35) / 1.4)
    x = np.tanh(x * 1.6)                                                       # ears and mic both clip
    return norm(reverb(x.astype(np.float32), 1.6, 0.4, pre=0.03), 0.98)


def tinnitus(sec=7):
    n = int(SR * sec); t = np.arange(n) / SR
    x = 0.6 * np.sin(2 * np.pi * 4100 * t) + 0.18 * np.sin(2 * np.pi * 6230 * t) + 0.08 * np.sin(2 * np.pi * 3930 * t)
    x *= 0.85 + 0.15 * np.sin(2 * np.pi * 0.35 * t)
    x *= np.minimum(1, t / 0.02)
    return norm(x.astype(np.float32), 0.5)


def radio_dead():
    """Comms' set keyed by the blast: a crushed, broken burst of static and then the carrier drops for good."""
    n = int(SR * 1.3); t = np.arange(n) / SR
    s = fft_band(rng.normal(0, 1, n).astype(np.float32), 300, 3400)
    s = np.tanh(s * 4) * 0.8
    gate = np.repeat(rng.uniform(0, 1, n // 1323 + 1) > 0.25, 1323)[:n]      # 30 ms dropouts
    s = s * gate + 0.25 * np.sin(2 * np.pi * (900 + 400 * np.sin(2 * np.pi * 3 * t)) * t) * gate
    s *= np.minimum(1, t / 0.01)
    click = np.zeros(int(SR * 0.05), np.float32); click[:60] = rng.normal(0, 0.8, 60)
    return norm(np.concatenate([np.zeros(int(0.03 * SR), np.float32), click, s.astype(np.float32), click]), 0.8)


def beat45():
    for name, who, text in LINES:
        if name[:3] in ("r17", "r18", "r19", "r20", "r21"):
            radio_line(name, who, text); print("radio", name)
    for name, text, rate, pitch in AIR:
        write(os.path.join(OUT, "Voice", name + ".wav"), air_line(name, text, rate, pitch)); print("air", name)
    write(os.path.join(OUT, "blast_close.wav"), blast_close())
    write(os.path.join(OUT, "tinnitus.wav"), tinnitus())
    write(os.path.join(OUT, "radio_dead.wav"), radio_dead())
    print("BEAT45_DONE")


def main():
    import sys
    if "--beat45" in sys.argv:
        beat45(); return
    if "--rain" in sys.argv:    # only the beat 3 weather sounds
        write(os.path.join(OUT, "rain_loop.wav"), rain_loop())
        write(os.path.join(OUT, "rain_metal_loop.wav"), rain_metal_loop())
        for i in range(1, 4):
            write(os.path.join(OUT, f"thunder_{i}.wav"), thunder(30 + i))
        print("RAIN_DONE"); return
    if "--lines" in sys.argv:   # only (re)make the named radio lines, e.g. --lines r11_comms_backdoor,r12_captain_copy
        want = sys.argv[sys.argv.index("--lines") + 1].split(",")
        for name, who, text in LINES:
            if name in want:
                radio_line(name, who, text); print("radio", name)
        return
    for name, who, text in LINES:
        radio_line(name, who, text)
        print("radio", name)
    write(os.path.join(OUT, "ak_close.wav"), ak_close())
    write(os.path.join(OUT, "ak_distant.wav"), ak_distant())
    write(os.path.join(OUT, "sniper_distant.wav"), sniper_distant())
    write(os.path.join(OUT, "bullet_crack.wav"), bullet_crack())
    for i in range(1, 4):
        write(os.path.join(OUT, f"impact_metal_{i}.wav"), impact_metal(i))
        write(os.path.join(OUT, f"impact_dirt_{i}.wav"), impact_dirt(10 + i))
    write(os.path.join(OUT, "wind_loop.wav"), wind_loop())
    write(os.path.join(OUT, "alarm_siren.wav"), alarm())
    write(os.path.join(OUT, "rain_loop.wav"), rain_loop())
    write(os.path.join(OUT, "rain_metal_loop.wav"), rain_metal_loop())
    for i in range(1, 4):
        write(os.path.join(OUT, f"thunder_{i}.wav"), thunder(30 + i))
    print("AUDIO_DONE")


if __name__ == "__main__":
    main()
