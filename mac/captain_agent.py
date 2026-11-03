"""The Captain's ears and judgement, all on this Mac: no internet, no accounts, nothing else to keep running.

The headset streams the Engineer's voice while the Captain is waiting for him (8 kHz mu-law frames, see VoiceActivity.cs).
Ear turns one finished call into text (faster-whisper, model already cached). Captain reads it and picks the radio clip he
answers with; the clips are the same pre-made radio lines as the rest of the mission, so his voice never changes.

    report   what happened after the blast: anything about Comms / a casualty / the mine -> "get him out of the open";
             no use -> "say again", once
    sitrep   after the device is down: he must get three things, in cover, Comms with him, needs backup. They add up over
             the exchange; the Captain asks for what is missing, at most twice, then closes it

    python3 mac/captain_agent.py "Comms is down, trip mine"            # try the rules on a sentence
"""
import os, re, sys, threading, wave

import numpy as np

MODEL = "small"
PROMPT = ("Alpha Lead, this is Engineer. Comms is down, trip mine, he's wounded. "
          "We're behind the container, in cover. Comms is with me. Requesting backup and a medic.")
# what Whisper tends to invent from noise
NOISE = {"", "you", "thank you", "thanks", "thanks for watching", "thank you for watching", "bye", "okay", "ok", "so", "the", "oh", "uh", "um"}

REPORT = r"\b(comm?s?|comm's|communicator|man down|down|hit|injured|injury|wounded|hurt|casualty|mine|blast|explosion|exploded|bomb|ied|trip|tripped|medic|bleeding|help)\b"
COVER = r"\b(cover|covered|behind|container|safe|shipment|steel|hidden|concealed|secure|secured|protected|out of the open)\b"
COMMS = r"\b(comm?s?|comm's|communicator|casualty|wounded|injured|him|he|he's|teammate|buddy|brother)\b"
BACKUP = r"\b(back ?up|support|medic|doc|doctor|help|assist|assistance|reinforce|reinforcements|evac|evacuation|extract|extraction|need|send|requesting|request)\b"

ASK = (("cover", "r31_captain_ask_cover"), ("comms", "r32_captain_ask_comms"), ("backup", "r33_captain_ask_backup"))


def decode_ulaw(raw):
    u = ~np.frombuffer(raw, np.uint8)
    mag = (((u & 0x0F).astype(np.int32) << 3) + 0x84) << ((u >> 4) & 7).astype(np.int32)
    x = (mag - 0x84).astype(np.float32) / 32768
    return np.where(u & 0x80, -x, x)


class Ear:
    """Loads the model once, in the background, when the relay starts: the first call must not wait for it."""

    def __init__(self, log):
        self.log, self.model = log, None
        self.ready = threading.Event()
        self.lock = threading.Lock()
        threading.Thread(target=self.load, daemon=True).start()

    def load(self):
        try:
            from faster_whisper import WhisperModel
            self.model = WhisperModel(MODEL, device="cpu", compute_type="int8", local_files_only=True)
            self.model.transcribe(np.zeros(16000, np.float32), language="en")      # first run is slow: get it over with
            self.log(f"Captain agent ready (whisper {MODEL})")
        except Exception as e:
            self.log(f"Captain agent has no ears: {e!r}. The mission still runs on voice timing alone")
        self.ready.set()

    def hear(self, ulaw, save=None):
        """One finished call -> text, or None when there is no model."""
        x = decode_ulaw(ulaw)
        peak = float(np.abs(x).max()) if len(x) else 0
        if peak > 0:
            x = x * min(0.9 / peak, 30)                 # the headset mic can be quiet
        if save:
            with wave.open(save, "wb") as w:
                w.setnchannels(1); w.setsampwidth(2); w.setframerate(8000)
                w.writeframes((x * 32767).astype("<i2").tobytes())
        self.ready.wait(20)
        if self.model is None:
            return None
        audio = np.interp(np.arange(len(x) * 2) / 2, np.arange(len(x)), x).astype(np.float32)      # 8 -> 16 kHz
        with self.lock:
            parts, _ = self.model.transcribe(audio, language="en", beam_size=1, initial_prompt=PROMPT,
                                             condition_on_previous_text=False, vad_filter=False)
            return " ".join(p.text.strip() for p in parts if p.no_speech_prob < 0.8).strip()


class Captain:
    """One per mission: remembers what he has already been told."""

    def __init__(self):
        self.said_again = 0
        self.rounds = 0
        self.slots = {"cover": 0, "comms": 0, "backup": 0}

    @staticmethod
    def has(pattern, text):
        return 1 if re.search(pattern, text) else 0

    def answer(self, ctx, text):
        """-> what the headset needs: the clip to play, what was understood, and whether the exchange is over."""
        if text is None:
            return {"clip": "", "intent": "no_ears", "heard": "", "done": 1, **self.slots}
        clean = re.sub(r"[^a-z' ]", " ", text.lower())
        clean = " ".join(clean.split())
        usable = clean not in NOISE
        r = self.report(clean, usable) if ctx == "report" else self.sitrep(clean, usable)
        return {**r, "heard": text, **self.slots}

    def report(self, text, usable):
        if usable and self.has(REPORT, text):
            return {"clip": "r23_captain_drag", "intent": "report_ok", "done": 1}
        if self.said_again == 0:
            self.said_again = 1
            return {"clip": "r27_captain_sayagain", "intent": "report_unclear", "done": 0}
        return {"clip": "r23_captain_drag", "intent": "report_unclear", "done": 1}      # he has a man down: don't stall him

    def sitrep(self, text, usable):
        got = usable and (self.has(COVER, text) or self.has(COMMS, text) or self.has(BACKUP, text))
        if got:
            for slot, pattern in (("cover", COVER), ("comms", COMMS), ("backup", BACKUP)):
                self.slots[slot] |= self.has(pattern, text)
        self.rounds += 1
        if all(self.slots.values()):
            return {"clip": "r30_captain_copy_all", "intent": "sitrep_full", "done": 1}
        if self.rounds >= 3:
            return {"clip": "r34_captain_coming", "intent": "sitrep_partial", "done": 1}
        if not got:
            return {"clip": "r27_captain_sayagain", "intent": "sitrep_unclear", "done": 0}
        clip = next(c for slot, c in ASK if not self.slots[slot])
        return {"clip": clip, "intent": "sitrep_partial", "done": 0}


if __name__ == "__main__":
    c = Captain()
    for line in sys.argv[1:]:
        print(line, "->", c.answer("sitrep" if line.startswith("s:") else "report", line.removeprefix("s:")))
