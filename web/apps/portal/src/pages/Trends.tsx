import { Button, SelectField, Table } from "@urbanfix/ui";
import { useMemo, useState, type PointerEvent } from "react";
import { api, ok, type S } from "../api";
import { Async } from "../components";
import { useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";

const W = 640;
const H = 240;
const M = { top: 12, right: 16, bottom: 28, left: 40 };

/** Round a maximum up to a tidy axis ceiling (4, 8, 20, 50...), so the gridlines fall on whole numbers. */
function niceMax(max: number): number {
  if (max <= 4) return 4;
  const pow = 10 ** Math.floor(Math.log10(max));
  for (const step of [1, 2, 2.5, 5, 10]) if (step * pow >= max) return step * pow;
  return 10 * pow;
}

/** Complaints reported and resolved per week or month, optionally for one category or sector (DB-02). */
export function Trends() {
  const t = useT();
  const [granularity, setGranularity] = useState<"week" | "month">("week");
  const [by, setBy] = useState<"none" | "category" | "sector">("none");
  const [pick, setPick] = useState("all");
  const periods = granularity === "week" ? 12 : 12;
  const report = useLoad(() => ok(api.GET("/dashboard/trends", { params: { query: { granularity, by, periods } } })), [granularity, by]);

  return (
    <div className="stack">
      <div className="queue__fields">
        <SelectField label={t("trends.per")} value={granularity} onChange={(e) => setGranularity(e.target.value as typeof granularity)}
          options={[{ value: "week", label: t("trends.week") }, { value: "month", label: t("trends.month") }]} />
        <SelectField label={t("trends.splitBy")} value={by} onChange={(e) => { setBy(e.target.value as typeof by); setPick("all"); }}
          options={(["none", "category", "sector"] as const).map((x) => ({ value: x, label: t(`trends.by.${x}` as Key) }))} />
        {by !== "none" && report.data && (
          <SelectField label={t("trends.show")} value={pick} onChange={(e) => setPick(e.target.value)}
            options={report.data.series.map((s) => ({ value: s.key, label: s.key === "all" ? t("trends.everything") : s.name }))} />
        )}
      </div>
      <Async state={report}>
        {(r) => <TrendChart report={r} serie={r.series.find((s) => s.key === pick) ?? r.series[0]} />}
      </Async>
    </div>
  );
}

function TrendChart({ report, serie }: { report: S["TrendReport"]; serie: S["TrendSeries"] }) {
  const t = useT();
  const f = useFormat();
  const [hover, setHover] = useState<number>();
  const [asTable, setAsTable] = useState(false);

  const created = serie.created as number[];
  const resolved = serie.resolved as number[];
  const periods = report.periods as string[];
  const n = periods.length;
  const top = niceMax(Math.max(1, ...created, ...resolved));
  const x = (i: number) => M.left + (n === 1 ? (W - M.left - M.right) / 2 : (i * (W - M.left - M.right)) / (n - 1));
  const y = (v: number) => M.top + (1 - v / top) * (H - M.top - M.bottom);
  const line = (values: number[]) => values.map((v, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(" ");
  const label = (iso: string) => new Intl.DateTimeFormat(f.locale === "ur" ? "ur-PK-u-nu-latn" : "en-GB", report.granularity === "month" ? { month: "short", year: "2-digit" } : { day: "numeric", month: "short" }).format(new Date(`${iso}T00:00:00`));
  const ticks = useMemo(() => [0, top / 4, top / 2, (top * 3) / 4, top], [top]);
  const every = Math.ceil(n / 6);

  function onMove(e: PointerEvent<SVGSVGElement>) {
    const box = e.currentTarget.getBoundingClientRect();
    const px = ((e.clientX - box.left) / box.width) * W;
    setHover(Math.min(n - 1, Math.max(0, Math.round(((px - M.left) / (W - M.left - M.right)) * (n - 1)))));
  }

  const summary = t("trends.summary", { created: f.number(created.reduce((a, b) => a + b, 0)), resolved: f.number(resolved.reduce((a, b) => a + b, 0)), periods: n });

  return (
    <figure className="trend" aria-label={t("trends.title")}>
      <figcaption className="row wrap">
        <strong>{t("trends.title")}</strong>
        <span className="trend__legend">
          <span className="trend__key trend__key--1" /> {t("trends.reported")}
          <span className="trend__key trend__key--2" /> {t("trends.resolved")}
        </span>
        <Button variant="ghost" onClick={() => setAsTable((v) => !v)}>{asTable ? t("trends.showChart") : t("trends.showTable")}</Button>
      </figcaption>
      {asTable ? (
        <Table
          caption={t("trends.title")}
          rows={periods.map((p, i) => ({ p, created: created[i], resolved: resolved[i] }))}
          rowKey={(r) => r.p}
          columns={[
            { key: "p", header: t("trends.period"), render: (r) => label(r.p) },
            { key: "c", header: t("trends.reported"), numeric: true, render: (r) => f.number(r.created) },
            { key: "r", header: t("trends.resolved"), numeric: true, render: (r) => f.number(r.resolved) },
          ]}
        />
      ) : (
        <div className="trend__plot" dir="ltr">
          <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={summary} onPointerMove={onMove} onPointerLeave={() => setHover(undefined)}>
            {ticks.map((v) => (
              <g key={v}>
                <line className="trend__grid" x1={M.left} x2={W - M.right} y1={y(v)} y2={y(v)} />
                <text className="trend__axis" x={M.left - 6} y={y(v) + 4} textAnchor="end">{f.number(Math.round(v * 10) / 10)}</text>
              </g>
            ))}
            {periods.map((p, i) => (i % every === 0 || i === n - 1) && (
              <text key={p} className="trend__axis" x={x(i)} y={H - 8} textAnchor="middle">{label(p)}</text>
            ))}
            <path className="trend__line trend__line--2" d={line(resolved)} />
            <path className="trend__line trend__line--1" d={line(created)} />
            {hover !== undefined && (
              <g>
                <line className="trend__cross" x1={x(hover)} x2={x(hover)} y1={M.top} y2={H - M.bottom} />
                <circle className="trend__dot trend__dot--2" cx={x(hover)} cy={y(resolved[hover])} r={5} />
                <circle className="trend__dot trend__dot--1" cx={x(hover)} cy={y(created[hover])} r={5} />
              </g>
            )}
          </svg>
          {hover !== undefined && (
            <div className="trend__tip" style={{ insetInlineStart: `${(x(hover) / W) * 100}%` }} role="status">
              <strong>{label(periods[hover])}</strong>
              <span><span className="trend__key trend__key--1" /> {t("trends.reported")}: {f.number(created[hover])}</span>
              <span><span className="trend__key trend__key--2" /> {t("trends.resolved")}: {f.number(resolved[hover])}</span>
            </div>
          )}
        </div>
      )}
      <p className="muted">{summary}</p>
    </figure>
  );
}
