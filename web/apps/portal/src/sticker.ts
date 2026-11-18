import QRCode from "qrcode";

// The printable gate sticker (GT-05): a QR code of the car's sticker code, plus the plate and the code in text.
// QR is read far more reliably by a phone camera than a 1D barcode, which matters for the guard app's scanner.
// The UHF chip still carries the same code over radio for the fixed gate reader; this label is the human/phone copy.

const esc = (s: string) => s.replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]!));

function labelHtml(plate: string, code: string, caption: string, qrSvg: string): string {
  return `<!doctype html><html><head><meta charset="utf-8"><title>${esc(code)}</title><style>
    @page { margin: 8mm; }
    body { font-family: system-ui, sans-serif; text-align: center; color: #000; margin: 0; padding: 12px; }
    .label { border: 2px solid #000; border-radius: 10px; padding: 14px 18px; display: inline-block; min-width: 200px; }
    .cap { font-size: 11px; letter-spacing: .12em; text-transform: uppercase; color: #333; }
    .plate { font-size: 30px; font-weight: 800; letter-spacing: .05em; margin: 6px 0 10px; }
    .qr { width: 150px; height: 150px; margin: 0 auto; }
    .qr svg { width: 100%; height: 100%; }
    .code { font-family: ui-monospace, monospace; font-size: 15px; letter-spacing: .22em; margin-top: 10px; }
  </style></head><body><div class="label">
    <div class="cap">${esc(caption)}</div>
    <div class="plate">${esc(plate)}</div>
    <div class="qr">${qrSvg}</div>
    <div class="code">${esc(code)}</div>
  </div><script>window.onload=function(){window.focus();window.print();}</script></body></html>`;
}

/** Opens a print window with one sticker: caption, plate, a QR of the code, and the code in text. */
export function printSticker({ plate, code, caption }: { plate: string; code: string; caption: string }): void {
  // Open synchronously inside the click gesture so the pop-up is not blocked; fill it once the QR has rendered.
  const w = window.open("", "_blank", "width=440,height=420");
  if (!w) return;
  w.document.write("<!doctype html><title>…</title><body style=\"font-family:system-ui;text-align:center;padding:40px\">Preparing sticker…</body>");
  QRCode.toString(code, { type: "svg", margin: 1, width: 150, errorCorrectionLevel: "M" })
    .then((qrSvg) => { w.document.open(); w.document.write(labelHtml(plate, code, caption, qrSvg)); w.document.close(); })
    .catch(() => w.close());
}
