// 异形面板：透明窗口 + 九宫格异形皮肤 + 高度跟随内容 + 透明区域点击穿透
const { invoke } = window.__TAURI__.core;
const { getCurrentWindow, LogicalSize } = window.__TAURI__.window;
const { listen } = window.__TAURI__.event;

const panel = getCurrentWindow();
const root = document.getElementById("panel");
const results = document.getElementById("results");
const hud = document.getElementById("hud");

const WIDTH = 552; // 与皮肤图的标称宽度一致
const IMAGE_SCALE = 2; // 皮肤图是 2x 图：固定边里 1 个 CSS 像素对应 2 个图片像素
// 九宫格切片（CSS 像素），与 panel.css、scripts/gen-panel-png.mjs 保持一致
const SLICE = { top: 92, right: 56, bottom: 66, left: 66 };
// 命中阈值：皮肤图里本体 alpha 是 0.78，阴影最高约 0.38，所以落在阴影上的点击会穿透
const HIT_ALPHA = 0.5;

// 把前端状态打到标准输出，方便在终端里观察
const log = (msg) => invoke("log", { msg }).catch(() => {});

// ---- 假的搜索结果，用来制造高度变化 ---------------------------------------
const APPS = [
  ["终端", "Terminal", "🖥️"],
  ["访达", "Finder", "📁"],
  ["系统设置", "System Settings", "⚙️"],
  ["Safari", "浏览器", "🧭"],
  ["Xcode", "开发工具", "🔨"],
  ["微信", "WeChat", "💬"],
  ["音乐", "Music", "🎵"],
];
let rowCount = 3;

function renderRows() {
  results.replaceChildren(
    ...APPS.slice(0, rowCount).map(([title, subtitle, icon], i) => {
      const li = document.createElement("li");
      if (i === 0) li.dataset.selected = "";
      li.innerHTML = `<span class="icon">${icon}</span><span class="title">${title}</span><span class="subtitle">${subtitle}</span>`;
      return li;
    }),
  );
}

// ---- 皮肤图的 alpha：按像素判断某个点是不是落在轮廓里 -------------------------
let mask = null; // 皮肤图解码后的像素

async function loadMask() {
  const img = new Image();
  img.src = "/assets/panel@2x.png";
  await img.decode();
  const canvas = document.createElement("canvas");
  canvas.width = img.naturalWidth;
  canvas.height = img.naturalHeight;
  const ctx = canvas.getContext("2d", { willReadFrequently: true });
  ctx.drawImage(img, 0, 0);
  mask = ctx.getImageData(0, 0, canvas.width, canvas.height);
}

// 九宫格的逆映射：窗口内的 CSS 像素 -> 皮肤图里的图片像素
function toImagePixel(p, size, lead, trail, imageSize) {
  if (p < lead) return p * IMAGE_SCALE;
  if (p < size - trail) {
    return lead * IMAGE_SCALE + ((p - lead) * (imageSize - (lead + trail) * IMAGE_SCALE)) / (size - lead - trail);
  }
  return imageSize - trail * IMAGE_SCALE + (p - (size - trail)) * IMAGE_SCALE;
}

// (x, y) 是相对窗口左上角的 CSS 像素
function skinAlpha(x, y) {
  if (!mask) return 0;
  const px = Math.floor(toImagePixel(x, root.clientWidth, SLICE.left, SLICE.right, mask.width));
  const py = Math.floor(toImagePixel(y, root.clientHeight, SLICE.top, SLICE.bottom, mask.height));
  if (px < 0 || py < 0 || px >= mask.width || py >= mask.height) return 0;
  return mask.data[(py * mask.width + px) * 4 + 3] / 255;
}

// ---- 命中掩码：交给 Rust，由它按光标位置直接决定是否穿透 ----------------------
// 窗口内每个逻辑像素：落在轮廓内、或者落在交互元素上，算实体（1），其余穿透（0）。
// 判断放在原生侧，是因为窗口一旦开始忽略鼠标事件，网页就收不到 mousemove 了；
// 而且“发事件给网页、网页判断、再调回原生”这个往返足够长，光标快速移出轮廓后立刻点击会被透明部分吃掉。
function sendHitMask() {
  const w = root.clientWidth;
  const h = root.clientHeight;
  const solid = new Uint8Array(w * h);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) solid[y * w + x] = skinAlpha(x, y) >= HIT_ALPHA ? 1 : 0;
  }
  for (const el of root.querySelectorAll("button, input, li")) {
    const b = el.getBoundingClientRect();
    for (let y = Math.max(0, Math.floor(b.top)); y < Math.min(h, b.bottom); y++) {
      for (let x = Math.max(0, Math.floor(b.left)); x < Math.min(w, b.right); x++) solid[y * w + x] = 1;
    }
  }
  // 游程编码：从 0 开始，依次是“非实体的长度、实体的长度、……”
  const runs = [];
  let value = 0;
  let run = 0;
  for (const v of solid) {
    if (v === value) {
      run++;
    } else {
      runs.push(run);
      value = v;
      run = 1;
    }
  }
  runs.push(run);
  invoke("set_hit_mask", { width: w, height: h, runs }).catch((e) => log(`set_hit_mask 失败：${e}`));
}

// ---- 原生窗口高度跟随内容 --------------------------------------------------
// 原生窗口只在高度真的变了的时候调一次 setSize；布局过程中随便怎么变都不触发
let fitting = false;
let fitPending = false;

function requestFit() {
  fitPending = true;
  if (!fitting) fitWindow();
}

async function fitWindow() {
  fitting = true;
  fitPending = false;
  const height = Math.ceil(root.getBoundingClientRect().height);
  const size = await panel.innerSize();
  if (size.toLogical(await panel.scaleFactor()).height !== height) {
    await panel.setSize(new LogicalSize(WIDTH, height));
  }
  fitting = false;
  if (fitPending) {
    fitWindow();
  } else {
    // 尺寸定了再更新掩码
    sendHitMask();
    reportGeometry();
  }
}

async function reportGeometry() {
  const [position, scale] = [await panel.outerPosition(), await panel.scaleFactor()];
  const b = document.getElementById("add").getBoundingClientRect();
  const add = [b.x, b.y, b.width, b.height].map(Math.round);
  const g = { x: position.x, y: position.y, scale, w: root.clientWidth, h: root.clientHeight, add };
  log(`geometry ${JSON.stringify(g)}`);
}

// ---- 悬停提示 --------------------------------------------------------------
// 光标位置和命中结果都来自 Rust 侧的轮询
let ignoring = false;

async function onCursorMove({ x, y, solid, mode }) {
  hud.textContent = `光标 (${Math.round(x)}, ${Math.round(y)})  轮廓内:${solid ? "是" : "否"}  点击穿透:${solid ? "关" : "开"}`;
  // 默认的 native 模式下，切换穿透已经在 Rust 侧完成了。
  // 环境变量 IRREGULAR_HIT_MODE=js 时改由这里调回去，多一次前后端往返，用来对比两种做法的时间窗。
  if (mode !== "js" || !solid === ignoring) return;
  ignoring = !solid;
  await panel.setIgnoreCursorEvents(ignoring);
  log(`ignore-cursor-events -> ${ignoring}`);
}

window.addEventListener("pointerdown", (e) => {
  log(`panel pointerdown (${Math.round(e.clientX)}, ${Math.round(e.clientY)})`);
});

document.getElementById("add").addEventListener("click", () => {
  rowCount = Math.min(APPS.length, rowCount + 1);
  renderRows();
  log(`row added, rows=${rowCount}`);
});

document.getElementById("remove").addEventListener("click", () => {
  rowCount = Math.max(0, rowCount - 1);
  renderRows();
  log(`row removed, rows=${rowCount}`);
});

// ---- 启动 ------------------------------------------------------------------
renderRows();
await loadMask();
await listen("cursor-move", ({ payload }) => onCursorMove(payload));
new ResizeObserver(requestFit).observe(root);
await fitWindow();
// 等第一帧画完、窗口尺寸对齐之后再显示，避免出现闪一下的矩形
await panel.show();
await panel.setFocus();
