import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import test from "node:test";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const t = JSON.parse(readFileSync(join(here, "tokens.json"), "utf8"));

const lin = (c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
function luminance(hex) {
  const n = parseInt(hex.slice(1), 16);
  const [r, g, b] = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((v) => lin(v / 255));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
export function ratio(a, b) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

test("contrast helper matches known WCAG values", () => {
  assert.ok(Math.abs(ratio("#000000", "#FFFFFF") - 21) < 0.01);
  assert.ok(Math.abs(ratio("#777777", "#FFFFFF") - 4.48) < 0.02);
});

for (const [name, theme] of Object.entries(t.themes)) {
  test(`${name}: every text/background pair meets AA (4.5:1)`, () => {
    for (const [fg, bg] of t.pairs) {
      const r = ratio(theme[fg], theme[bg]);
      assert.ok(r >= 4.5, `${name}: ${fg} ${theme[fg]} on ${bg} ${theme[bg]} = ${r.toFixed(2)}`);
    }
  });

  test(`${name}: borders and focus rings meet 3:1 against their surface`, () => {
    for (const [fg, bg, min] of t.uiPairs) {
      const r = ratio(theme[fg], theme[bg]);
      assert.ok(r >= min, `${name}: ${fg} on ${bg} = ${r.toFixed(2)}`);
    }
  });
}

test("KPI tile gradients keep white text readable at both ends", () => {
  for (const [name, stops] of Object.entries(t.gradients)) {
    for (const stop of stops) {
      const r = ratio(t.kpiTileText, stop);
      assert.ok(r >= 4.5, `${name} stop ${stop} = ${r.toFixed(2)}`);
    }
  }
});

test("both themes define exactly the same tokens", () => {
  assert.deepEqual(Object.keys(t.themes.light).sort(), Object.keys(t.themes.dark).sort());
});

test("touch targets are at least 48px", () => assert.ok(t.touchTarget >= 48));

test("generated files are up to date", () => {
  const before = ["tokens.css", "tokens.ts"].map((f) => readFileSync(join(here, f), "utf8"));
  execFileSync(process.execPath, [join(here, "build.mjs")]);
  const after = ["tokens.css", "tokens.ts"].map((f) => readFileSync(join(here, f), "utf8"));
  assert.deepEqual(after, before, "run `pnpm --filter @urbanfix/tokens build` and commit the result");
});
