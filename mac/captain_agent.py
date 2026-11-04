"""The Captain, all on this Mac: he hears the Engineer, understands him and answers in his own words. No accounts, no cloud.

The headset streams the Engineer's voice while the Captain is waiting for him (8 kHz mu-law frames, see VoiceActivity.cs).

    Ear     speech to text: Whisper large-v3-turbo on the Mac's GPU (mlx-whisper), faster-whisper if that isn't installed
    Brain   a local LLM (Ollama) reads what he said, works out what he has reported, writes the Captain's answer
    Mouth   text to speech: macOS say through the same radio chain as the mission's lines, 8 kHz mu-law for the Bluetooth link

    report   after the blast: did he say what happened? The Captain answers it and orders the drag; no use -> "say again", once
    sitrep   after the device is down: he must get three things (in cover, Comms with him, needs backup). They add up over the
             exchange; the Captain asks for what is missing, at most twice, then closes it

If the LLM or the voice fails, rules and the mission's pre-made radio clips take over: the run never hangs on this.

    python3 mac/captain_agent.py --wav mac/sessions/voice_x.wav report      # replay a recording through ears and brain
    python3 mac/captain_agent.py report "Comms is down, trip mine"          # try a sentence
"""
import json, os, re, subprocess, sys, tempfile, threading, time, urllib.request, wave

import numpy as np

MLX_MODEL = "mlx-community/whisper-large-v3-turbo"
FALLBACK_MODEL = "small"
LLM_MODEL = "qwen2.5:7b-instruct"
LLM_URL = "http://localhost:11434/api/chat"
LLM_TIMEOUT = 6
VOICE, RATE, PITCH = "Aman", 168, 0.94      # the Captain in assets_src/tools/gen_audio.py
SR = 8000
# vocabulary only: a sentence here gets echoed back on noise
PROMPT = "Alpha Lead, Engineer, Comms, Doc, trip mine, casualty, container, cover, backup, medic, sitrep, tango."
# what Whisper tends to invent from noise
NOISE = {"", "you", "thank you", "thanks", "thanks for watching", "thank you for watching", "bye", "okay", "ok", "so", "the", "oh", "uh", "um"}

REPORT = r"\b(comm?s?|comm's|communicator|man down|down|hit|injured|injury|wounded|hurt|casualty|mine|blast|explosion|exploded|bomb|ied|trip|tripped|medic|bleeding|help)\b"
COVER = r"\b(cover|covered|behind|container|safe|shipment|steel|hidden|concealed|secure|secured|protected|out of the open)\b"
COMMS = r"\b(comm?s?|comm's|communicator|casualty|wounded|injured|him|he|he's|teammate|buddy|brother)\b"
BACKUP = r"\b(back ?up|support|medic|doc|doctor|help|assist|assistance|reinforce|reinforcements|evac|evacuation|extract|extraction|need|send|requesting|request)\b"

ASK = (("cover", "r31_captain_ask_cover"), ("comms", "r32_captain_ask_comms"), ("backup", "r33_captain_ask_backup"))

PERSONA = ("You are Alpha Lead, Captain of an Indian Army special forces team, on a tactical radio in a firefight. You and Doc are "
           "inside the warehouse at the device. The Engineer and Comms are out front at a shipping container, three enemies still "
           "firing. Comms hit a trip mine and is badly wounded. Speak like a real officer on the radio: short, calm, direct, no "
           "pleasantries, plain words. Never say you are an AI. Answer what he actually said. At most 18 words. "
           "Answer only with JSON.")
TASK = {
    "report": 'The Engineer is reporting after a blast. Give {"clear":1,"reply":"..."}. clear is 1 if he said anything about Comms, a man '
              'down, the mine or the blast, else 0. If clear: repeat back what he told you in a few words, then order him to drag Comms '
              'behind the container now. If not clear: ask him to say again. Full sentences, not fragments. Examples:\n'
              '"Comms stepped on a mine, he is down" -> {"clear":1,"reply":"Copy, Comms down on a mine. Drag him behind your container, now."}\n'
              '"big explosion, I cannot see him" -> {"clear":1,"reply":"Copy the blast. Find Comms and drag him behind the container. Move."}',
    "sitrep": 'The device is neutralised and you asked the Engineer for a sitrep. You need three facts: cover (he is in cover or behind the '
              'container), comms (Comms is with him or safe), backup (he asks for backup, support or a medic). Already known: {known}. '
              'Give {{"cover":0,"comms":0,"backup":0,"reply":"..."}} with 1 for each fact known so far. Reply in full sentences: repeat back '
              'what he told you, then {next}. Examples:\n'
              '"we are behind the container" -> {{"cover":1,"comms":0,"backup":0,"reply":"Good, you are in cover. What is Comms\' status, is he with you?"}}\n'
              '"he is with me but bleeding" -> {{"cover":1,"comms":1,"backup":0,"reply":"Copy, Comms is with you and bleeding. Do you need support?"}}\n'
              '"send a medic" -> {{"cover":1,"comms":1,"backup":1,"reply":"Copy all, Engineer. Hold there, Doc and I are coming to you."}}',
}
NEXT = {"cover": "ask him to confirm he is in cover", "comms": "ask for Comms' status and whether he is with him",
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
    """Loads the model once, in the background, when the relay starts: the first call must not wait for it."""

    def __init__(self, log):
        self.log, self.run = log, None
        self.ready = threading.Event()
        self.lock = threading.Lock()
        threading.Thread(target=self.load, daemon=True).start()

    def load(self):
        try:
            try:
                import mlx_whisper

                def run(audio):
                    r = mlx_whisper.transcribe(audio, path_or_hf_repo=MLX_MODEL, language="en", initial_prompt=PROMPT, temperature=0.0,
                                               condition_on_previous_text=False, verbose=None)
                    return [(s["text"], s.get("no_speech_prob", 0), s.get("avg_logprob", 0)) for s in r["segments"]]
                name = MLX_MODEL
            except ImportError:
                from faster_whisper import WhisperModel
                model = WhisperModel(FALLBACK_MODEL, device="cpu", compute_type="int8", local_files_only=True)

                def run(audio):
                    parts, _ = model.transcribe(audio, language="en", beam_size=5, initial_prompt=PROMPT, condition_on_previous_text=False)
                    return [(p.text, p.no_speech_prob, p.avg_logprob) for p in parts]
                name = "faster-whisper " + FALLBACK_MODEL
            run(np.zeros(16000, np.float32))        # the first run is slow: get it over with
            self.run = run
            self.log(f"Captain's ears ready ({name})")
        except Exception as e:
            self.log(f"Captain has no ears: {e!r}. The mission still runs on voice timing alone")
        self.ready.set()

    def hear(self, ulaw, save=None):
        """One finished call -> text, or None when there is no model."""
        x = decode_ulaw(ulaw)
        x = x - float(x.mean()) if len(x) else x
        peak = float(np.abs(x).max()) if len(x) else 0
        if peak > 0:
            x = x * min(0.9 / peak, 30)                 # the headset mic can be quiet
        if save:
            with wave.open(save, "wb") as w:
                w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
                w.writeframes((x * 32767).astype("<i2").tobytes())
        return self.text(x)

    def text(self, x):
        self.ready.wait(60)
        if self.run is None:
            return None
        audio = np.interp(np.arange(len(x) * 2) / 2, np.arange(len(x)), x).astype(np.float32)      # 8 -> 16 kHz
        with self.lock:
            parts = self.run(audio)
        return " ".join(t.strip() for t, nospeech, logp in parts if not (nospeech > 0.7 and logp < -0.8)).strip()


class Brain:
    def __init__(self, log):
        self.log = log
        threading.Thread(target=self.warm, daemon=True).start()

    def warm(self):
        t0 = time.time()
        self.log(f"Captain's brain ready ({LLM_MODEL}, {time.time() - t0:.0f} s)" if self.ask("Reply with {}", "radio check", 90) is not None
                 else f"Captain has no LLM ({LLM_MODEL} at {LLM_URL}): rules and pre-made lines only")

    @staticmethod
    def ask(task, said, timeout=LLM_TIMEOUT):
        """-> the parsed JSON answer, or None."""
        body = json.dumps({"model": LLM_MODEL, "stream": False, "format": "json", "keep_alive": "3h",
                           "options": {"temperature": 0.3, "num_predict": 80},
                           "messages": [{"role": "system", "content": PERSONA + " " + task},
                                        {"role": "user", "content": f'The Engineer on the radio: "{said}"'}]}).encode()
        try:
            req = urllib.request.Request(LLM_URL, body, {"Content-Type": "application/json"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return json.loads(json.load(r)["message"]["content"])
        except Exception:
            return None


class Mouth:
    @staticmethod
    def band(x, lo, hi):
        X = np.fft.rfft(x); f = np.fft.rfftfreq(len(x), 1 / SR)
        X[(f < lo) | (f > hi)] *= 0.02
        return np.fft.irfft(X, len(x)).astype(np.float32)

    def speak(self, text):
        """The Captain's words through the radio chain of gen_audio.py radio_line -> 8 kHz mu-law, or None."""
        try:
            with tempfile.TemporaryDirectory() as td:
                aiff, wav_path = os.path.join(td, "v.aiff"), os.path.join(td, "v.wav")
                if subprocess.run(["say", "-v", VOICE, "-r", str(RATE), "-o", aiff, text], capture_output=True).returncode:
                    subprocess.run(["say", "-r", str(RATE), "-o", aiff, text], check=True)     # that voice isn't on this Mac
                subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", aiff, "-af", f"asetrate=22050*{PITCH},aresample={SR}",
                                "-ac", "1", wav_path], check=True)
                with wave.open(wav_path) as w:
                    x = np.frombuffer(w.readframes(w.getnframes()), "<i2").astype(np.float32) / 32768
        except Exception:
            return None
        rng = np.random.default_rng()
        norm = lambda v, p: v * (p / max(float(np.abs(v).max()), 1e-6))
        x = self.band(norm(x, 0.8), 380, 3100)                  # handset band
        x = np.tanh(x * 3.2) / np.tanh(3.2)                     # transmitter overdrive
        x = self.band(x, 300, 3400)
        x = norm(x, 0.85) + self.band(rng.normal(0, 1, len(x)).astype(np.float32), 800, 3900) * 0.035
        t = np.arange(int(0.07 * SR)) / SR
        beep = (0.25 * np.sin(2 * np.pi * 1400 * t) * np.minimum(1, t / 0.002) * np.exp(-t / 0.05)).astype(np.float32)
        n = int(0.22 * SR); t = np.arange(n) / SR
        tail = self.band(rng.normal(0, 1, n).astype(np.float32), 500, 3900) * 0.28 * np.minimum(1, t / 0.003) * np.exp(-t / 0.08)
        return encode_ulaw(np.concatenate([np.zeros(int(0.06 * SR), np.float32), beep, x, tail.astype(np.float32)]))


class Captain:
    """One per mission: remembers what he has already been told."""

    def __init__(self, brain=None):
        self.brain = brain
        self.said_again = 0
        self.rounds = 0
        self.slots = {"cover": 0, "comms": 0, "backup": 0}

    @staticmethod
    def has(pattern, text):
        return 1 if re.search(pattern, text) else 0

    def think(self, task, text):
        if not self.brain:
            return {}
        return self.brain.ask(task, text) or {}

    @staticmethod
    def words(a):
        r = a.get("reply")
        if not isinstance(r, str):
            return ""
        return " ".join(r.replace('"', "").split())[:150]

    def answer(self, ctx, text):
        """-> clip (pre-made fallback), reply (his own words, spoken if there are any), intent, done, slots."""
        if text is None:
            return {"clip": "", "reply": "", "intent": "no_ears", "heard": "", "done": 1, **self.slots}
        clean = " ".join(re.sub(r"[^a-z' ]", " ", text.lower()).split())
        usable = clean not in NOISE
        r = self.report(clean, text, usable) if ctx == "report" else self.sitrep(clean, text, usable)
        return {**r, "heard": text, **self.slots}

    def report(self, clean, text, usable):
        a = self.think(TASK["report"], text) if usable else {}
        if usable and (a.get("clear") == 1 or self.has(REPORT, clean)):
            reply = self.words(a)
            if reply and not re.search(r"drag|pull|cover|behind|container", reply.lower()):
                reply += " Drag him behind your container, now!"
            return {"clip": "r23_captain_drag", "reply": reply, "intent": "report_ok", "done": 1}
        if self.said_again == 0:
            self.said_again = 1
            return {"clip": "r27_captain_sayagain", "reply": "", "intent": "report_unclear", "done": 0}
        return {"clip": "r23_captain_drag", "reply": "", "intent": "report_unclear", "done": 1}     # he has a man down: don't stall him

    def sitrep(self, clean, text, usable):
        before = dict(self.slots)
        known = ", ".join(k for k, v in before.items() if v) or "nothing yet"
        if usable:
            for slot, pattern in (("cover", COVER), ("comms", COMMS), ("backup", BACKUP)):
                self.slots[slot] |= self.has(pattern, clean)
        ask = next((slot for slot, _ in ASK if not self.slots[slot]), None)
        a = self.think(TASK["sitrep"].format(known=known, next=NEXT[ask]), text) if usable else {}
        for slot in self.slots:
            self.slots[slot] |= 1 if a.get(slot) == 1 else 0
        got = self.slots != before
        self.rounds += 1
        reply = self.words(a)
        if all(self.slots.values()):
            return {"clip": "r30_captain_copy_all", "reply": reply, "intent": "sitrep_full", "done": 1}
        if self.rounds >= 3:
            return {"clip": "r34_captain_coming", "reply": "", "intent": "sitrep_partial", "done": 1}
        if not got:
            return {"clip": "r27_captain_sayagain", "reply": reply if usable else "", "intent": "sitrep_unclear", "done": 0}
        clip = next(c for slot, c in ASK if not self.slots[slot])
        return {"clip": clip, "reply": reply, "intent": "sitrep_partial", "done": 0}


if __name__ == "__main__":
    log = lambda m: print(m, flush=True)
    args = sys.argv[1:]
    brain = Brain(log)
    c = Captain(brain)
    if args[0] == "--wav":
        ear = Ear(log)
        with wave.open(args[1]) as w:
            x = np.frombuffer(w.readframes(w.getnframes()), "<i2").astype(np.float32) / 32768
        t0 = time.time(); said = ear.text(x)
        print(f"heard in {time.time() - t0:.1f} s: {said}")
        print(c.answer(args[2] if len(args) > 2 else "report", said))
    else:
        for line in args[1:]:
            t0 = time.time(); r = c.answer(args[0], line); print(f"{time.time() - t0:.1f} s", line, "->", r)
