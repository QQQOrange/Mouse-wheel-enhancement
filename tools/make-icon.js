// tools/make-icon.js —— 生成 app.ico（多尺寸 PNG 条目，4x 超采样抗锯齿）
//
// 设计（32 单位方格为基准）：
//   深色圆角底板
//   + 浅色鼠标俯视轮廓（上窄下宽，像真鼠标）
//   + 顶部一条按键分隔线（把上半分成左右键，这一笔是「这是鼠标」的关键）
//   + 亮蓝滚轮（竖向胶囊，位于上半部）
//   + 滚轮内的上下双向箭头（表示滚动，也顺带表示「增强」）
//
// 为什么不是「一个胶囊 + 两个箭头」：那样只有认识这个软件的人才知道是滚轮，
// 图标的第一职责是让不认识的人也能认出「跟鼠标有关」。
//
// 用法：
//   node tools/make-icon.js           生成 app.ico 与 tools/icon-preview.png
//   node tools/make-icon.js --ascii   额外打印 32px 的字符画（改形状时用来核对）
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const SIZES = [16, 24, 32, 48, 64, 128, 256];
const SS = 4; // 超采样倍数

/* ══════════════ 配色 ══════════════ */
const BG_A = [0x1a, 0x22, 0x31]; // 底板左上（略亮）
const BG_B = [0x0a, 0x0d, 0x14]; // 底板右下
const MOUSE = [0xe9, 0xef, 0xfb]; // 鼠标外壳：近白微蓝
const WHEEL_TOP = [0x93, 0xbc, 0xff]; // 滚轮顶部
const WHEEL_BOT = [0x39, 0x6a, 0xe8]; // 滚轮底部
const ARROW = [0x08, 0x0c, 0x14]; // 刻线：在蓝滚轮上压出的深槽
const EDGE = [0x2a, 0x38, 0x52]; // 鼠标外圈 1px 描边（深底上提亮轮廓）

/* ══════════════ 基础绘制 ══════════════ */
function makeCanvas(n) {
  return { n, px: new Float32Array(n * n * 4) }; // 直通 RGBA 0..1
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

const lerp = (a, b, t) => a + (b - a) * t;

/** 圆角矩形 SDF：矩形 [x0,x1]×[y0,y1]，半径 r。<=0 为内部。 */
function rrSDF(X, Y, x0, y0, x1, y1, r) {
  const hx = (x1 - x0) / 2 - r;
  const hy = (y1 - y0) / 2 - r;
  const dx = Math.abs(X - (x0 + x1) / 2) - hx;
  const dy = Math.abs(Y - (y0 + y1) / 2) - hy;
  return Math.hypot(Math.max(dx, 0), Math.max(dy, 0)) + Math.min(Math.max(dx, dy), 0) - r;
}

/* ══════════════ 鼠标轮廓 ══════════════ */
/* 用「半宽剖面」定义形状：给一组 (y, 半宽) 控制点，中间平滑插值。
 *
 * 为什么不用椭圆：椭圆的上端一定是尖的，32px 下看起来像水滴 —— 而真鼠标
 * 俯视是「前端较宽的圆弧 + 中下部最宽 + 尾端圆收」。剖面曲线能精确控制这三段。
 *
 * 这份数据托盘图标也会用（GDI+ 那边按同样控制点采样成多边形），
 * 两处的形状因此永远一致。 */
const PROFILE = [
  [3.9, 0.3],
  [4.4, 3.6],
  [5.6, 5.1],
  [7.4, 6.2],
  [10.0, 7.0],
  [14.0, 7.6],
  [18.0, 7.8],
  [22.0, 7.4],
  [25.0, 6.5],
  [27.0, 4.8],
  [28.1, 0.3],
];

const smoothstep = (t) => t * t * (3 - 2 * t);

function mouseHalfW(Y) {
  const P = PROFILE;
  if (Y <= P[0][0] || Y >= P[P.length - 1][0]) return 0;
  for (let i = 0; i < P.length - 1; i++) {
    const y0 = P[i][0], w0 = P[i][1], y1 = P[i + 1][0], w1 = P[i + 1][1];
    if (Y >= y0 && Y <= y1) {
      return w0 + (w1 - w0) * smoothstep((Y - y0) / (y1 - y0));
    }
  }
  return 0;
}

/* ══════════════ 滚轮 ══════════════ */
const W = { cx: 16, yTop: 9.4, yBot: 18.0, r: 0 };  // r 由 rim() 按尺寸给

/* 滚轮半宽。小尺寸要更粗一点：16px 下 2.75 算出来只有 1.4px，
 * 再被抗锯齿吃掉一半就看不见了。 */
function rim(size) { return size <= 24 ? 3.0 : 2.78; }

function inWheel(X, Y, r) {
  const cy = (W.yTop + W.yBot) / 2;
  const h = (W.yBot - W.yTop) / 2;
  const dx = Math.abs(X - W.cx);
  const dy = Math.abs(Y - cy);
  if (dy <= h - r) return dx <= r;
  return Math.hypot(dx, dy - (h - r)) <= r;
}

/** 滚轮上的横向刻线（滚轮的通用视觉符号，比箭头更「像滚轮」也更耐缩小）。 */
const NOTCH = { y1: 11.9, y2: 15.4, halfW: 1.65, halfH: 0.34 };

function inNotch(X, Y) {
  if (Math.abs(X - W.cx) > NOTCH.halfW) return false;
  return Math.abs(Y - NOTCH.y1) <= NOTCH.halfH || Math.abs(Y - NOTCH.y2) <= NOTCH.halfH;
}

/* ══════════════ 渲染 ══════════════ */
function render(size) {
  const n = size * SS;
  const k = 32 / n; // 设备像素 → 设计单位
  const small = size <= 24; // 小尺寸：结构简化、线条加粗
  const mScale = small ? 1.02 : 1.0;
  const lineHalf = small ? 1.0 : 0.66;
  const drawDetail = size >= 48;
  const wheelR = rim(size);

  const c = makeCanvas(n);

  for (let py = 0; py < n; py++) {
    for (let px = 0; px < n; px++) {
      const X = (px + 0.5) * k;
      const Y = (py + 0.5) * k;

      /* ① 底板：圆角方块，左上到右下的极轻渐变 */
      if (rrSDF(X, Y, 0.4, 0.4, 31.6, 31.6, 7.4) > 0) continue;
      const g = Math.min(1, Math.max(0, (X + Y) / 64));
      const bgR = lerp(BG_A[0], BG_B[0], g) / 255;
      const bgG = lerp(BG_A[1], BG_B[1], g) / 255;
      const bgB = lerp(BG_A[2], BG_B[2], g) / 255;
      blend(c, px, py, bgR, bgG, bgB, 1);

      /* ② 鼠标外壳 */
      const hw = mouseHalfW(Y) * mScale;
      if (hw <= 0) continue;
      const dxm = Math.abs(X - 16);
      if (dxm > hw) continue;
      blend(c, px, py, MOUSE[0] / 255, MOUSE[1] / 255, MOUSE[2] / 255, 1);

      /* ②b 外壳内描边：贴着左右边缘压一道暗线，
       *     深色底上「白色蛋形」才不会显得飘，也更像鼠标的分模线 */
      if (hw - dxm < 0.42) blend(c, px, py, EDGE[0] / 255, EDGE[1] / 255, EDGE[2] / 255, 0.5);

      /* ③ 顶部按键分隔线：从顶端一直到滚轮，把上半分成左右键 */
      if (Y >= (small ? 4.0 : 4.6) && Y <= 10.9 && hw > lineHalf + 0.4 && dxm <= lineHalf) {
        blend(c, px, py, bgR, bgG, bgB, 1);
      }

      /* ④ 滚轮：竖向渐变 + 一圈冷色描边 */
      if (inWheel(X, Y, wheelR)) {
        const t = Math.min(1, Math.max(0, (Y - W.yTop) / (W.yBot - W.yTop)));
        blend(c, px, py,
          lerp(WHEEL_TOP[0], WHEEL_BOT[0], t) / 255,
          lerp(WHEEL_TOP[1], WHEEL_BOT[1], t) / 255,
          lerp(WHEEL_TOP[2], WHEEL_BOT[2], t) / 255, 1);

        /* ⑤ 刻线（只在大尺寸画：16px 下它会糊成一片脏点，反而降低辨识度） */
        if (drawDetail && inNotch(X, Y)) {
          blend(c, px, py, ARROW[0] / 255, ARROW[1] / 255, ARROW[2] / 255, 0.78);
        }
      }
    }
  }

  /* 下采样 */
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
      if (a > 0) {
        out[o] = Math.round(r / a * 255);
        out[o + 1] = Math.round(g / a * 255);
        out[o + 2] = Math.round(b / a * 255);
      }
      out[o + 3] = Math.round(a / cnt * 255);
    }
  }
  return out;
}

/* ══════════════ PNG 编码 ══════════════ */
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

/* ══════════════ 字符画（无图形界面时核对形状） ══════════════ */
function ascii(size) {
  const px = render(size);
  const chars = ' .:-=+*#%@';
  const lines = [];
  for (let y = 0; y < size; y++) {
    let line = '';
    for (let x = 0; x < size; x++) {
      const i = (y * size + x) * 4;
      const a = px[i + 3] / 255;
      if (a < 0.12) { line += ' '; continue; }
      // 蓝色区域单独用 'B' 标出来：纯按亮度看不出「哪块是滚轮」
      const r = px[i], g = px[i + 1], b = px[i + 2];
      if (b - r > 55) { line += 'B'; continue; }   // 深槽是「蓝+黑」，色差不够，别误判成滚轮
      const lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
      const v = Math.min(9, Math.max(1, Math.round(lum * a * 9)));
      line += chars[v];
    }
    lines.push(line);
  }
  return lines.join('\n');
}

/* ══════════════ 输出 ══════════════ */
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
fs.writeFileSync(path.join(__dirname, '..', 'app.ico'), ico);

// 预览图：256 看细节，32 看实际大小下的效果
const big = images[images.length - 1];
fs.writeFileSync(path.join(__dirname, 'icon-preview.png'), big.data);
const mid = images.find((i) => i.s === 32);
fs.writeFileSync(path.join(__dirname, 'icon-preview-32.png'), mid.data);

console.log('wrote app.ico:', ico.length, 'bytes; sizes:', SIZES.join(','));

/* --verify：把刚写出的 ico 再读回来校验一遍。
 * 这个文件是二进制手工拼的，条目偏移算错一个字节，Windows 就会安静地
 * 显示默认图标（不报错、不影响运行），只有双击看任务栏才能发现。 */
if (process.argv.includes('--verify')) {
  const buf = fs.readFileSync(path.join(__dirname, '..', 'app.ico'));
  const n = buf.readUInt16LE(4);
  console.log('ico 条目数：' + n + '（期望 ' + SIZES.length + '）');
  let bad = 0;
  for (let i = 0; i < n; i++) {
    const e = 6 + i * 16;
    const w = buf[e] === 0 ? 256 : buf[e];
    const len = buf.readUInt32LE(e + 8);
    const off = buf.readUInt32LE(e + 12);
    const pngOk = buf[off] === 0x89 && buf[off + 1] === 0x50 && buf[off + 2] === 0x4e && buf[off + 3] === 0x47;
    const iw = buf.readUInt32BE(off + 16), ih = buf.readUInt32BE(off + 20);
    const ok = pngOk && iw === w && ih === w && off + len <= buf.length;
    if (!ok) bad++;
    console.log('  ' + (ok ? 'ok ' : 'BAD') + ' ' + String(w).padStart(3) + 'px  条目名 ' + buf[e] + 'x' + buf[e + 1] +
                '  数据 ' + len + 'B  PNG ' + iw + 'x' + ih + ' @' + off);
  }
  console.log(bad === 0 ? '全部校验通过' : ('有 ' + bad + ' 条不合格'));
  process.exitCode = bad === 0 ? 0 : 1;
}

if (process.argv.includes('--ascii') || process.argv.some((a) => a.startsWith('--ascii='))) {
  const one = process.argv.find((a) => a.startsWith('--ascii='));
  if (one) {
    const n = parseInt(one.split('=')[1], 10) || 32;
    console.log('\n===== ' + n + 'px =====');
    console.log(ascii(n));
  } else {
    console.log('\n===== 32px =====');
    console.log(ascii(32));
    console.log('\n===== 16px（任务栏里的实际大小） =====');
    console.log(ascii(16));
  }
}
