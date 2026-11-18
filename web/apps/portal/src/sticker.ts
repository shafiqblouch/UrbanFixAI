// A tiny, dependency-free Code 39 barcode for the printable gate sticker (GT-05). Code 39 needs no checksum and
// covers A–Z and 0–9, which is exactly the no-look-alike alphabet our sticker codes use. Each character is nine
// elements (five bars, four spaces) that are narrow or wide; characters are separated by one narrow space.

const CODE39: Record<string, string> = {
  "0": "nnnwwnwnn", "1": "wnnwnnnnw", "2": "nnwwnnnnw", "3": "wnwwnnnnn", "4": "nnnwwnnnw",
  "5": "wnnwwnnnn", "6": "nnwwwnnnn", "7": "nnnwnnwnw", "8": "wnnwnnwnn", "9": "nnwwnnwnn",
  A: "wnnnnwnnw", B: "nnwnnwnnw", C: "wnwnnwnnn", D: "nnnnwwnnw", E: "wnnnwwnnn", F: "nnwnwwnnn",
  G: "nnnnnwwnw", H: "wnnnnwwnn", I: "nnwnnwwnn", J: "nnnnwwwnn", K: "wnnnnnnww", L: "nnwnnnnww",
  M: "wnwnnnnwn", N: "nnnnwnnww", O: "wnnnwnnwn", P: "nnwnwnnwn", Q: "nnnnnnwww", R: "wnnnnnwwn",
  S: "nnwnnnwwn", T: "nnnnwnwwn", U: "wwnnnnnnw", V: "nwwnnnnnw", W: "wwwnnnnnn", X: "nwnnwnnnw",
  Y: "wwnnwnnnn", Z: "nwwnwnnnn", "-": "nwnnnnwnw", ".": "wwnnnnwnn", " ": "nwwnnnwnn", "*": "nwnnwnwnn",
};

/** The black bars (in abstract "narrow units") for a code, wrapped in the required `*` start/stop guards. */
function bars(code: string): { bars: { x: number; w: number }[]; width: number } {
  const text = `*${code.toUpperCase().replace(/[^0-9A-Z\-. ]/g, "")}*`;
  const out: { x: number; w: number }[] = [];
  let x = 0;
  for (const ch of text) {
    const pattern = CODE39[ch];
    if (!pattern) continue;
    for (let i = 0; i < 9; i++) {
      const w = pattern[i] === "w" ? 3 : 1;
      if (i % 2 === 0) out.push({ x, w }); // even elements are bars, odd are spaces
      x += w;
    }
    x += 1; // one narrow space between characters
  }
  return { bars: out, width: x };
}

/** An inline SVG string for the barcode, sized by `unit` px per narrow element and `height` px tall. */
export function code39Svg(code: string, unit = 2, height = 64): string {
  const { bars: b, width } = bars(code);
  const rects = b.map((r) => `<rect x="${r.x * unit}" y="0" width="${r.w * unit}" height="${height}"/>`).join("");
  return `<svg width="${width * unit}" height="${height}" viewBox="0 0 ${width * unit} ${height}" xmlns="http://www.w3.org/2000/svg" fill="#000" shape-rendering="crispEdges">${rects}</svg>`;
}

/** Opens a print window with one sticker: caption, plate, the Code 39 barcode and the human code. */
export function printSticker({ plate, code, caption }: { plate: string; code: string; caption: string }): void {
  const esc = (s: string) => s.replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]!));
  const html = `<!doctype html><html><head><meta charset="utf-8"><title>${esc(code)}</title><style>
    @page { margin: 8mm; }
    body { font-family: system-ui, sans-serif; text-align: center; color: #000; }
    .label { border: 2px solid #000; border-radius: 10px; padding: 14px 18px; display: inline-block; min-width: 240px; }
    .cap { font-size: 11px; letter-spacing: .12em; text-transform: uppercase; color: #333; }
    .plate { font-size: 30px; font-weight: 800; letter-spacing: .05em; margin: 6px 0 10px; }
    .code { font-family: ui-monospace, monospace; font-size: 15px; letter-spacing: .22em; margin-top: 6px; }
    svg { max-width: 100%; }
  </style></head><body><div class="label">
    <div class="cap">${esc(caption)}</div>
    <div class="plate">${esc(plate)}</div>
    ${code39Svg(code)}
    <div class="code">${esc(code)}</div>
  </div><script>window.onload=function(){window.focus();window.print();}</script></body></html>`;
  const w = window.open("", "_blank", "width=420,height=360");
  if (!w) return;
  w.document.open();
  w.document.write(html);
  w.document.close();
}
