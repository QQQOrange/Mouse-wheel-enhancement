// tools/make-icon.js —— 生成 app.ico（多尺寸 PNG 条目，4x 超采样抗锯齿）
// 纯 Node 实现，不依赖任何第三方包。仓库里保留它，方便以后改配色重生成。
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const SIZES = [16, 24, 32, 48, 64, 128, 256];
const SS = 4; // 超采样倍数

const BG = [0x12, 0x14, 0x1a];
const FG = [0x5b, 0x8c, 0xff];
const FG2 = [0x8f, 0xb0, 0xff];

function makeCanvas(n) {
  return { n, px: new Float32Array(n * n * 4) }; // 预乘前的直通 RGBA 0..1
}

function blend(c, x, y, r, g, b, a) {
  if (x < 0 || y < 0 || x >= c.n || y >= c.n || a <= 0) return;
  const i = (y * c.n + x) * 4;
  const p = c.px;
  const dr = p[i], dg = p[i + 1], db = p[i + 2], da = p[i + 3];
  const na = a + da * (1 - a);
  if (na <= 0) return;
  p[i] = (r * a + dr * da * (1 - a)) / na;
  p[i + 1] = (g * a + dg * da * (1 - a)) / na;
  p[i + 2] = (b * a + db * da * (1 - a)) / na;
  p[i + 3] = na;
}

// 圆角矩形（含 1px 内缩边距），返回覆盖判断
function roundRectSDF(x, y, w, h, r) {
  const dx = Math.max(x - r, 0, r - x - w);
  const dy = Math.max(y - r, 0, r - y - h);
  return Math.hypot(dx, dy) - r;
}

function render(size) {
  const n = size * SS;
  const c = makeCanvas(n);
  const s = n / 32; // 以 32 单位设计稿为基准

  // 底板：圆角方块
  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) {
      const d = roundRectSDF(x + 0.5, y + 0.5, n - 1, n - 1, 7.5 * s);
      if (d <= 0) blend(c, x, y, BG[0] / 255, BG[1] / 255, BG[2] / 255, 1);
    }
  }

  // 滚轮本体：竖向胶囊
  const cx = 16 * s;
  const wHalf = 5.2 * s;
  const hHalf = 10.4 * s;
  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) {
      const px = Math.abs(x + 0.5 - cx);
      const py = Math.abs(y + 0.5 - cx); // cx 就是画布中心（正方形），纵向同样以它为轴
      // 胶囊：|px| <= r 且 |py| <= hHalf - r → 中间矩形；两端补圆头
      const r = wHalf;
      let ok = false;
      if (px <= r && py <= hHalf - r) ok = true;
      else {
        const dy = py - (hHalf - r);
        if (dy > 0 && Math.hypot(px, dy) <= r) ok = true;
      }
      if (ok) blend(c, x, y, FG[0] / 255, FG[1] / 255, FG[2] / 255, 1);
    }
  }

  // 轮上的两道刻线（深色），做出「滚轮」的辨识度
  for (let k = 0; k < 2; k++) {
    const cy = (13.2 + k * 4.2) * s;
    for (let y = 0; y < n; y++) {
      for (let x = 0; x < n; x++) {
        const px = Math.abs(x + 0.5 - cx);
        const py = Math.abs(y + 0.5 - cy);
        if (px <= 3.1 * s && py <= 0.62 * s) blend(c, x, y, BG[0] / 255, BG[1] / 255, BG[2] / 255, 0.92);
      }
    }
  }

  // 右侧加速双箭头（提示「增强」）
  const ax = 25.4 * s;
  for (let k = 0; k < 2; k++) {
    const ay = (11.4 + k * 5.6) * s;
    for (let t = 0; t < 40; t++) {
      const f = t / 39;
      // 尖头朝上：f=0 时在顶部且宽度为 0，往下逐渐变宽
      const yy = ay + f * 3.1 * s;
      const halfw = 2.5 * s * f;
      for (let x = Math.floor(ax - halfw); x <= Math.ceil(ax + halfw); x++) {
        if (x >= 0 && x < n) blend(c, x, Math.round(yy), FG2[0] / 255, FG2[1] / 255, FG2[2] / 255, 0.88);
      }
    }
  }

  // 下采样
  const out = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let r = 0, g = 0, b = 0, a = 0;
      for (let sy = 0; sy < SS; sy++) {
        for (let sx = 0; sx < SS; sx++) {
          const i = ((y * SS + sy) * n + (x * SS + sx)) * 4;
          const pa = c.px[i + 3];
          r += c.px[i] * pa; g += c.px[i + 1] * pa; b += c.px[i + 2] * pa; a += pa;
        }
      }
      const cnt = SS * SS;
      const o = (y * size + x) * 4;
      if (a > 0) { out[o] = Math.round(r / a * 255); out[o + 1] = Math.round(g / a * 255); out[o + 2] = Math.round(b / a * 255); }
      out[o + 3] = Math.round(a / cnt * 255);
    }
  }
  return out;
}

function crc32(buf) {
  let c, crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = (crc >>> 8) ^ c;
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}

function png(rgba, size) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0;
    rgba.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

const images = SIZES.map((s) => ({ s, data: png(render(s), s) }));

const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(images.length, 4);
let offset = 6 + 16 * images.length;
const entries = [];
for (const im of images) {
  const e = Buffer.alloc(16);
  e[0] = im.s === 256 ? 0 : im.s;
  e[1] = im.s === 256 ? 0 : im.s;
  e[2] = 0; e[3] = 0;
  e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6);
  e.writeUInt32LE(im.data.length, 8);
  e.writeUInt32LE(offset, 12);
  offset += im.data.length;
  entries.push(e);
}

const ico = Buffer.concat([header, ...entries, ...images.map((i) => i.data)]);
const out = path.join(__dirname, '..', 'app.ico');
fs.writeFileSync(out, ico);
// 顺便导出 256 px PNG 预览，方便肉眼检查
fs.writeFileSync(path.join(__dirname, 'icon-preview.png'), images[images.length - 1].data);
console.log('wrote', out, ico.length, 'bytes; sizes:', SIZES.join(','));
