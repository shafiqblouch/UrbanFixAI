import { Alert, Button, SelectField, Table, TextAreaField, TextField } from "@urbanfix/ui";
import { useState } from "react";
import type { S } from "./api";
import { ErrorNote } from "./components";
import { useAction } from "./hooks";
import { useFormat, useT, type Key } from "./i18n";

export type Draft = { kind: string; priceItemId: string; description: string; quantity: string; unit: string; unitRate: string };
export const emptyLine = (kind = "material"): Draft => ({ kind, priceItemId: "", description: "", quantity: "1", unit: "", unitRate: "" });

export const toDraft = (l: S["CostLineDto"]): Draft => ({
  kind: l.kind, priceItemId: l.priceItemId ?? "", description: l.description, quantity: String(l.quantity), unit: l.unit, unitRate: String(l.unitRate),
});

const round2 = (n: number) => Math.round(n * 100) / 100;

/** The rate the line will be priced at: the price list's for materials, the typed one otherwise. */
export function lineRate(d: Draft, items: S["PriceItemDto"][]): number {
  if (d.priceItemId) return items.find((i) => i.id === d.priceItemId)?.currentRate?.rate ?? 0;
  return Number(d.unitRate) || 0;
}

export const lineAmount = (d: Draft, items: S["PriceItemDto"][]) => round2((Number(d.quantity) || 0) * lineRate(d, items));
export const total = (lines: Draft[], items: S["PriceItemDto"][]) => round2(lines.reduce((n, l) => n + lineAmount(l, items), 0));

export function toInput(d: Draft): S["CostLineInput"] {
  return {
    kind: d.kind, priceItemId: d.priceItemId || null, description: d.description || null, quantity: Number(d.quantity),
    unit: d.unit || null, unitRate: d.priceItemId ? null : Number(d.unitRate),
  };
}

const valid = (d: Draft) => Number(d.quantity) > 0 && (d.priceItemId !== "" || (d.kind !== "material" && d.description.trim() !== "" && d.unitRate !== "" && Number(d.unitRate) >= 0));

/**
 * Lines of materials (from the price list), labour, transport and other costs with a running total (WP-08).
 * Used for the estimate before work and for the actual costs at completion.
 */
export function CostEditor({ heading, initial, items, readOnly, note, onSave, saveLabel }: {
  heading: string; initial: Draft[]; items: S["PriceItemDto"][]; readOnly?: boolean; note?: boolean;
  onSave: (lines: Draft[], note: string) => Promise<unknown>; saveLabel: string;
}) {
  const t = useT();
  const f = useFormat();
  const action = useAction();
  const [lines, setLines] = useState<Draft[]>(initial.length ? initial : [emptyLine()]);
  const [noteText, setNoteText] = useState("");
  const [saved, setSaved] = useState(false);
  const set = (i: number, patch: Partial<Draft>) => { setSaved(false); setLines((ls) => ls.map((l, n) => (n === i ? { ...l, ...patch } : l))); };

  if (readOnly) {
    return (
      <Table caption={heading} rows={lines.map((l, i) => ({ ...l, i }))} rowKey={(l) => String(l.i)}
        columns={[
          { key: "k", header: t("cost.kind"), render: (l) => t(`cost.${l.kind}` as Key) },
          { key: "d", header: t("description"), render: (l) => items.find((x) => x.id === l.priceItemId)?.name ?? l.description },
          { key: "q", header: t("cost.quantity"), render: (l) => `${f.number(Number(l.quantity))} ${l.unit}`, numeric: true },
          { key: "r", header: t("cost.rate"), render: (l) => f.money(lineRate(l, items)), numeric: true },
          { key: "a", header: t("cost.amount"), render: (l) => f.money(lineAmount(l, items)), numeric: true },
        ]} />
    );
  }

  return (
    <form className="stack" onSubmit={(e) => { e.preventDefault(); void action.run(async () => { await onSave(lines, noteText); setSaved(true); }); }} aria-label={heading}>
      {lines.map((l, i) => (
        <fieldset key={i} className="cost-line">
          <legend>{t("cost.line", { n: i + 1 })}</legend>
          <SelectField label={t("cost.kind")} value={l.kind} onChange={(e) => set(i, { kind: e.target.value, priceItemId: e.target.value === "material" ? l.priceItemId : "" })}
            options={["material", "labour", "transport", "other"].map((k) => ({ value: k, label: t(`cost.${k}` as Key) }))} />
          {l.kind === "material" || l.priceItemId ? (
            <SelectField label={t("cost.item")} placeholder={t("choose")} value={l.priceItemId}
              onChange={(e) => set(i, { priceItemId: e.target.value, unit: items.find((x) => x.id === e.target.value)?.unit ?? "" })}
              options={items.filter((x) => x.isActive && x.currentRate).map((x) => ({ value: x.id, label: `${x.name} (${f.money(x.currentRate!.rate)} / ${x.unit})` }))} />
          ) : (
            <>
              <TextField label={t("description")} value={l.description} onChange={(e) => set(i, { description: e.target.value })} />
              <TextField label={t("cost.rate")} type="number" min="0" step="0.01" inputMode="decimal" value={l.unitRate} onChange={(e) => set(i, { unitRate: e.target.value })} />
            </>
          )}
          <TextField label={t("cost.quantity")} type="number" min="0" step="0.001" inputMode="decimal" value={l.quantity} onChange={(e) => set(i, { quantity: e.target.value })} />
          <p className="cost-line__amount" aria-live="polite">{t("cost.amount")}: <strong>{f.money(lineAmount(l, items))}</strong></p>
          {lines.length > 1 && <Button type="button" variant="ghost" onClick={() => setLines((ls) => ls.filter((_, n) => n !== i))}>{t("cost.remove")}</Button>}
        </fieldset>
      ))}
      <div className="row wrap">
        <Button type="button" variant="secondary" onClick={() => setLines((ls) => [...ls, emptyLine("labour")])}>{t("cost.add")}</Button>
        <strong className="cost-total" aria-live="polite">{t("cost.total")}: {f.money(total(lines, items))}</strong>
      </div>
      {note && <TextAreaField label={t("cost.note")} value={noteText} onChange={(e) => setNoteText(e.target.value)} rows={2} />}
      <ErrorNote error={action.error} />
      {saved && <Alert tone="success">{t("saved")}</Alert>}
      <Button type="submit" loading={action.busy} disabled={!lines.every(valid)}>{saveLabel}</Button>
    </form>
  );
}
