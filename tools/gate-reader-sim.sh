#!/usr/bin/env bash
# A throwaway "gate reader" for trying the RFID + boom-barrier flow (GT-05) without any hardware.
# It only speaks the three HTTP calls a real edge gateway would. See docs/gate-security.md.
#
#   BASE_URL   the API base, including /api        (default: http://localhost:5181)
#   GATE_KEY   the reader key from Gate -> Readers (grk_...)   [required]
#
# Usage:
#   GATE_KEY=grk_xxx ./gate-reader-sim.sh scan <TAG>   # a car with this sticker is at the lane
#   GATE_KEY=grk_xxx ./gate-reader-sim.sh present       # a car is present with no tag
#   GATE_KEY=grk_xxx ./gate-reader-sim.sh poll          # watch for barrier opens (Ctrl-C to stop)
#   GATE_KEY=grk_xxx ./gate-reader-sim.sh demo <TAG>    # scan, then poll until it opens
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:5181}"
: "${GATE_KEY:?set GATE_KEY to a reader key (grk_...) from the portal's Gate -> Readers page}"
H=(-H "X-Gate-Key: ${GATE_KEY}" -H "content-type: application/json")

scan() { # $1 = tag (may be empty for a presence with no tag)
  curl -fsS "${H[@]}" -X POST "${BASE_URL}/gate/scan" -d "{\"tag\":\"${1-}\"}"
}

poll_once() { curl -fsS "${H[@]}" "${BASE_URL}/gate/commands"; }

case "${1:-}" in
  scan)    echo "scan -> $(scan "${2:?give a tag}")" ;;
  present) echo "present -> $(scan "")" ;;
  poll)
    echo "watching for opens at ${BASE_URL} ..."
    while true; do
      out="$(poll_once)"
      [ "$out" != "[]" ] && echo "OPEN BARRIER <- ${out}"
      sleep 1
    done ;;
  demo)
    tag="${2:?give a tag}"
    res="$(scan "$tag")"; echo "scan -> ${res}"
    case "$res" in
      *'"action":"open"'*) echo "registered car: barrier opens immediately." ;;
      *'"action":"hold"'*)
        echo "held for a guard. Approve it in the app; waiting for the open ..."
        for _ in $(seq 1 60); do
          out="$(poll_once)"; [ "$out" != "[]" ] && { echo "OPEN BARRIER <- ${out}"; exit 0; }
          sleep 1
        done
        echo "no open within 60s (denied, or not decided)." ;;
    esac ;;
  *) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
