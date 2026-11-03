#!/bin/bash
# Double-click to start the Stealth Mode relay in Terminal.app (it has the Bluetooth permission the WHOOP needs).
# It idles by itself and only works while a mission runs (starts on the right-trigger press, ends with the script).
cd "$(dirname "$0")/.." && exec python3 mac/whoop_relay.py --name "${1:-Pulin Chaudhari}"
