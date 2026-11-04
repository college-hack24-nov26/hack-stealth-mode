"""The Captain, built like the Mac JARVIS: fast reflexes first, a real brain right behind them.

The headset streams the Engineer's voice as he speaks (8 kHz mu-law frames, see VoiceActivity.cs). When he stops:

    Type 1, ~0.7 s after his last word   Whisper small.en (0.25 s, started the moment his voice goes quiet) -> Laya answers typed
                                         questions about it (~60 ms, with a word grammar as a safety net) -> the decision is made
                                         and a short pre-made "Copy" plays on the Quest, nothing to transfer
    Type 2, ~2-3 s after his last word   one long-running Claude Code process writes the Captain's real answer to what was said
                                         -> macOS say through the mission's radio chain -> streamed to the Quest
    Record, in the background            Whisper large-v3-turbo: the accurate transcript for the debrief app

If Claude or the voice fails, the mission's pre-made radio clips take over: the run never hangs on this.

    python3 mac/captain_agent.py report "Comms is down, trip mine"          # try sentences (sitrep for the sitrep)
"""
import concurrent.futures, json, os, queue, re, shutil, subprocess, sys, tempfile, threading, time, wave

import numpy as np

QUICK_MODEL = "mlx-community/whisper-small.en-mlx"
RECORD_MODEL = "mlx-community/whisper-large-v3-turbo"
LAYA_MODEL = "aac6fef/laya-mlx"
BRAIN_MODEL = "claude-haiku-5-5"
BRAIN_TIMEOUT = 3.5
VOICE, RATE, PITCH = "Aman", 168, 0.94      # the Captain in assets_src/tools/gen_audio.py
SR = 8000
GPU = concurrent.futures.ThreadPoolExecutor(1)      # MLX is not thread safe: both Whispers and Laya run on this one thread
# vocabulary only: a sentence here gets echoed back on noise
PROMPT = "Alpha Lead, Engineer, Comms, Doc, trip mine, casualty, container, cover, backup, medic, sitrep, tango."
# what Whisper tends to invent from noise
NOISE = {"", "you", "thank you", "thanks", "thanks for watching", "thank you for watching", "bye", "okay", "ok", "so", "the", "oh", "uh", "um"}

REPORT = r"\b(comm?s?|comm's|communicator|man down|down|hit|injured|injury|wounded|hurt|casualty|mine|blast|explosion|exploded|bomb|ied|trip|tripped|medic|bleeding|help)\b"
COVER = r"\b(cover|covered|behind|container|safe|shipment|steel|hidden|concealed|secure|secured|protected|out of the open)\b"
COMMS = r"\b(comm?s?|comm's|communicator|casualty|wounded|injured|him|he|he's|teammate|buddy|brother)\b"
BACKUP = r"\b(back ?up|support|medic|doc|doctor|help|assist|assistance|reinforce|reinforcements|evac|evacuation|extract|extraction|need|send|requesting|request)\b"

ASK = (("cover", "r31_captain_ask_cover"), ("comms", "r32_captain_ask_comms"), ("backup", "r33_captain_ask_backup"))

PERSONA = ("You are Alpha Lead, Captain of an Indian Army special forces team, on a tactical radio in a firefight. You and Doc are inside "
           "the warehouse at the device. The Engineer and Comms are out front at a shipping container; the last enemies are still firing. "
           "Comms hit a trip mine and is badly wounded. Each message gives what the Engineer just said and what your answer must do. "
           "Speak like a real officer on the radio: calm, direct, plain words, full short sentences. Answer what he actually said. Start with "
           "his news, not with 'Copy': you have already acknowledged him. At most 14 words. Never invent positions, numbers, snipers or orders "
           "beyond what you are told. Output only the words you say on the radio.")
LAYA = {
    "report": {"type": "noul", "instructions": "Is someone reporting that a teammate is hurt or down, or that there was an explosion or a mine?"},
    "cover": {"type": "noul", "instructions": "Is the speaker saying they are in cover, or behind something, or safe?"},
    "comms": {"type": "noul", "instructions": "Is the speaker saying the wounded man is with them or how he is doing?"},
    "backup": {"type": "noul", "instructions": "Is the speaker asking for backup, support, help or a medic?"},
}
LAYA_YES = {"report": 0.5, "cover": 0.6, "comms": 0.5, "backup": 0.7}
GOAL = {"report": "repeat back what happened and order him to drag Comms behind the container now",
        "cover": "ask him to confirm he is in cover", "comms": "ask for Comms' status and whether he is with him",
        "backup": "ask whether he needs support", None: "tell him to hold there, you and Doc are coming to him"}


def decode_ulaw(raw):
    u = ~np.frombuffer(raw, np.uint8)
    mag = (((u & 0x0F).astype(np.int32) << 3) + 0x84) << ((u >> 4) & 7).astype(np.int32)
    x = (mag - 0x84).astype(np.float32) / 32768
    return np.where(u & 0x80, -x, x)


def encode_ulaw(x):
    s = np.clip((x * 32767).astype(np.int32), -32635, 32635)
    sign = np.where(s < 0, 0x80, 0)
    s = np.abs(s) + 0x84
    exp = np.clip(np.floor(np.log2(s)).astype(np.int32) - 7, 0, 7)
    return (~(sign | exp << 4 | (s >> (exp + 3)) & 0x0F) & 0xFF).astype(np.uint8).tobytes()


class Ear:
    """Both models load once, in the background, when the relay starts: the first call must not wait for them."""

    def __init__(self, log):
        self.log, self.run = log, None
        self.ready = threading.Event()
        GPU.submit(self.load)

    def load(self):
        try:
            import mlx_whisper

            def run(model, audio):
                r = mlx_whisper.transcribe(audio, path_or_hf_repo=model, language="en", initial_prompt=PROMPT, temperature=0.0,
                                           condition_on_previous_text=False, verbose=None)
                return " ".join(s["text"].strip() for s in r["segments"] if not (s.get("no_speech_prob", 0) > 0.7 and s.get("avg_logprob", 0) < -0.8)).strip()
            for model in (QUICK_MODEL, RECORD_MODEL):       # the first run of each is slow: get it over with
                run(model, np.zeros(16000, np.float32))
            self.run = run
            self.log("Captain's ears ready (whisper small.en + large-v3-turbo)")
        except Exception as e:
            self.log(f"Captain has no ears: {e!r}. The mission still runs on voice timing alone")
        self.ready.set()

    @staticmethod
    def clean(ulaw):
        x = decode_ulaw(ulaw)
        x = x - float(x.mean()) if len(x) else x
        peak = float(np.abs(x).max()) if len(x) else 0
        return x * min(0.9 / peak, 30) if peak > 0 else x       # the headset mic can be quiet

    def text(self, model, x):
        self.ready.wait(60)
        if self.run is None:
            return None
        audio = np.interp(np.arange(len(x) * 2) / 2, np.arange(len(x)), x).astype(np.float32)      # 8 -> 16 kHz
        return GPU.submit(self.run, model, audio).result()

    def quick(self, ulaw):
        """Type 1: a finished or nearly finished call -> text in about 0.25 s."""
        return self.text(QUICK_MODEL, self.clean(ulaw))

    def hear(self, ulaw, save=None):
        """The record: the accurate words, about 0.9 s."""
        x = self.clean(ulaw)
        if save:
            with wave.open(save, "wb") as w:
                w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
                w.writeframes((x * 32767).astype("<i2").tobytes())
        return self.text(RECORD_MODEL, x)


class Reflex:
    """Type 1: Laya answers typed questions about what he said; the word grammar catches what it misses."""

    def __init__(self, log):
        self.log, self.agent = log, None
        GPU.submit(self.load)

    def load(self):
        try:
            import warnings
            warnings.filterwarnings("ignore", module="laya_mlx")
            import laya_mlx
            agent = laya_mlx.load(LAYA_MODEL)
            agent.predict("radio check", LAYA)
            self.agent = agent
            self.log("Captain's reflexes ready (Laya)")
        except Exception as e:
            self.log(f"Captain has no Laya: {e!r}. Word grammar only")

    def judge(self, text):
        """-> {question: 0/1}"""
        if not self.agent:
            return {}
        try:
            a = GPU.submit(self.agent.predict, text, LAYA).result()["answers"]
            return {k: 1 if a[k]["noul"] >= LAYA_YES[k] else 0 for k in LAYA}
        except Exception:
            return {}


class Brain:
    """Type 2: ONE long-running Claude Code process, so a reply costs the model's time and no start-up."""

    def __init__(self, log):
        self.log, self.proc = log, None
        self.lock = threading.Lock()
        self.words = queue.Queue()
        self.restart()

    def restart(self):
        """A clean head for a new mission, ready long before the first call."""
        threading.Thread(target=self.start, daemon=True).start()

    def start(self):
        with self.lock:
            if self.proc and self.proc.poll() is None:
                self.proc.kill()
            claude = shutil.which("claude") or os.path.expanduser("~/.local/bin/claude")
            try:
                self.proc = subprocess.Popen([claude, "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                                              "--include-partial-messages", "--no-session-persistence", "--setting-sources", "",
                                              "--strict-mcp-config", "--tools", "", "--model", BRAIN_MODEL, "--system-prompt", PERSONA],
                                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, bufsize=1,
                                             cwd=tempfile.gettempdir())
            except OSError as e:
                self.log(f"Captain has no brain: {e!r}. Pre-made lines only")
                self.proc = None
                return
            threading.Thread(target=self.read, args=(self.proc,), daemon=True).start()
            warm = self.turn("Radio check. Answer: Loud and clear.", 20)
        self.log(f"Captain's brain ready ({BRAIN_MODEL})" if warm else "Captain's brain didn't answer: pre-made lines until it does")

    def read(self, proc):
        for line in proc.stdout:
            try:
                e = json.loads(line).get("event", {})
            except ValueError:
                continue
            if e.get("type") == "content_block_delta" and e.get("delta", {}).get("type") == "text_delta":
                self.words.put(e["delta"]["text"])
            elif e.get("type") == "message_stop":
                self.words.put(None)

    def turn(self, said, timeout):
        while not self.words.empty():       # anything left over from a turn that ran late
            self.words.get_nowait()
        try:
            self.proc.stdin.write(json.dumps({"type": "user", "message": {"role": "user", "content": [{"type": "text", "text": said}]}}) + "\n")
            self.proc.stdin.flush()
        except (OSError, ValueError, AttributeError):
            return ""
        out, end = "", time.time() + timeout
        while True:
            try:
                w = self.words.get(timeout=max(end - time.time(), 0.01))
            except queue.Empty:
                return ""
            if w is None:
                return out
            out += w

    def ask(self, said):
        """-> the Captain's words, or "" in time for the pre-made line."""
        if not self.lock.acquire(timeout=0.2):
            return ""
        try:
            return self.turn(said, BRAIN_TIMEOUT)
        finally:
            self.lock.release()


class Mouth:
    @staticmethod
    def band(x, lo, hi, sr=SR):
        X = np.fft.rfft(x); f = np.fft.rfftfreq(len(x), 1 / sr)
        X[(f < lo) | (f > hi)] *= 0.02
        return np.fft.irfft(X, len(x)).astype(np.float32)

    def speak(self, text):
        """The Captain's words through the radio chain of gen_audio.py radio_line -> 8 kHz mu-law, or None."""
        try:
            with tempfile.TemporaryDirectory() as td:
                path = os.path.join(td, "v.wav")
                subprocess.run(["say", "-v", VOICE, "-r", str(RATE), "--data-format=LEI16@16000", "-o", path, text], check=True, capture_output=True)
                with wave.open(path) as w:
                    x = np.frombuffer(w.readframes(w.getnframes()), "<i2").astype(np.float32) / 32768
        except Exception:
            return None
        rng = np.random.default_rng()
        norm = lambda v, p: v * (p / max(float(np.abs(v).max()), 1e-6))
        x = self.band(norm(x, 0.8), 380, 3100, 16000)           # handset band, before leaving 16 kHz
        x = np.interp(np.arange(0, len(x), 16000 * PITCH / SR), np.arange(len(x)), x).astype(np.float32)     # his pitch, 8 kHz
        x = np.tanh(x * 3.2) / np.tanh(3.2)                     # transmitter overdrive
        x = self.band(x, 300, 3400)
        x = norm(x, 0.85) + self.band(rng.normal(0, 1, len(x)).astype(np.float32), 800, 3900) * 0.035
        t = np.arange(int(0.07 * SR)) / SR
        beep = (0.25 * np.sin(2 * np.pi * 1400 * t) * np.minimum(1, t / 0.002) * np.exp(-t / 0.05)).astype(np.float32)
        n = int(0.22 * SR); t = np.arange(n) / SR
        tail = self.band(rng.normal(0, 1, n).astype(np.float32), 500, 3900) * 0.28 * np.minimum(1, t / 0.003) * np.exp(-t / 0.08)
        return encode_ulaw(np.concatenate([beep, x, tail.astype(np.float32)]))


class Captain:
    """One per mission: remembers what he has already been told, and decides (Type 1). Claude only chooses the words."""

    def __init__(self, reflex=None):
        self.reflex = reflex
        self.said_again = 0
        self.rounds = 0
        self.slots = {"cover": 0, "comms": 0, "backup": 0}

    @staticmethod
    def has(pattern, text):
        return 1 if re.search(pattern, text) else 0

    def decide(self, ctx, text):
        """-> ack (short pre-made clip, now), clip (pre-made answer), goal (what Claude's answer must do; None: the clip is the answer)."""
        if text is None:
            return {"ack": "", "clip": "", "goal": None, "intent": "no_ears", "done": 1, "heard": "", **self.slots}
        clean = " ".join(re.sub(r"[^a-z' ]", " ", text.lower()).split())
        usable = clean not in NOISE
        laya = self.reflex.judge(text) if self.reflex and usable else {}
        r = self.report(clean, usable, laya) if ctx == "report" else self.sitrep(clean, usable, laya)
        return {**r, "heard": text, **self.slots}

    def report(self, clean, usable, laya):
        if usable and (laya.get("report") or self.has(REPORT, clean)):
            return {"ack": "r41_captain_copy", "clip": "r23_captain_drag", "goal": GOAL["report"], "intent": "report_ok", "done": 1}
        if self.said_again == 0:
            self.said_again = 1
            return {"ack": "", "clip": "r27_captain_sayagain", "goal": None, "intent": "report_unclear", "done": 0}
        return {"ack": "", "clip": "r23_captain_drag", "goal": None, "intent": "report_unclear", "done": 1}     # he has a man down: don't stall him

    def sitrep(self, clean, usable, laya):
        before = dict(self.slots)
        if usable:
            for slot, pattern in (("cover", COVER), ("comms", COMMS), ("backup", BACKUP)):
                self.slots[slot] |= self.has(pattern, clean) | laya.get(slot, 0)
        got = self.slots != before
        self.rounds += 1
        missing = next((slot for slot, _ in ASK if not self.slots[slot]), None)
        if missing is None:
            return {"ack": "r42_captain_goodcopy", "clip": "r30_captain_copy_all", "goal": GOAL[None], "intent": "sitrep_full", "done": 1}
        if self.rounds >= 3:
            return {"ack": "", "clip": "r34_captain_coming", "goal": None, "intent": "sitrep_partial", "done": 1}
        if not got:
            return {"ack": "", "clip": "r27_captain_sayagain", "goal": None, "intent": "sitrep_unclear", "done": 0}
        return {"ack": "r41_captain_copy", "clip": dict(ASK)[missing], "goal": GOAL[missing], "intent": "sitrep_partial", "done": 0}

    @staticmethod
    def brief(said, goal):
        return f'The Engineer said: "{said}". Your answer must: {goal}.'


if __name__ == "__main__":
    log = lambda m: print(m, flush=True)
    reflex, brain = Reflex(log), Brain(log)
    time.sleep(8)
    c = Captain(reflex)
    for line in sys.argv[2:]:
        t0 = time.time(); r = c.decide(sys.argv[1], line); t1 = time.time()
        reply = brain.ask(c.brief(line, r["goal"])) if r["goal"] else ""
        print(f"type 1 {t1 - t0:.2f} s, type 2 {time.time() - t1:.2f} s | {line} -> {r['intent']} {r['ack']} | {reply or r['clip']}")
    sys.stdout.flush()
    os._exit(0)         # MLX crashes in its exit-time cleanup otherwise
