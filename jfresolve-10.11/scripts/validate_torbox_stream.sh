#!/usr/bin/env bash
# Validates TorBox stream-first flow: mylist → filename match → createstream → ffprobe/ffmpeg
set -euo pipefail

API_KEY="${TORBOX_API_KEY:?Set TORBOX_API_KEY}"
TORRENTIO_URL='https://torrentio.strem.fun/resolve/torbox/'"${API_KEY}"'/5b905043c5486b1fa8c6ed1ac3e938429f68f525/X.Men.97.S01E01.2160p.WEB-DL.DV.HDR.DDP5.1.Atmos.H265.MP4-BEN.THE.MEN.mp4/3/X.Men.97.S01E01.2160p.WEB-DL.DV.HDR.DDP5.1.Atmos.H265.MP4-BEN.THE.MEN.mp4'
HASH='5b905043c5486b1fa8c6ed1ac3e938429f68f525'
FILENAME='X.Men.97.S01E01.2160p.WEB-DL.DV.HDR.DDP5.1.Atmos.H265.MP4-BEN.THE.MEN.mp4'
FFPROBE="${FFPROBE:-/opt/homebrew/bin/ffprobe}"
FFMPEG="${FFMPEG:-/opt/homebrew/bin/ffmpeg}"

echo "== 1. mylist + filename match =="
MYLIST=$(curl -sS -H "Authorization: Bearer ${API_KEY}" \
  "https://api.torbox.app/v1/api/torrents/mylist?bypass_cache=true")

FILE_ID=$(echo "$MYLIST" | python3 -c "
import sys,json
hash='${HASH}'.lower()
target='${FILENAME}'
for t in json.load(sys.stdin).get('data',[]):
    if t.get('hash','').lower()!=hash: continue
    for f in t.get('files',[]):
        sn=f.get('short_name') or f.get('name','')
        if sn==target or sn.endswith('/'+target):
            print(f.get('id'))
            break
    break
")
TORRENT_ID=$(echo "$MYLIST" | python3 -c "
import sys,json
for t in json.load(sys.stdin).get('data',[]):
    if t.get('hash','').lower()=='${HASH}'.lower():
        print(t.get('id')); break
")

echo "torrent_id=${TORRENT_ID} file_id=${FILE_ID} (expected file_id=11 for S01E01)"
test "$FILE_ID" = "11"

echo ""
echo "== 2. createstream (stream-first) =="
CREATE=$(curl -sS -H "Authorization: Bearer ${API_KEY}" \
  "https://api.torbox.app/v1/api/stream/createstream?id=${TORRENT_ID}&file_id=${FILE_ID}&type=torrent&scrobbling_enabled=false&chosen_audio_index=0")
HLS=$(echo "$CREATE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('data',{}).get('hls_url',''))")
test -n "$HLS"
echo "hls_url=${HLS:0:100}..."

echo ""
echo "== 3. ffprobe HLS (chunked, no full file) =="
"$FFPROBE" -v error -show_entries stream=codec_type,codec_name,width,height -of csv=p=0 "$HLS" | head -4

echo ""
echo "== 4. ffmpeg read 3 seconds only =="
"$FFMPEG" -v error -i "$HLS" -t 3 -f null - && echo "ffmpeg 3s read: OK"

echo ""
echo "== 5. requestdl /dld/ is fallback only (compare) =="
DLD=$(curl -sS "https://api.torbox.app/v1/api/torrents/requestdl?token=${API_KEY}&torrent_id=${TORRENT_ID}&file_id=${FILE_ID}&redirect=false" | python3 -c "import sys,json; print(json.load(sys.stdin).get('data',''))")
CT=$(curl -sSI "$DLD" | grep -i '^content-type:' | head -1)
echo "/dld/ content-type: $CT"

echo ""
echo "ALL CHECKS PASSED"
