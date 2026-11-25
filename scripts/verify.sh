#!/bin/sh
# 一键验证 Tauri 透明窗口的异形轮廓：
#   1. 像素检查：面板窗口自身的 alpha，以及屏幕上的实际合成结果（初始高度、加行拉高之后各做一次）
#   2. 停稳后点击：轮廓内由面板接收，轮廓外落到背后的窗口
#   3. 甩动测试：光标从轮廓内快速移到轮廓外后立刻点击，统计被面板透明部分吃掉的次数，
#      以及“光标越界后多久之内点击会丢”这个时间窗有多长
#   4. （只在 Windows 上）光标快速进出轮廓时，切换穿透会不会让画面闪
#
# 用法：pnpm tauri build --debug --no-bundle && scripts/verify.sh
# 环境变量 PROFILE=release 可以换成正式构建（先用 pnpm tauri build --no-bundle 编译）。
#
# macOS 和 Windows（Git Bash）都能跑。验证工具用的是系统自带的编译器，不用另装 SDK：
#   macOS    scripts/probe.swift、scripts/backdrop.swift，用 swiftc 编译
#   Windows  scripts/probe.cs、scripts/backdrop.cs，用 .NET Framework 自带的 csc 编译
# Windows 上的截图和 geometry 用 win- 前缀（正式构建是 win-release-），不覆盖 macOS 的。
#
# 环境变量 IRREGULAR_HIT_MODE 选择在哪里切换“忽略鼠标事件”，两种做法可以对比：
#   native（默认）  在 Rust 的轮询线程里直接做
#   js              Rust 只报光标位置，由前端判断后再调回去，多一次前后端往返
#
# 运行时会启动两个进程：面板应用，以及一个独立进程里的原生棋盘格窗口（scripts/backdrop.swift
# 或 scripts/backdrop.cs），后者会被摆在面板正后方。运行期间会移动鼠标、模拟点击，结束时两个进程都会关闭。
set -eu
cd "$(dirname "$0")/.."

OS=$(uname -s)
PROFILE=${PROFILE:-debug}
BUILD=src-tauri/target/probe-build
OUT=verification
LOG=$OUT/app-$PROFILE.log  # 面板应用的输出
BACKDROP_LOG=$OUT/backdrop-$PROFILE.log  # 棋盘格窗口的输出

case "$OS" in
  Darwin) APP=src-tauri/target/$PROFILE/tauri-irregular-edge ;;
  *) APP=src-tauri/target/$PROFILE/tauri-irregular-edge.exe ;;
esac
if [ ! -x "$APP" ]; then
  [ "$PROFILE" = debug ] && how=--debug || how=
  echo "找不到 $APP，先运行：pnpm tauri build $how --no-bundle"; exit 1
fi
mkdir -p "$BUILD" "$OUT"

case "$OS" in
  Darwin)
    PREFIX=
    # 按 macOS 13 作为目标编译：这两个脚本用的激活接口在 macOS 14 被标为弃用，但对命令行工具仍然可用
    TARGET="$(uname -m)-apple-macos13.0"
    swiftc -O -target "$TARGET" -module-cache-path "$BUILD/module-cache" scripts/probe.swift -o "$BUILD/probe"
    swiftc -O -target "$TARGET" -module-cache-path "$BUILD/module-cache" scripts/backdrop.swift -o "$BUILD/backdrop"
    ;;
  *)
    if [ "$PROFILE" = debug ]; then PREFIX=win-; else PREFIX=win-release-; fi
    # 老版 csc 不认路径里的正斜杠（会把目录部分丢掉），所以给它的路径都用 Windows 的写法
    CSC="$(cygpath -u "$WINDIR")/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    "$CSC" -nologo -codepage:65001 -r:System.Drawing.dll "-out:$(cygpath -w "$PWD/$BUILD")\\probe.exe" "$(cygpath -w "$PWD/scripts/probe.cs")"
    "$CSC" -nologo -codepage:65001 -target:winexe -r:System.Drawing.dll -r:System.Windows.Forms.dll \
      "-out:$(cygpath -w "$PWD/$BUILD")\\backdrop.exe" "$(cygpath -w "$PWD/scripts/backdrop.cs")"
    ;;
esac

BACKDROP_PID=
cleanup() {
  if [ "$OS" = Darwin ]; then
    [ -n "$BACKDROP_PID" ] && kill "$BACKDROP_PID" 2>/dev/null || true
    kill "$APP_PID" 2>/dev/null || true
  else
    # Windows 上两个程序都是 probe 启动的，也由它来关
    "$BUILD/probe" stop "$APP_PID" > /dev/null 2>&1 || true
  fi
}

wait_for() {  # wait_for <文件> <内容>
  for _ in $(seq 1 60); do grep -q "$2" "$1" 2>/dev/null && return 0; sleep 0.25; done
  return 1
}

if [ "$OS" = Darwin ]; then
  "$APP" > "$LOG" 2>&1 &
  APP_PID=$!
else
  # 让 probe 来启动，由它报出进程号。Git Bash 的 $! 可能只是一个中间等待进程的号，
  # 和真正的应用对不上，kill 也结束不了它
  APP_PID=$("$BUILD/probe" launch "$APP" "$LOG")
fi
trap cleanup EXIT INT TERM
wait_for "$LOG" geometry || { echo "面板应用没有启动："; cat "$LOG"; exit 1; }

read_geometry() {
  geometry=$(sed -n 's/^.*geometry //p' "$LOG" | tail -1)
  X=$(printf '%s' "$geometry" | sed 's/.*"x":\([0-9]*\).*/\1/')
  Y=$(printf '%s' "$geometry" | sed 's/.*"y":\([0-9]*\).*/\1/')
  W=$(printf '%s' "$geometry" | sed 's/.*"w":\([0-9]*\).*/\1/')
  H=$(printf '%s' "$geometry" | sed 's/.*"h":\([0-9]*\).*/\1/')
  SCALE=$(printf '%s' "$geometry" | sed 's/.*"scale":\([0-9.]*\).*/\1/')
  ADD_Y=$(printf '%s' "$geometry" | sed 's/.*"add":\[[0-9]*,\([0-9]*\),.*/\1/')
}

# 棋盘格窗口比面板大一圈：上方和左右各多 36，下方多留一些放计数文字，也给面板继续长高留余量。
# X、Y 是物理像素；W、H、SCALE 是逻辑像素和缩放比例，1x 屏上两者一样
read_geometry
set -- $(awk -v x="$X" -v y="$Y" -v w="$W" -v h="$H" -v s="$SCALE" \
  'BEGIN { m = 36 * s; printf "%d %d %d %d", x - m, y - m, w * s + 2 * m, h * s + (3 * 40 + 36 + 130) * s }')
if [ "$OS" = Darwin ]; then
  "$BUILD/backdrop" "$@" > "$BACKDROP_LOG" 2>&1 &
  BACKDROP_PID=$!
else
  "$BUILD/probe" launch "$BUILD/backdrop.exe" "$BACKDROP_LOG" "$@" > /dev/null
fi

probe() { "$BUILD/probe" "$@" > /dev/null; }
lines() { wc -l < "$1" | tr -d ' '; }
since() { tail -n +$(($2 + 1)) "$1"; }
# 甩动之后过了多少毫秒，面板才切换到“忽略鼠标事件”
switch_delay() {
  awk '/unix=[0-9]+ ignore-cursor-events -> true/ {
        t = substr($0, index($0, "unix=") + 5); t = t + 0
        if (t >= moved) { print t - moved; exit }
      }' moved="$1" "$LOG"
}
# 运行 probe click，并打印两个进程在这期间收到的点击
click_and_report() {
  from=$(lines "$LOG")
  from_backdrop=$(lines "$BACKDROP_LOG")
  probe click "$APP_PID" "$@" local
  sleep 0.3
  since "$BACKDROP_LOG" "$from_backdrop" | sed 's/^/    背后的窗口收到：/'
  since "$LOG" "$from" | grep "pointerdown" | sed 's/^.*pointerdown/    面板收到：pointerdown/' || true
}

# 等 probe 能同时找到两个窗口（它会先把两个应用切到前台）
for _ in $(seq 1 10); do "$BUILD/probe" list "$APP_PID" > /dev/null 2>&1 && break; sleep 0.5; done
echo "切换“忽略鼠标事件”的位置：${IRREGULAR_HIT_MODE:-native}"
"$BUILD/probe" list "$APP_PID"

echo
echo "== 1. 像素检查，初始高度 ${W}x${H} =="
probe capture "$APP_PID" "$OUT" "${PREFIX}initial"
"$BUILD/probe" analyze "$OUT" "${PREFIX}initial"

echo
echo "== 2. 点三次“+ 一行”，窗口拉高 =="
echo "（面板不是焦点窗口时，落在它上面的第一次点击只会让它拿回焦点，所以没加行就再点一次）"
for _ in 1 2 3; do
  read_geometry
  before=$H
  for attempt in 1 2; do
    click_and_report 93 $((ADD_Y + 13))
    # 等窗口真的长高（最多 3 秒）：从点下去到窗口报告新的尺寸不是瞬时的
    for _ in $(seq 1 30); do read_geometry; [ "$H" -gt "$before" ] && break; sleep 0.1; done
    [ "$H" -gt "$before" ] && break
    echo "    -> 没有加行，再点一次"
  done
  sleep 0.3
done
read_geometry
echo "窗口现在是 ${W}x${H}"
probe capture "$APP_PID" "$OUT" "${PREFIX}resized"
"$BUILD/probe" analyze "$OUT" "${PREFIX}resized"

echo
echo "== 3. 光标停稳 300ms 后点击（坐标是面板窗口内的坐标）=="
BOTTOM=$((H - 66))  # 本体下边缘
click_test() {
  echo "-- $1"
  shift
  click_and_report "$@"
}
click_test "左上角，完全透明" 4 4
click_test "本体正下方的阴影" 336 $((BOTTOM + 12))
click_test "右侧缺口里" $((W - 46)) 70
click_test "右上圆角之外" $((W - 3)) 3
echo "-- 本体内部，没有元素（第一次点击只拿回焦点的话，就再点一次）"
for attempt in 1 2; do
  from=$(lines "$LOG")
  click_and_report 44 156
  since "$LOG" "$from" | grep -q "panel pointerdown" && break
  echo "    -> 面板没收到，再点一次"
done

echo
echo "== 4. 从本体内部 (44, 156) 甩到目标点，到达后不到 1 毫秒就按下，各 10 次（最坏情况）=="
flick_test() {  # flick_test <说明> <终点x> <终点y> <到达后等几毫秒再按下>
  label=$1
  x=$2
  y=$3
  delay=$4
  leaks=0
  passes=0
  delays=""
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    from=$(lines "$LOG")
    from_backdrop=$(lines "$BACKDROP_LOG")
    moved=$("$BUILD/probe" flick "$APP_PID" 44 156 "$x" "$y" "$delay" local | sed -n 's/.*移动时刻 unix=\([0-9]*\).*/\1/p')
    sleep 0.3
    since "$LOG" "$from" | grep -q "panel pointerdown" && leaks=$((leaks + 1))
    since "$BACKDROP_LOG" "$from_backdrop" | grep -q "^click" && passes=$((passes + 1))
    delays="$delays $(switch_delay "$moved")"
    sleep 0.15
  done
  echo "${label}：穿透成功 ${passes} 次，被面板吃掉 ${leaks} 次，两边都没收到 $((10 - passes - leaks)) 次"
  echo "   每次从光标到达到开始穿透的耗时（毫秒）：${delays}"
}
flick_test "甩到左上角（完全透明）" 4 4 0
flick_test "甩到右侧缺口里" $((W - 46)) 70 0
flick_test "甩到本体下方的阴影" 336 $((BOTTOM + 12)) 0

echo
echo "== 5. 时间窗有多长：到达左上角后分别等 N 毫秒再按下，各 10 次 =="
for delay in 0 2 5 8 12 20 40; do
  flick_test "等 ${delay} 毫秒" 4 4 "$delay"
done

if [ "$OS" != Darwin ]; then
  # 只有 Windows 需要测这一项：那里切换穿透是改窗口样式，光标每进出轮廓一次样式就变一次
  echo
  echo "== 6. 光标在轮廓里外快速来回（每秒十几次），切换穿透会不会让画面闪 =="
  "$BUILD/probe" flicker "$APP_PID" "$OUT" "${PREFIX}flicker"
fi

echo
echo "截图在 ${OUT}/ 下：*-screen.png 是屏幕上的实际效果，*-panel.png 是面板窗口自己的样子（带 alpha）。"
