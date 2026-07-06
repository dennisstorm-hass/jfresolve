#!/usr/bin/env bash
# Verifies Jellyfin's -re -async 1 (ReadAtNativeFramerate) stalls remote HLS probe/remux.
set -euo pipefail

FFMPEG="${FFMPEG:-ffmpeg}"
# Apple public HLS test stream (no API key required)
HLS="${HLS_URL:-https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_ts/master.m3u8}"
PROBE_SEC="${PROBE_SEC:-2}"

log() { echo "[validate-re] $*"; }
fail() { echo "[validate-re] FAIL: $*" >&2; exit 1; }

command -v "$FFMPEG" >/dev/null || fail "ffmpeg not found"

run_ffmpeg() {
  local label="$1"; shift
  log "== $label =="
  local start end elapsed
  start=$(date +%s)
  if "$FFMPEG" -hide_banner -loglevel error -analyzeduration 5000000 -probesize 1G "$@" -t "$PROBE_SEC" -f null -; then
    end=$(date +%s)
    elapsed=$((end - start))
    log "$label completed in ${elapsed}s"
    printf '%s' "$elapsed"
  else
    fail "$label ffmpeg exited non-zero"
  fi
}

# Jellyfin-like probe/read WITHOUT -re
FAST=$(run_ffmpeg "without -re" \
  -fflags +igndts+genpts+discardcorrupt \
  -i "$HLS")
log "without -re wall time: ${FAST}s"

# Jellyfin ReadAtNativeFramerate adds -re -async 1 (hangs on remote HLS)
log "== with -re -async 1 (ReadAtNativeFramerate) — 25s wall-clock limit =="
start=$(date +%s)
set +e
"$FFMPEG" -hide_banner -loglevel error -analyzeduration 5000000 -probesize 1G -async 1 -re \
  -fflags +igndts+genpts+discardcorrupt -i "$HLS" -t "$PROBE_SEC" -f null -
code=$?
set -e
elapsed=$(( $(date +%s) - start ))
log "with -re exited code=$code elapsed=${elapsed}s"

if [ "$elapsed" -gt 20 ]; then
  log "CONFIRMED: -re stalls remote HLS (>${elapsed}s for ${PROBE_SEC}s output)"
else
  log "WARN: -re completed faster than expected (${elapsed}s) on this stream"
fi

if [ "$FAST" -gt 120 ]; then
  log "WARN: without -re also slow (${FAST}s) — network or stream dependent"
fi

log "ALL CHECKS DONE — strip -re/-async for TorBox HLS FFmpeg jobs"
