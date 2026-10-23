import { Card, EmptyState, KpiTile, ProgressBar, Table } from "@urbanfix/ui";
import { Link } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, ComplaintStatusChip, PageHeader } from "../components";
import { useLoad, type Loaded } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { Overruns } from "./JobExtras";
import { Trends } from "./Trends";

const OPEN = ["New", "OnHold", "UnderReview"];
const ACTIVE = ["Assigned", "InProgress"];

/** Widgets that need the complaint summary; it is loaded once for all of them. */
const NEEDS_SUMMARY = ["kpis", "workload", "overdue", "satisfaction"];

/**
 * The dashboard each role lands on (DB-08): the server says which widgets, in which order, and every widget reads its own figures,
 * so what a person sees follows what they may see (WP-11, DB-01 to DB-07).
 */
export function DashboardPage() {
  const t = useT();
  const layout = useLoad(() => ok(api.GET("/dashboard/layout")), []);
  return (
    <>
      <PageHeader title={t("menu.dashboard")} />
      <Async state={layout}>{(l) => <Widgets widgets={l.widgets as string[]} />}</Async>
    </>
  );
}

function Widgets({ widgets }: { widgets: string[] }) {
  const needs = widgets.some((w) => NEEDS_SUMMARY.includes(w));
  const summary = useLoad(() => (needs ? ok(api.GET("/dashboard/summary")) : Promise.resolve(undefined)), [needs]);
  const spend = useLoad(() => (widgets.some((w) => ["spend", "lowStock"].includes(w)) ? ok(api.GET("/dashboard/spend")) : Promise.resolve(undefined)), [widgets.join()]);
  const t = useT();
  if (widgets.length === 0) return <EmptyState title={t("dashboard.noWidgets")} />;
  return (
    <div className="stack">
      {widgets.map((w) => <Widget key={w} id={w} summary={summary} spend={spend} />)}
    </div>
  );
}

function Widget({ id, summary, spend }: { id: string; summary: Loaded<S["Summary"] | undefined>; spend: Loaded<S["SpendWidget"] | undefined> }) {
  const t = useT();
  switch (id) {
    case "kpis": return <Async state={summary}>{(d) => (d ? <Kpis d={d} /> : null)}</Async>;
    case "recent": return <Recent />;
    case "workload": return <Async state={summary}>{(d) => (d ? <Workload d={d} /> : null)}</Async>;
    case "overdue": return <Async state={summary}>{(d) => (d ? <OverdueJobs d={d} /> : null)}</Async>;
    case "satisfaction": return <Async state={summary}>{(d) => (d ? <Satisfaction d={d} /> : null)}</Async>;
    case "trends": return <Card title={t("dashboard.trends")}><Trends /></Card>;
    case "spend": return <Async state={spend}>{(s) => (s ? <Spend s={s} /> : null)}</Async>;
    case "lowStock": return <Async state={spend}>{(s) => (s ? <LowStock s={s} /> : null)}</Async>;
    case "overruns": return <Card title={t("reports.overruns")}><Overruns /></Card>;
    case "delivery": return <Delivery />;
    case "charges": return <Async state={spend}>{(s) => (s ? <ChargesWaiting s={s} /> : null)}</Async>;
    default: return null;
  }
}

function Kpis({ d }: { d: S["Summary"] }) {
  const t = useT();
  const f = useFormat();
  const count = (keys: string[]) => d.byStatus.filter((r) => keys.includes(r.key)).reduce((n, r) => n + r.count, 0);
  const done = count(["Resolved", "Closed"]);
  return (
    <>
      <section className="kpis" aria-label={t("dashboard.summary")}>
        <KpiTile tone="indigo" label={t("dashboard.total")} value={f.number(d.total)} />
        <KpiTile tone="coral" label={t("dashboard.open")} value={f.number(count(OPEN))} hint={t("dashboard.openHint")} />
        <KpiTile tone="teal" label={t("dashboard.active")} value={f.number(count(ACTIVE))} />
        <KpiTile tone="amber" label={t("dashboard.overdue")} value={f.number(d.overdueJobs.length)} hint={t("dashboard.overdueHint")} />
      </section>
      <Card title={t("dashboard.progress")}>
        <div className="stack">
          <ProgressBar label={t("dashboard.resolutionRate")} value={d.total === 0 ? 0 : done / d.total} />
          {d.byStatus.filter((r) => r.count > 0).map((r) => (
            <ProgressBar key={r.key} label={t(`complaintStatus.${r.key}` as Key)} value={d.total === 0 ? 0 : r.count / d.total} valueText={f.number(r.count)} />
          ))}
          <p className="muted">
            {t("dashboard.avgResolution", { hours: d.averageResolutionHours == null ? "—" : f.hours(d.averageResolutionHours) })}
            {" · "}
            {t("dashboard.reopened", { count: d.reopenedComplaints })}
          </p>
        </div>
      </Card>
    </>
  );
}

function Recent() {
  const t = useT();
  const recent = useLoad(async () => ok(api.GET("/complaints", { params: { query: { limit: 6 } } })), []);
  return (
    <Card title={t("dashboard.recent")} actions={<Link to="/complaints">{t("viewAll")}</Link>}>
      <Async state={recent}>
        {(rows) => rows.length === 0 ? <p className="muted">{t("empty")}</p> : (
          <ul className="recent">
            {rows.map((c) => (
              <li key={c.id}>
                <Link to={`/complaints/${c.id}`}><strong>{c.reference}</strong></Link>
                <span className="clip">{c.description}</span>
                <ComplaintStatusChip status={c.status} />
              </li>
            ))}
          </ul>
        )}
      </Async>
    </Card>
  );
}

function Workload({ d }: { d: S["Summary"] }) {
  const t = useT();
  const f = useFormat();
  // The OpenAPI generator types a repeated list element as unknown[]; these are CountRow lists.
  const bySector = d.bySector as S["CountRow"][];
  return (
    <div className="grid-2">
      <Card title={t("dashboard.workload")}>
        <Table caption={t("dashboard.workload")} rows={d.teamWorkload} rowKey={(r) => r.teamId ?? "none"}
          columns={[
            { key: "team", header: t("team"), render: (r) => r.team },
            { key: "open", header: t("dashboard.workloadOpen"), render: (r) => f.number(r.open), numeric: true },
            { key: "prog", header: t("dashboard.workloadProgress"), render: (r) => f.number(r.inProgress), numeric: true },
            { key: "pend", header: t("dashboard.workloadPending"), render: (r) => f.number(r.pendingVerification), numeric: true },
          ]} />
      </Card>
      <Card title={t("dashboard.bySector")}>
        <Table caption={t("dashboard.bySector")} rows={bySector} rowKey={(r) => r.key}
          columns={[
            { key: "name", header: t("sector"), render: (r) => r.name },
            { key: "count", header: t("count"), render: (r) => f.number(r.count), numeric: true },
          ]} />
      </Card>
    </div>
  );
}

function OverdueJobs({ d }: { d: S["Summary"] }) {
  const t = useT();
  const f = useFormat();
  if (d.overdueJobs.length === 0) return null;
  return (
    <Card title={t("dashboard.overdueJobs")}>
      <Table caption={t("dashboard.overdueJobs")} rows={d.overdueJobs} rowKey={(r) => r.jobId}
        columns={[
          { key: "job", header: t("job"), render: (r) => <Link to={`/jobs/${r.jobId}`}>{r.number}</Link> },
          { key: "ref", header: t("reference"), render: (r) => r.reference },
          { key: "pri", header: t("priority"), render: (r) => t(`priority.${r.priority}` as Key) },
          { key: "hours", header: t("dashboard.hoursOver"), render: (r) => f.hours(r.hoursOverdue), numeric: true },
        ]} />
    </Card>
  );
}

function Satisfaction({ d }: { d: S["Summary"] }) {
  const t = useT();
  const f = useFormat();
  if (d.ratingCount === 0) return null;
  return (
    <section className="kpis" aria-label={t("dashboard.satisfaction")}>
      <KpiTile tone="teal" label={t("dashboard.rating")} value={d.averageRating == null ? "—" : t("feedback.stars", { rating: f.number(d.averageRating, { maximumFractionDigits: 1 }) })} hint={t("dashboard.ratings", { count: d.ratingCount })} />
      <KpiTile tone="indigo" label={t("dashboard.confirmedFixed")} value={f.number(d.confirmedFixedCount)} hint={t("dashboard.ofRated", { count: d.ratingCount })} />
    </section>
  );
}

function Spend({ s }: { s: S["SpendWidget"] }) {
  const t = useT();
  const f = useFormat();
  return (
    <Card title={t("dashboard.spend")} actions={<Link to="/reports">{t("viewAll")}</Link>}>
      <div className="stack">
        <section className="kpis" aria-label={t("dashboard.spend")}>
          <KpiTile tone="indigo" label={t("reports.jobs")} value={f.number(s.jobs)} hint={t("dashboard.thisMonth")} />
          <KpiTile tone="teal" label={t("reports.estimated")} value={f.money(s.estimated)} />
          <KpiTile tone="coral" label={t("reports.actual")} value={f.money(s.actual)} />
        </section>
        {s.byCategory.length > 0 && (
          <Table caption={t("dashboard.spend")} rows={s.byCategory} rowKey={(r) => r.key}
            columns={[
              { key: "n", header: t("category"), render: (r) => r.name },
              { key: "e", header: t("reports.estimated"), numeric: true, render: (r) => f.money(r.estimated) },
              { key: "a", header: t("reports.actual"), numeric: true, render: (r) => f.money(r.actual) },
            ]} />
        )}
      </div>
    </Card>
  );
}

function LowStock({ s }: { s: S["SpendWidget"] }) {
  const t = useT();
  const f = useFormat();
  const items = s.lowStock as S["LowStockItem"][];
  return (
    <Card title={t("dashboard.lowStock")} actions={<Link to="/stock">{t("viewAll")}</Link>}>
      {items.length === 0 ? <p className="muted">{t("dashboard.noLowStock")}</p> : (
        <Table caption={t("dashboard.lowStock")} rows={items} rowKey={(i) => i.itemId}
          columns={[
            { key: "i", header: t("stock.item"), render: (i) => i.item },
            { key: "h", header: t("stock.onHand"), numeric: true, render: (i) => <strong className="over">{f.number(i.onHand)} {i.unit}</strong> },
            { key: "t", header: t("stock.lowAt"), numeric: true, render: (i) => f.number(i.threshold) },
          ]} />
      )}
    </Card>
  );
}

function ChargesWaiting({ s }: { s: S["SpendWidget"] }) {
  const t = useT();
  const f = useFormat();
  return (
    <section className="kpis" aria-label={t("menu.charges")}>
      <KpiTile tone="amber" label={t("dashboard.chargesWaiting")} value={f.number(s.chargesWaiting ?? 0)} hint={t("charges.status.Pending")} />
    </section>
  );
}

function Delivery() {
  const t = useT();
  const f = useFormat();
  const stats = useLoad(() => ok(api.GET("/reports/delivery", { params: { query: {} } })), []);
  return (
    <Card title={t("dashboard.delivery")}>
      <Async state={stats}>
        {(rows) => rows.length === 0 ? <p className="muted">{t("dashboard.noDelivery")}</p> : (
          <Table caption={t("dashboard.delivery")} rows={rows} rowKey={(r) => r.channel}
            columns={[
              { key: "c", header: t("channel"), render: (r) => r.channel },
              { key: "t", header: t("dashboard.messages"), numeric: true, render: (r) => f.number(r.total) },
              // Sent counts too: for the in-app inbox "sent" is as delivered as it gets, and a provider's receipt may never come.
              { key: "d", header: t("notices.delivered"), numeric: true, render: (r) => f.number(r.sent + r.delivered + r.read) },
              { key: "f", header: t("notices.failed"), numeric: true, render: (r) => f.number(r.failed) },
              { key: "r", header: t("dashboard.failureRate"), numeric: true, render: (r) => (r.failureRate > 0.1 ? <strong className="over">{f.number(Math.round(r.failureRate * 100))}%</strong> : `${f.number(Math.round(r.failureRate * 100))}%`) },
            ]} />
        )}
      </Async>
    </Card>
  );
}
