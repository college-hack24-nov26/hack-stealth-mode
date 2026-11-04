#!/bin/bash
# One-time setup of a Mac for the relay and the Captain's voice agent (Apple silicon; Intel Macs fall back to faster-whisper).
#   bash mac/setup.sh
# Then copy web-app/.env.local from the team (the debrief app's keys are not in git) and double-click "StealthMode Relay.command".
cd "$(dirname "$0")/.." || exit 1
command -v brew >/dev/null || { echo "Install Homebrew first: https://brew.sh"; exit 1; }
brew list ffmpeg >/dev/null 2>&1 || brew install ffmpeg
command -v ollama >/dev/null || brew install ollama
brew services start ollama
python3 -m pip install --break-system-packages -r mac/requirements.txt
ollama pull qwen2.5:7b-instruct
python3 -c "from huggingface_hub import snapshot_download as d; d('mlx-community/whisper-large-v3-turbo'); d('Systran/faster-whisper-small')"
say -v '?' | grep -q '^Aman' || echo "Optional: System Settings > Accessibility > Spoken Content > add the Aman voice (the Captain). Without it he uses the default voice."
echo "Done. Next: web-app/.env.local, then double-click mac/StealthMode Relay.command"
