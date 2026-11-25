// 生成异形面板皮肤图：src/assets/panel@2x.png
//
//   - 不规则轮廓：四个圆角半径各不相同，底部有一个尖角“尾巴”，右侧有一个内凹缺口
//   - 本体半透明（BODY_ALPHA），边缘按带符号距离场做抗锯齿
//   - 阴影直接烘在图里（柔和、半透明），只出现在轮廓之外
//   - 可以按九宫格拉伸：四角和四边保持原样，中间区域随窗口高度拉伸
//
// 用法：node scripts/gen-panel-png.mjs
import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// ---- 尺寸与外观（单位均为逻辑像素） --------------------------------------
const SCALE = 2; // 图片倍率，Retina 屏上 1 逻辑像素 = 2 图片像素
const MARGIN = 36; // 四周留给阴影的透明边距
const BODY_W = 480;
const BODY_H = 220; // 本体标称高度，也是九宫格能容纳的最小高度
const RADIUS = { tl: 30, tr: 12, br: 24, bl: 8 };
const TAIL = { x: 64, half: 18, len: 30, overlap: 6, round: 3 }; // 底部尖角：位置、半宽、伸出长度、与本体重叠、圆角
const NOTCH = { x: BODY_W, y: 34, r: 20 }; // 右侧内凹缺口：圆心在右边缘上
const BODY_RGB = [0x23, 0x2b, 0x3d];
const BODY_ALPHA = 0.78;
const SHADOW = { sigma: 9, offsetY: 12, alpha: 0.38 };

// 九宫格切片（逻辑像素）= 边距 + 该方向上必须保持原样的部分。
// 改上面的参数时，要同步改 src/panel.css 里的 --slice-* 和 src/panel.js 里的 SLICE。
//   top:    MARGIN + (NOTCH.y + NOTCH.r)      = 36 + 54 = 90 -> 取 92
//   right:  MARGIN + NOTCH.r                  = 36 + 20 = 56
//   bottom: MARGIN + TAIL.len                 = 36 + 30 = 66
//   left:   MARGIN + max(RADIUS.tl, RADIUS.bl) = 36 + 30 = 66
export const SLICES = { top: 92, right: 56, bottom: 66, left: 66 };

// ---- 带符号距离函数（负值在形状内部） --------------------------------------
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

function sdRoundRect(px, py, hx, hy, r) {
  const radius = px < 0 ? (py < 0 ? r.tl : r.bl) : py < 0 ? r.tr : r.br;
  const qx = Math.abs(px) - (hx - radius);
  const qy = Math.abs(py) - (hy - radius);
  const ox = Math.max(qx, 0);
  const oy = Math.max(qy, 0);
  return Math.hypot(ox, oy) + Math.min(Math.max(qx, qy), 0) - radius;
}

// Inigo Quilez 的精确三角形距离
function sdTriangle(px, py, [x0, y0], [x1, y1], [x2, y2]) {
  const e = [
    [x1 - x0, y1 - y0],
    [x2 - x1, y2 - y1],
    [x0 - x2, y0 - y2],
  ];
  const v = [
    [px - x0, py - y0],
    [px - x1, py - y1],
    [px - x2, py - y2],
  ];
  const s = Math.sign(e[0][0] * e[2][1] - e[0][1] * e[2][0]);
  let best = [Infinity, Infinity];
  for (let k = 0; k < 3; k++) {
    const [ex, ey] = e[k];
    const [vx, vy] = v[k];
    const t = clamp((vx * ex + vy * ey) / (ex * ex + ey * ey), 0, 1);
    const dx = vx - ex * t;
    const dy = vy - ey * t;
    best = [Math.min(best[0], dx * dx + dy * dy), Math.min(best[1], s * (vx * ey - vy * ex))];
  }
  return -Math.sqrt(best[0]) * Math.sign(best[1]);
}

// 本体坐标系：(0, 0) 是本体左上角
function shapeDistance(x, y) {
  const box = sdRoundRect(x - BODY_W / 2, y - BODY_H / 2, BODY_W / 2, BODY_H / 2, RADIUS);
  // 三角形先画小一圈，再用“距离减 round”外扩，得到带圆角的尖角，尖端正好落在 BODY_H + TAIL.len
  const tipY = BODY_H + TAIL.len - TAIL.round;
  const baseY = BODY_H - TAIL.overlap;
  const tail =
    sdTriangle(x, y, [TAIL.x, tipY], [TAIL.x + TAIL.half, baseY], [TAIL.x - TAIL.half, baseY]) - TAIL.round;
  const notch = -(Math.hypot(x - NOTCH.x, y - NOTCH.y) - NOTCH.r);
  return Math.max(Math.min(box, tail), notch);
}

// ---- 渲染 -----------------------------------------------------------------
const W0 = BODY_W + 2 * MARGIN;
const H0 = MARGIN + BODY_H + TAIL.len + MARGIN;
const W = W0 * SCALE;
const H = H0 * SCALE;

const coverage = new Float32Array(W * H);
for (let j = 0; j < H; j++) {
  const y = (j + 0.5) / SCALE - MARGIN;
  for (let i = 0; i < W; i++) {
    const x = (i + 0.5) / SCALE - MARGIN;
    coverage[j * W + i] = clamp(0.5 - shapeDistance(x, y) * SCALE, 0, 1);
  }
}

// 可分离高斯模糊，用来生成阴影
function gaussianBlur(src, sigma) {
  const radius = Math.ceil(sigma * 2.6);
  const kernel = new Float32Array(radius * 2 + 1);
  let sum = 0;
  for (let k = -radius; k <= radius; k++) {
    const w = Math.exp(-(k * k) / (2 * sigma * sigma));
    kernel[k + radius] = w;
    sum += w;
  }
  for (let k = 0; k < kernel.length; k++) kernel[k] /= sum;

  const tmp = new Float32Array(W * H);
  const out = new Float32Array(W * H);
  for (let j = 0; j < H; j++) {
    for (let i = 0; i < W; i++) {
      let acc = 0;
      for (let k = -radius; k <= radius; k++) acc += src[j * W + clamp(i + k, 0, W - 1)] * kernel[k + radius];
      tmp[j * W + i] = acc;
    }
  }
  for (let j = 0; j < H; j++) {
    for (let i = 0; i < W; i++) {
      let acc = 0;
      for (let k = -radius; k <= radius; k++) acc += tmp[clamp(j + k, 0, H - 1) * W + i] * kernel[k + radius];
      out[j * W + i] = acc;
    }
  }
  return out;
}

const blurred = gaussianBlur(coverage, SHADOW.sigma * SCALE);
const offset = SHADOW.offsetY * SCALE;

const rgba = Buffer.alloc(W * H * 4);
for (let j = 0; j < H; j++) {
  for (let i = 0; i < W; i++) {
    const idx = j * W + i;
    const cov = coverage[idx];
    const aBody = cov * BODY_ALPHA;
    // 阴影只画在轮廓之外，这样本体各处的透明度都是 BODY_ALPHA
    const src = j - offset >= 0 ? blurred[(j - offset) * W + i] : 0;
    const aShadow = SHADOW.alpha * src * (1 - cov);
    const aOut = aBody + aShadow * (1 - aBody);
    // 先按预乘 alpha 合成，再还原成 PNG 用的直通 alpha
    const out = idx * 4;
    for (let c = 0; c < 3; c++) rgba[out + c] = Math.round((BODY_RGB[c] * aBody) / (aOut || 1));
    rgba[out + 3] = Math.round(aOut * 255);
  }
}

// ---- 编码 PNG -------------------------------------------------------------
const CRC_TABLE = Uint32Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc32 = (buf) => {
  let c = 0xffffffff;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};
const chunk = (type, data) => {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
};

function encodePng(width, height, pixels) {
  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0; // 过滤器类型 0（不过滤）
    pixels.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // 位深
  ihdr[9] = 6; // 颜色类型 6：RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(raw, { level: 9 })),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

const outDir = join(dirname(fileURLToPath(import.meta.url)), "..", "src", "assets");
mkdirSync(outDir, { recursive: true });
const outFile = join(outDir, `panel@${SCALE}x.png`);
writeFileSync(outFile, encodePng(W, H, rgba));

// ---- 自检用的采样 ---------------------------------------------------------
const alphaAt = (x, y) => rgba[((Math.round(y * SCALE)) * W + Math.round(x * SCALE)) * 4 + 3];
// [名称, x, y, 期望的 alpha 范围]
const BODY_A = Math.round(BODY_ALPHA * 255);
const SHADOW_MAX = Math.ceil(SHADOW.alpha * 255);
const samples = [
  ["左上角外，应为 0", 2, 2, [0, 0]],
  ["右上圆角外，应为 0", W0 - 2, 2, [0, 0]],
  ["本体内部", MARGIN + 8, MARGIN + 120, [BODY_A, BODY_A]],
  ["尾巴尖端（本体）", MARGIN + TAIL.x, MARGIN + BODY_H + TAIL.len - 2, [BODY_A, BODY_A]],
  ["缺口中心，只有阴影", MARGIN + BODY_W - 10, MARGIN + NOTCH.y, [1, SHADOW_MAX]],
  ["尾巴尖端下方，只有阴影", MARGIN + TAIL.x, MARGIN + BODY_H + TAIL.len + 3, [1, SHADOW_MAX]],
  ["本体正下方 12px，只有阴影", MARGIN + 300, MARGIN + BODY_H + 12, [1, SHADOW_MAX]],
];

console.log(`wrote ${outFile}  ${W}x${H}px (${W0}x${H0} logical)`);
console.log(`slices (logical px): ${JSON.stringify(SLICES)}`);
let failed = 0;
for (const [name, x, y, [lo, hi]] of samples) {
  const a = alphaAt(x, y);
  const ok = a >= lo && a <= hi;
  if (!ok) failed++;
  console.log(`  ${ok ? "ok  " : "FAIL"} alpha ${name.padEnd(26)} (${x},${y}) = ${a}  期望 ${lo}..${hi}`);
}
process.exitCode = failed ? 1 : 0;
