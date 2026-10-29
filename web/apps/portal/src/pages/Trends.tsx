import { Button, Table } from "@urbanfix/ui";
import { useEffect, useMemo, useRef, useState, type PointerEvent } from "react";
import { api, ok, type S } from "../api";
import { Async } from "../components";
import { useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";

const M = { top: 12, right: 16, bottom: 28, left: 36 };

/** Round a maximum up to a tidy axis ceiling (4, 8, 20, 50...), so the gridlines fall on whole numbers. */
function niceMax(max: number): number {
  if (max <= 4) return 4;
  const pow = 10 ** Math.floor(Math.log10(max));
  for (const step of [1, 2, 2.5, 5, 10]) if (step * pow >= max) return step * pow;
  return 10 * pow;
}

/** The size of an element, kept up to date, so the chart is drawn at real pixels and fills the space its card gives it. */
function useBox<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  const [box, setBox] = useState({ w: 0, h: 0 });
  useEffect(() => {
    const el = ref.current;
    if (!el || typeof ResizeObserver === "undefined") return;
    const read = () => setBox({ w: Math.round(el.clientWidth), h: Math.round(el.clientHeight) });
    read();
    const ro = new ResizeObserver(read);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return [ref, box] as const;
}

/** Complaints reported and resolved per week or month, optionally for one category or sector (DB-02). */
export function Trends() {
  const t = useT();
  const [granularity, setGranularity] = useState<"week" | "month">("week");
  const [by, setBy] = useState<"none" | "category" | "sector">("none");
  const [pick, setPick] = useState("all");
  const periods = 12;
  const report = useLoad(() => ok(api.GET("/dashboard/trends", { params: { query: { granularity, by, periods } } })), [granularity, by]);

  return (
    <section className="uf-card dash-fill">
      <div className="trends__head">
        <div>
          <h2 className="trends__title">{t("trends.title")}</h2>
          <p className="trends__sub">{t("dash.trendsSub")}</p>
        </div>
        <div className="trends__ctl">
          <select className="dash-select" aria-label={t("trends.per")} value={granularity} onChange={(e) => setGranularity(e.target.value as typeof granularity)}>
            <option value="week">{t("dash.perWeek")}</option>
            <option value="month">{t("dash.perMonth")}</option>
          </select>
          <select className="dash-select" aria-label={t("trends.splitBy")} value={by} onChange={(e) => { setBy(e.target.value as typeof by); setPick("all"); }}>
            {(["none", "category", "sector"] as const).map((x) => <option key={x} value={x}>{t("trends.splitBy")}: {t(`trends.by.${x}` as Key)}</option>)}
          </select>
          {by !== "none" && report.data && (
            <select className="dash-select" aria-label={t("trends.show")} value={pick} onChange={(e) => setPick(e.target.value)}>
              {report.data.series.map((s) => <option key={s.key} value={s.key}>{s.key === "all" ? t("trends.everything") : s.name}</option>)}
            </select>
          )}
        </div>
      </div>
      <Async state={report}>
        {(r) => <TrendChart report={r} serie={r.series.find((s) => s.key === pick) ?? r.series[0]} />}
      </Async>
    </section>
  );
}

function TrendChart({ report, serie }: { report: S["TrendReport"]; serie: S["TrendSeries"] }) {
  const t = useT();
  const f = useFormat();
  const [hover, setHover] = useState<number>();
  const [asTable, setAsTable] = useState(false);
  const [plot, box] = useBox<HTMLDivElement>();

  const created = serie.created as number[];
  const resolved = serie.resolved as number[];
  const periods = report.periods as string[];
  const n = periods.length;
  const W = box.w || 640;
  const H = Math.max(box.h, 0) || 240;
  const top = niceMax(Math.max(1, ...created, ...resolved));
  const x = (i: number) => M.left + (n === 1 ? (W - M.left - M.right) / 2 : (i * (W - M.left - M.right)) / (n - 1));
  const y = (v: number) => M.top + (1 - v / top) * (H - M.top - M.bottom);
  // Smooth curves with flat tangents at each point: no overshoot below zero, as in a chart with light tension.
  const curve = (values: number[]) => values.map((v, i) => {
    if (i === 0) return `M${x(0).toFixed(1)},${y(v).toFixed(1)}`;
    const x0 = x(i - 1), y0 = y(values[i - 1]), x1 = x(i), y1 = y(v), mx = (x0 + x1) / 2;
    return `C${mx.toFixed(1)},${y0.toFixed(1)} ${mx.toFixed(1)},${y1.toFixed(1)} ${x1.toFixed(1)},${y1.toFixed(1)}`;
  }).join(" ");
  const area = `${curve(created)} L${x(n - 1).toFixed(1)},${y(0).toFixed(1)} L${x(0).toFixed(1)},${y(0).toFixed(1)} Z`;
  const label = (iso: string) => new Intl.DateTimeFormat(f.locale === "ur" ? "ur-PK-u-nu-latn" : "en-GB", report.granularity === "month" ? { month: "short", year: "2-digit" } : { day: "numeric", month: "short" }).format(new Date(`${iso}T00:00:00`));
  const ticks = useMemo(() => [0, top / 4, top / 2, (top * 3) / 4, top], [top]);
  const every = Math.ceil(n / Math.max(3, Math.floor(W / 90)));

  function onMove(e: PointerEvent<SVGSVGElement>) {
    const b = e.currentTarget.getBoundingClientRect();
    const px = ((e.clientX - b.left) / b.width) * W;
    setHover(Math.min(n - 1, Math.max(0, Math.round(((px - M.left) / (W - M.left - M.right)) * (n - 1)))));
  }

  const summary = t("trends.summary", { created: f.number(created.reduce((a, b) => a + b, 0)), resolved: f.number(resolved.reduce((a, b) => a + b, 0)), periods: n });

  return (
    <figure className="trend" aria-label={t("trends.title")}>
      <figcaption className="trend__legendrow">
        <span className="trend__legend">
          <span className="trend__key trend__key--1" /> {t("trends.reported")}
          <span className="trend__key trend__key--2" /> {t("trends.resolved")}
        </span>
        <Button variant="ghost" onClick={() => setAsTable((v) => !v)}>{asTable ? t("trends.showChart") : t("trends.showTable")}</Button>
      </figcaption>
      {asTable ? (
        <div className="trend__table">
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
        </div>
      ) : (
        <div className="trend__plot" dir="ltr" ref={plot}>
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
            <path className="trend__area" d={area} />
            <path className="trend__line trend__line--2" d={curve(resolved)} />
            <path className="trend__line trend__line--1" d={curve(created)} />
            {n <= 16 && periods.map((p, i) => (
              <g key={p}>
                <circle className="trend__dot trend__dot--2" cx={x(i)} cy={y(resolved[i])} r={3} />
                <circle className="trend__dot trend__dot--1" cx={x(i)} cy={y(created[i])} r={3} />
              </g>
            ))}
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
      <p className="muted trend__summary">{summary}</p>
    </figure>
  );
}
