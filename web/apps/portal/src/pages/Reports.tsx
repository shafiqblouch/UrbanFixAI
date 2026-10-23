import { Card, EmptyState, KpiTile, SelectField, Table, TextField } from "@urbanfix/ui";
import { useState } from "react";
import { Link } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, PageHeader } from "../components";
import { Overruns } from "./JobExtras";
import { SummarySettings } from "./SummarySettings";
import { canAssign } from "../roles";
import { useSession } from "../session";
import { useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";

const TABS = ["costs", "repeated", "overruns", "summary"] as const;
type Tab = (typeof TABS)[number];

/** Where the money went, and where the same repair keeps coming back (JC-10, WP-18). */
export function ReportsPage() {
  const t = useT();
  const { role } = useSession();
  const tabs = TABS.filter((x) => x !== "overruns" || canAssign(role));
  const [tab, setTab] = useState<Tab>("costs");
  return (
    <>
      <PageHeader title={t("menu.reports")} />
      <div className="tabs" role="tablist" aria-label={t("menu.reports")}>
        {tabs.map((x) => (
          <button key={x} role="tab" aria-selected={tab === x} className={tab === x ? "tab tab--active" : "tab"} onClick={() => setTab(x)}>{t(`reports.${x}` as Key)}</button>
        ))}
      </div>
      {tab === "costs" && <Costs />}
      {tab === "repeated" && <Repeated />}
      {tab === "overruns" && <Overruns />}
      {tab === "summary" && <SummarySettings />}
    </>
  );
}

/** A date picked in the field, as the start (or end) of that day in ISO form. */
const startOf = (d: string) => (d ? new Date(`${d}T00:00:00`).toISOString() : undefined);
const endOf = (d: string) => (d ? new Date(`${d}T23:59:59`).toISOString() : undefined);

function Costs() {
  const t = useT();
  const f = useFormat();
  const [groupBy, setGroupBy] = useState<"job" | "category" | "street">("category");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const report = useLoad(() => ok(api.GET("/reports/costs", { params: { query: { groupBy, from: startOf(from), to: endOf(to) } } })), [groupBy, from, to]);
  return (
    <div className="stack">
      <div className="queue__fields">
        <SelectField label={t("reports.groupBy")} value={groupBy} onChange={(e) => setGroupBy(e.target.value as typeof groupBy)}
          options={(["category", "street", "job"] as const).map((g) => ({ value: g, label: t(`reports.by.${g}` as Key) }))} />
        <TextField type="date" label={t("from")} value={from} onChange={(e) => setFrom(e.target.value)} />
        <TextField type="date" label={t("to")} value={to} onChange={(e) => setTo(e.target.value)} />
      </div>
      <Async state={report}>
        {(r) => (
          <>
            <section className="kpis" aria-label={t("reports.costs")}>
              <KpiTile tone="indigo" label={t("reports.jobs")} value={f.number(r.jobs)} />
              <KpiTile tone="teal" label={t("reports.estimated")} value={f.money(r.estimated)} />
              <KpiTile tone="coral" label={t("reports.actual")} value={f.money(r.actual)} />
            </section>
            {r.rows.length === 0 ? <EmptyState title={t("reports.noCosts")} /> : (
              <Table
                caption={t("reports.costs")}
                rows={r.rows}
                rowKey={(x) => x.key}
                columns={[
                  { key: "name", header: t(`reports.by.${groupBy}` as Key), render: (x) => (groupBy === "job" ? <Link to={`/jobs/${x.key}`}>{x.name}</Link> : x.name) },
                  { key: "jobs", header: t("reports.jobs"), numeric: true, render: (x) => f.number(x.jobs) },
                  { key: "est", header: t("reports.estimated"), numeric: true, render: (x) => f.money(x.estimated) },
                  { key: "act", header: t("reports.actual"), numeric: true, render: (x) => f.money(x.actual) },
                  { key: "var", header: t("reports.variance"), numeric: true, render: (x) => (x.variance > 0 ? <strong className="over">{f.money(x.variance)}</strong> : f.money(x.variance)) },
                ]}
              />
            )}
          </>
        )}
      </Async>
    </div>
  );
}

function Repeated() {
  const t = useT();
  const f = useFormat();
  const [by, setBy] = useState<"house" | "street">("house");
  const [months, setMonths] = useState("");
  const [minCount, setMinCount] = useState("");
  const list = useLoad(() => ok(api.GET("/reports/repeated-repairs", {
    params: { query: { by, months: months ? Number(months) : undefined, minCount: minCount ? Number(minCount) : undefined } },
  })), [by, months, minCount]);
  return (
    <div className="stack">
      <p className="muted">{t("reports.repeatedHint")}</p>
      <div className="queue__fields">
        <SelectField label={t("reports.repeatedBy")} value={by} onChange={(e) => setBy(e.target.value as typeof by)}
          options={(["house", "street"] as const).map((g) => ({ value: g, label: t(`reports.place.${g}` as Key) }))} />
        <TextField type="number" min="1" max="60" label={t("reports.months")} hint={t("reports.useDefault")} value={months} onChange={(e) => setMonths(e.target.value)} />
        <TextField type="number" min="2" max="100" label={t("reports.minRepairs")} hint={t("reports.useDefault")} value={minCount} onChange={(e) => setMinCount(e.target.value)} />
      </div>
      <Async state={list}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("reports.noRepeats")} /> : (
          <ul className="queue">
            {(rows as S["RepeatedRepair"][]).map((r) => (
              <li key={`${r.placeKey}-${r.category ?? ""}`}>
                <Card title={r.place}>
                  <p>{t("reports.repeatedSummary", { count: r.repairs, category: r.category ?? t("reports.anyCategory"), total: f.money(r.totalCost) })}</p>
                  <ul className="timeline">
                    {r.jobs.map((j) => (
                      <li key={j.jobId}>
                        <span><Link to={`/jobs/${j.jobId}`}>{j.number}</Link> · {j.reference} <small className="muted">{f.date(j.createdAt)}</small></span>
                        <span>{f.money(j.actual)}</span>
                      </li>
                    ))}
                  </ul>
                </Card>
              </li>
            ))}
          </ul>
        )}
      </Async>
    </div>
  );
}
