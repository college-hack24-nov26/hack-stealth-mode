#!/bin/bash
# Double-click to start the Stealth Mode relay in Terminal.app (it has the Bluetooth permission the WHOOP needs).
# It idles by itself and only works while a mission runs (starts on the right-trigger press, ends with the script).
# An older relay left running would swallow the headset's voice: this one replaces it.
pkill -f "mac/whoop_relay.py" 2>/dev/null; sleep 1
cd "$(dirname "$0")/.." && exec python3 mac/whoop_relay.py --name "${1:-Pulin Chaudhari}"
