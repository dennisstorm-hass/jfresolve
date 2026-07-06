#!/usr/bin/env bash
# Simulates TorBox createstream HLS + Jellyfin DynamicHlsController remux locally.
set -euo pipefail

API_KEY="${TORBOX_API_KEY:?Set TORBOX_API_KEY}"
TORRENT_ID="${TORRENT_ID:-50745473}"
FILE_ID="${FILE_ID:-11}"
FFMPEG="${FFMPEG:-ffmpeg}"
FFPROBE="${FFPROBE:-ffprobe}"
WORKDIR="${TMPDIR:-/tmp}/jfresolve-hls-sim-$$"
mkdir -p "$WORKDIR"
trap 'rm -rf "$WORKDIR"' EXIT

log() { echo "[sim] $*"; }
fail() { echo "[sim] FAIL: $*" >&2; exit 1; }

log "== 1. createstream =="
CREATE=$(curl -sS -H "Authorization: Bearer ${API_KEY}" \
  "https://api.torbox.app/v1/api/stream/createstream?id=${TORRENT_ID}&file_id=${FILE_ID}&type=torrent&scrobbling_enabled=false&chosen_audio_index=0")
HLS=$(echo "$CREATE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('data',{}).get('hls_url',''))")
test -n "$HLS" || fail "no hls_url"

log "== 2. codec check (HLS vs /dld/) =="
HLS_CODEC=$("$FFPROBE" -v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 "$HLS" | head -1)
DLD=$(curl -sS "https://api.torbox.app/v1/api/torrents/requestdl?token=${API_KEY}&torrent_id=${TORRENT_ID}&file_id=${FILE_ID}&redirect=false" | python3 -c "import sys,json; print(json.load(sys.stdin).get('data',''))")
DLD_CODEC=$("$FFPROBE" -v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 "$DLD" | head -1)
log "createstream HLS video codec: $HLS_CODEC"
log "/dld/ MP4 video codec:       $DLD_CODEC"
test "$HLS_CODEC" = "h264" || fail "expected h264 from createstream HLS"
test "$DLD_CODEC" = "hevc" || log "WARN: expected hevc from /dld/ (got $DLD_CODEC)"

log "== 3. rewrite playlist (plugin direct passthrough) =="
curl -sS "$HLS" -o "$WORKDIR/upstream.m3u8"
python3 - "$WORKDIR/upstream.m3u8" "$HLS" "$WORKDIR/rewritten.m3u8" <<'PY'
import sys
from urllib.parse import urljoin, urlparse, parse_qs, urlencode, urlunparse
playlist_path, playlist_url, out_path = sys.argv[1:4]
base = urlparse(playlist_url)
base_q = parse_qs(base.query)
def append_query(url: str) -> str:
    if "token=" in url.lower(): return url
    if not base_q: return url
    p = urlparse(url)
    q = parse_qs(p.query)
    for k, v in base_q.items(): q.setdefault(k, v)
    flat = {k: v[0] if len(v) == 1 else v for k, v in q.items()}
    return urlunparse(p._replace(query=urlencode(flat)))
out = []
for line in open(playlist_path):
    t = line.rstrip("\r\n")
    if not t or t.startswith("#"): out.append(t); continue
    abs_url = t if t.startswith("http") else urljoin(playlist_url, t)
    out.append(append_query(abs_url))
open(out_path, "w").write("\n".join(out) + "\n")
PY

log "== 4. WRONG Jellyfin cmd (hevc bsf on h264 HLS) must fail =="
if "$FFMPEG" -hide_banner -v error -i "$HLS" -map 0:v:0 -codec:v copy -bsf:v hevc_mp4toannexb -t 1 -f null - 2>/dev/null; then
  fail "wrong hevc bsf should have failed on h264 input"
else
  log "wrong hevc bsf: failed as expected"
fi

log "== 5. FIXED Jellyfin remux (h264 copy + aac_adtstoasc) =="
cd "$WORKDIR"
"$FFMPEG" -hide_banner -v warning -analyzeduration 200M -probesize 1G -fflags +genpts \
  -i "$HLS" -map 0:v:0 -map 0:a:0 \
  -codec:v copy -bsf:a aac_adtstoasc -codec:a copy -max_muxing_queue_size 2048 \
  -f hls -hls_time 6 -hls_segment_type fmp4 \
  -hls_fmp4_init_filename 'out-init.mp4' -hls_segment_filename 'out-seg%d.mp4' \
  -t 10 -y out.m3u8 2>"$WORKDIR/remux.log" || { tail -20 "$WORKDIR/remux.log"; fail "fixed remux"; }
test -s out-init.mp4 && test -s out-seg0.mp4 || fail "empty remux segments"
log "fixed remux: OK (init=$(stat -f%z out-init.mp4 2>/dev/null || stat -c%s out-init.mp4)B seg0=$(stat -f%z out-seg0.mp4 2>/dev/null || stat -c%s out-seg0.mp4)B)"

log "== 6. FFmpeg bypass: direct TorBox HLS as Jellyfin input (1.0.0.94) =="
"$FFMPEG" -hide_banner -v warning -analyzeduration 200M -probesize 1G -fflags +genpts \
  -i "$HLS" -map 0:v:0 -map 0:a:0 \
  -codec:v copy -bsf:a aac_adtstoasc -codec:a copy -max_muxing_queue_size 2048 \
  -f hls -hls_time 6 -hls_segment_type fmp4 \
  -hls_fmp4_init_filename 'bypass-init.mp4' -hls_segment_filename 'bypass-seg%d.mp4' \
  -t 8 -y "$WORKDIR/bypass-out.m3u8" 2>"$WORKDIR/bypass.log" || { tail -10 "$WORKDIR/bypass.log"; fail "bypass remux"; }
test -s "$WORKDIR/bypass-init.mp4" || fail "empty bypass init"
log "direct TorBox HLS as FFmpeg input: OK"

echo ""
echo "ALL SIMULATION CHECKS PASSED"
echo "Root cause: createstream HLS is h264/aac; Jellyfin must use direct TorBox HLS for FFmpeg -i (not plugin proxy)."
