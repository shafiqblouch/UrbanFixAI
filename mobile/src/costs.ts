import type { components } from "@urbanfix/api-client";

type S = components["schemas"];

export type Kind = "material" | "labour" | "transport" | "other";
export const KINDS: Kind[] = ["material", "labour", "transport", "other"];

/** One line being typed. Numbers stay text until saved, so half-typed values like "2." are not rejected. */
export interface Line {
  kind: Kind;
  priceItemId: string;
  description: string;
  quantity: string;
  unitRate: string;
}

export const emptyLine = (kind: Kind = "material"): Line => ({ kind, priceItemId: "", description: "", quantity: "1", unitRate: "" });

export const fromServer = (l: S["CostLineDto"]): Line => ({
  kind: (KINDS.includes(l.kind as Kind) ? l.kind : "other") as Kind, priceItemId: l.priceItemId ?? "", description: l.description, quantity: String(l.quantity), unitRate: String(l.unitRate),
});

const round2 = (n: number) => Math.round(n * 100) / 100;
const num = (s: string) => {
  const n = Number(s.replace(",", "."));
  return Number.isFinite(n) ? n : 0;
};

/** Price-list items are priced at the rate in force now (the server does the same); other lines at the typed rate. */
export const rateOf = (l: Line, items: S["PriceItemDto"][]): number =>
  l.priceItemId ? items.find((i) => i.id === l.priceItemId)?.currentRate?.rate ?? 0 : num(l.unitRate);

export const amountOf = (l: Line, items: S["PriceItemDto"][]) => round2(num(l.quantity) * rateOf(l, items));
export const totalOf = (lines: Line[], items: S["PriceItemDto"][]) => round2(lines.reduce((n, l) => n + amountOf(l, items), 0));

/** What is still wrong with a line, as a code; undefined when it can be saved. */
export function problemWith(l: Line): "quantity" | "item" | "description" | "rate" | undefined {
  if (!(num(l.quantity) > 0)) return "quantity";
  if (l.kind === "material" || l.priceItemId) return l.priceItemId ? undefined : "item";
  if (l.description.trim() === "") return "description";
  if (l.unitRate.trim() === "" || num(l.unitRate) < 0) return "rate";
  return undefined;
}

export const toInput = (l: Line): S["CostLineInput"] => ({
  kind: l.kind, priceItemId: l.priceItemId || null, description: l.description.trim() || null, quantity: num(l.quantity), unit: null, unitRate: l.priceItemId ? null : num(l.unitRate),
});
