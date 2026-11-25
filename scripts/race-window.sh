#!/bin/sh
# 测“切换穿透”这个做法的时间窗：光标从轮廓内移到轮廓外之后，多久之内点击会出问题。
#
# 做法：反复执行“停在本体内部 -> 甩到透明处 -> 等 N 毫秒 -> 按下”，同时记录三个时刻：
#   光标到达、鼠标按下、面板开始忽略鼠标事件。然后按“按下相对开始穿透的时间差”给结果分类：
#     穿透成功      背后的窗口收到了点击
#     被面板吃掉    面板还没开始穿透，点击落在了它的透明部分上
#     两边都没收到  点击凭空丢失
#
# 用法：scripts/race-window.sh [到达后最多等几毫秒，默认 16] [每个延迟做几次，默认 6]
# 需要 python3（Windows 上命令是 python）。环境变量 IRREGULAR_HIT_MODE 的含义同 verify.sh。
# macOS 和 Windows（Git Bash）都能跑，先运行一次 scripts/verify.sh 把 probe 和 backdrop 编出来。
set -eu
cd "$(dirname "$0")/.."

OS=$(uname -s)
PROFILE=${PROFILE:-debug} # 换成 release 可以测正式构建
case "$OS" in
  Darwin) APP=src-tauri/target/$PROFILE/tauri-irregular-edge; PY=python3 ;;
  *) APP=src-tauri/target/$PROFILE/tauri-irregular-edge.exe; PY=python ;;
esac
BUILD=src-tauri/target/probe-build
OUT=verification
LOG=$OUT/race-app-$PROFILE.log
BACKDROP_LOG=$OUT/race-backdrop-$PROFILE.log
MAX_DELAY=${1:-16}
REPS=${2:-6}

if [ ! -x "$APP" ]; then
  [ "$PROFILE" = debug ] && how=--debug || how=
  echo "找不到 $APP，先运行：pnpm tauri build $how --no-bundle"; exit 1
fi
[ -x "$BUILD/probe" ] && [ -x "$BUILD/backdrop" ] || { echo "找不到 probe / backdrop，先运行一次 scripts/verify.sh"; exit 1; }
command -v "$PY" > /dev/null || { echo "需要 python3"; exit 1; }
mkdir -p "$OUT"

BACKDROP_PID=
cleanup() {
  if [ "$OS" = Darwin ]; then
    [ -n "$BACKDROP_PID" ] && kill "$BACKDROP_PID" 2>/dev/null || true
    kill "$APP_PID" 2>/dev/null || true
  else
    # Windows 上两个程序都是 probe 启动的，也由它来关（原因见 scripts/verify.sh）
    "$BUILD/probe" stop "$APP_PID" > /dev/null 2>&1 || true
  fi
}

if [ "$OS" = Darwin ]; then
  "$APP" > "$LOG" 2>&1 &
  APP_PID=$!
else
  APP_PID=$("$BUILD/probe" launch "$APP" "$LOG")
fi
trap cleanup EXIT INT TERM
for _ in $(seq 1 60); do grep -q geometry "$LOG" 2>/dev/null && break; sleep 0.25; done

geometry=$(sed -n 's/^.*geometry //p' "$LOG" | tail -1)
X=$(printf '%s' "$geometry" | sed 's/.*"x":\([0-9]*\).*/\1/')
Y=$(printf '%s' "$geometry" | sed 's/.*"y":\([0-9]*\).*/\1/')
SCALE=$(printf '%s' "$geometry" | sed 's/.*"scale":\([0-9.]*\).*/\1/')
# X、Y 是物理像素；棋盘格窗口的尺寸按缩放比例换算成物理像素
set -- $(awk -v x="$X" -v y="$Y" -v s="$SCALE" 'BEGIN { printf "%d %d %d %d", x - 36 * s, y - 36 * s, 624 * s, 480 * s }')
if [ "$OS" = Darwin ]; then
  "$BUILD/backdrop" "$@" > "$BACKDROP_LOG" 2>&1 &
  BACKDROP_PID=$!
else
  "$BUILD/probe" launch "$BUILD/backdrop.exe" "$BACKDROP_LOG" "$@" > /dev/null
fi
for _ in $(seq 1 10); do "$BUILD/probe" list "$APP_PID" > /dev/null 2>&1 && break; sleep 0.5; done

# 每次甩动一行：<等待毫秒> <光标到达时刻> <按下时刻>，单位是 Unix 时间毫秒
FLICKS=$OUT/race-flicks.txt
: > "$FLICKS"
for _ in $(seq 1 "$REPS"); do
  for delay in $(seq 0 "$MAX_DELAY"); do
    out=$("$BUILD/probe" flick "$APP_PID" 44 156 4 4 "$delay" local)
    moved=$(printf '%s' "$out" | sed -n 's/.*移动时刻 unix=\([0-9]*\).*/\1/p')
    down=$(printf '%s' "$out" | sed -n 's/.*按下时刻 unix=\([0-9]*\).*/\1/p')
    echo "$delay $moved $down" >> "$FLICKS"
    sleep 0.25
  done
done

echo "切换“忽略鼠标事件”的位置：${IRREGULAR_HIT_MODE:-native}；共 $(wc -l < "$FLICKS" | tr -d ' ') 次甩动"
# -X utf8：Windows 上 python 默认按系统代码页写标准输出，和脚本自己的 UTF-8 输出混在一起会乱码
"$PY" -I -X utf8 - "$FLICKS" "$LOG" "$BACKDROP_LOG" <<'EOF'
import re, sys
from collections import defaultdict, Counter

flicks, app_log, backdrop_log = sys.argv[1:4]
read = lambda path: open(path, encoding="utf-8").read().splitlines()
ts = lambda line: int(re.search(r"unix=(\d+)", line).group(1))
app, backdrop = read(app_log), read(backdrop_log)
toggles = [ts(l) for l in app if "ignore-cursor-events -> true" in l]
eats = [ts(l) for l in app if "panel pointerdown" in l]
clicks = [ts(l) for l in backdrop if l.startswith("click")]

# (等待毫秒, 切换耗时, 按下相对切换的时间差, 结果)
rows = []
for line in read(flicks):
    delay, moved, down = map(int, line.split())
    toggle = next((t for t in toggles if t >= moved - 2), None)
    eaten = any(moved <= t <= moved + 300 for t in eats)
    passed = any(moved <= t <= moved + 500 for t in clicks)
    outcome = "被面板吃掉" if eaten else "穿透成功" if passed else "两边都没收到"
    rows.append((delay, None if toggle is None else toggle - moved, None if toggle is None else down - toggle, outcome))

order = ["穿透成功", "被面板吃掉", "两边都没收到"]
def table(groups):
    print("  " + " " * 18 + "  ".join("%s" % o for o in order))
    for label, items in groups:
        count = Counter(o for *_, o in items)
        print("  %-18s  " % label + "  ".join("%-10s" % count.get(o, 0) for o in order))

print("\n按“光标到达之后等了多久”统计：")
by_delay = defaultdict(list)
for delay, switch, delta, outcome in rows:
    by_delay[delay].append((switch, outcome))
table(("%d ms" % d, v) for d, v in sorted(by_delay.items()))

print("\n按“鼠标按下相对面板开始穿透的时间差”统计：")
bins = [(-10**6, -1, "切换之前"), (0, 1, "之后 0..1 ms"), (2, 3, "之后 2..3 ms"), (4, 5, "之后 4..5 ms"),
        (6, 7, "之后 6..7 ms"), (8, 9, "之后 8..9 ms"), (10, 11, "之后 10..11 ms"), (12, 10**6, "之后 12 ms 及以上")]
by_bin = defaultdict(list)
for delay, switch, delta, outcome in rows:
    if delta is None:
        continue
    for lo, hi, label in bins:
        if lo <= delta <= hi:
            by_bin[label].append((None, outcome))
table((label, by_bin[label]) for _, _, label in bins)

switches = [s for _, s, _, _ in rows if s is not None]
print("\n光标到达 -> 面板开始穿透：%d..%d ms（面板日志记录的时刻，实际生效还要再晚一点）" % (min(switches), max(switches)))
EOF
