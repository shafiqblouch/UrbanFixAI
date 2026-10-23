import type { ReactNode } from "react";

export type KpiTone = "indigo" | "coral" | "teal" | "amber";

export interface KpiTileProps {
  label: string;
  value: ReactNode;
  hint?: string;
  tone?: KpiTone;
}

export function KpiTile({ label, value, hint, tone = "indigo" }: KpiTileProps) {
  return (
    <div className={`uf-kpi uf-kpi--${tone}`} role="group" aria-label={label}>
      <span className="uf-kpi__label">{label}</span>
      <span className="uf-kpi__value">{value}</span>
      {hint && <span className="uf-kpi__hint">{hint}</span>}
    </div>
  );
}
