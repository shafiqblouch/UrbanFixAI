import { Card, EmptyState, Table } from "@urbanfix/ui";
import { useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, ComplaintStatusChip } from "../components";
import { useLoad, type Loaded } from "../hooks";
import { Icon, type IconName } from "../icons";
import { useFormat, useT, type Key } from "../i18n";
import { useLookups } from "../lookups";
import { menusFor } from "../roles";
import { useSession } from "../session";
import { Trends } from "./Trends";

const OPEN = ["New", "OnHold", "UnderReview"];
const ACTIVE = ["Assigned", "InProgress"];

/** Widgets that need the complaint summary; it is loaded once for all of them. */
const NEEDS_SUMMARY = ["kpis", "workload", "overdue", "satisfaction"];

/** The dashboard is split into tabs so it stays compact; each widget the server names lands in one of them. */
const TABS = {
  overview: ["kpis", "recent", "overdue", "satisfaction"],
  trends: ["trends", "workload"],
  finance: ["spend", "lowStock", "charges"],
  workload: ["workload", "delivery"],
} as const;
type Tab = keyof typeof TABS;
const TAB_ICON: Record<Tab, IconName> = { overview: "dashboard", trends: "reports", finance: "charges", workload: "directory" };

/**
 * The dashboard each role lands on (DB-08): the server says which widgets, in which order, and every widget reads its own figures,
 * so what a person sees follows what they may see (WP-11, DB-01 to DB-07).
 */
export function DashboardPage() {
  const layout = useLoad(() => ok(api.GET("/dashboard/layout")), []);
  return <Async state={layout}>{(l) => <Widgets widgets={l.widgets as string[]} />}</Async>;
}

function Widgets({ widgets }: { widgets: string[] }) {
  const t = useT();
  const f = useFormat();
  const needs = widgets.some((w) => NEEDS_SUMMARY.includes(w));
  const summary = useLoad(() => (needs ? ok(api.GET("/dashboard/summary")) : Promise.resolve(undefined)), [needs]);
  const spend = useLoad(() => (widgets.some((w) => ["spend", "lowStock"].includes(w)) ? ok(api.GET("/dashboard/spend")) : Promise.resolve(undefined)), [widgets.join()]);
  const [chosen, setChosen] = useState<Tab>("overview");
  const has = (id: string) => widgets.includes(id);
  const tabs = (Object.keys(TABS) as Tab[]).filter((tab) => TABS[tab].some(has));
  if (tabs.length === 0) return <EmptyState title={t("dashboard.noWidgets")} />;
  const active = tabs.includes(chosen) ? chosen : tabs[0];
  const d = summary.data;

  return (
    <div className="dash">
      {d && (has("kpis") || has("overdue")) && <Pulse d={d} />}
      {d && has("kpis") && <KpiRow d={d} />}
      <div className="dash-tabrow">
        <div className="tabs" role="tablist">
          {tabs.map((tab) => (
            <button key={tab} type="button" role="tab" aria-selected={tab === active} className={`tab${tab === active ? " tab--active" : ""}`} onClick={() => setChosen(tab)}>
              <span className="tab__inner"><Icon name={TAB_ICON[tab]} size={15} />{t(`dash.tab.${tab}` as Key)}</span>
            </button>
          ))}
        </div>
        <div className="row">
          {d && <span className="muted dash-avg">{t("dash.avgRes")} <strong>{d.averageResolutionHours == null ? "—" : `${f.hours(d.averageResolutionHours)} h`}</strong></span>}
          <button type="button" className="icon-btn dash-refresh" title={t("dash.refresh")} aria-label={t("dash.refresh")} onClick={() => { summary.reload(); spend.reload(); }}><Icon name="refresh" size={17} /></button>
        </div>
      </div>
      {!d && needs && <Async state={summary}>{() => null}</Async>}
      <div className="dash-body">
        {active === "overview" && <Overview has={has} d={d} />}
        {active === "trends" && <TrendsTab has={has} d={d} />}
        {active === "finance" && <FinanceTab has={has} spend={spend} />}
        {active === "workload" && <WorkloadTab has={has} d={d} />}
      </div>
    </div>
  );
}

/** The one-line "what needs attention now" strip. */
function Pulse({ d }: { d: S["Summary"] }) {
  const t = useT();
  const f = useFormat();
  const n = d.overdueJobs.length;
  const avg = d.averageResolutionHours == null ? null : t("dashboard.avgResolution", { hours: f.hours(d.averageResolutionHours) });
  return (
    <div className={`dash-pulse${n > 0 ? " dash-pulse--alert" : ""}`} role="status">
      <span className="dash-pulse__tag"><Icon name="pulse" size={13} />{t("dash.pulse")}</span>
      <span className="dash-pulse__text">
        {n > 0 ? <><strong>{t("dash.pulseOverdue", { count: f.number(n), job: d.overdueJobs[0].number })}</strong> {t("dash.pulseNeeds")}</> : t("dash.pulseOk")}
        {avg && <> {avg}.</>}
      </span>
      {n > 0 && <Link className="dash-pulse__link" to={`/jobs/${d.overdueJobs[0].jobId}`}>{t("dash.takeAction")} <Icon name="arrow" size={12} /></Link>}
    </div>
  );
}

function KpiRow({ d }: { d: S["Summary"] }) {
  const t = useT();
  const f = useFormat();
  const count = (keys: string[]) => d.byStatus.filter((r) => keys.includes(r.key)).reduce((n, r) => n + r.count, 0);
  // Reported this week against last week. Not every role may read trends, so a refusal just hides the figure.
  const weeks = useLoad(async () => {
    try { return await ok(api.GET("/dashboard/trends", { params: { query: { granularity: "week", by: "none", periods: 2 } } })); } catch { return undefined; }
  }, []);
  const created = (weeks.data?.series?.[0]?.created ?? []) as number[];
  const change = created.length >= 2 && created[created.length - 2] > 0 ? Math.round(((created[created.length - 1] - created[created.length - 2]) / created[created.length - 2]) * 100) : undefined;
  const teams = d.teamWorkload.filter((w) => w.inProgress > 0 || w.open > 0).map((w) => w.team);
  const worstHours = d.overdueJobs.reduce((m, j) => Math.max(m, j.hoursOverdue), 0);
  const cards: { tone: string; icon: IconName; label: string; value: number; hint?: ReactNode; aside?: ReactNode; to: string }[] = [
    { tone: "brand", icon: "list", label: t("dashboard.total"), value: d.total, to: "/complaints",
      hint: change === undefined ? undefined : <><strong className={change >= 0 ? "dash-up" : "dash-down"}>{change >= 0 ? "+" : ""}{f.number(change)}%</strong> {t("dash.vsLastWeek")}</>, aside: t("dash.totalWord") },
    { tone: "warn", icon: "clock", label: t("dashboard.open"), value: count(OPEN), hint: t("dashboard.openHint"), to: "/complaints" },
    { tone: "info", icon: "jobs", label: t("dashboard.active"), value: count(ACTIVE), hint: teams.length > 0 ? t("dash.assignedTo", { teams: teams.slice(0, 2).join(", ") }) : undefined, to: "/jobs" },
    { tone: "bad", icon: "alert", label: t("dashboard.overdue"), value: d.overdueJobs.length, hint: d.overdueJobs.length > 0 ? t("dash.pastTarget", { hours: f.hours(worstHours) }) : t("dashboard.overdueHint"), to: "/jobs" },
  ];
  return (
    <section className="dash-kpis" aria-label={t("dashboard.summary")}>
      {cards.map((c) => (
        <Link key={c.label} to={c.to} className={`dash-kpi dash-kpi--${c.tone}`}>
          <span className="dash-kpi__text">
            <span className="dash-kpi__label">{c.label}</span>
            <strong className="dash-kpi__value">{f.number(c.value)}</strong>
            {(c.hint || c.aside) && <span className="dash-kpi__hint">{c.hint}{c.aside && <span className="dash-kpi__aside">{c.aside}</span>}</span>}
          </span>
          <span className="dash-kpi__icon"><Icon name={c.icon} size={18} /></span>
        </Link>
      ))}
    </section>
  );
}

function Overview({ has, d }: { has: (id: string) => boolean; d: S["Summary"] | undefined }) {
  const left = (has("kpis") && d) || has("recent");
  const role = useSession().role;
  const quick = menusFor(role).filter((m) => m === "notices" || m === "reports");
  const right = (has("overdue") && d) || (has("satisfaction") && d && d.ratingCount > 0) || quick.length > 0;
  if (!left && !right) return null;
  return (
    <div className="dash-grid">
      <div className="stack">
        {has("kpis") && d && <Stages d={d} />}
        {has("recent") && <Recent total={d?.total ?? 0} />}
      </div>
      <div className="stack">
        {has("overdue") && d && <OverdueAlert d={d} />}
        {has("satisfaction") && d && <Rating d={d} />}
        {quick.length > 0 && <QuickActions menus={quick} />}
      </div>
    </div>
  );
}

function Stages({ d }: { d: S["Summary"] }) {
  const t = useT();
  const f = useFormat();
  const count = (keys: string[]) => d.byStatus.filter((r) => keys.includes(r.key)).reduce((n, r) => n + r.count, 0);
  const pct = (n: number) => (d.total === 0 ? 0 : n / d.total);
  const done = count(["Resolved", "Closed"]);
  const stages = [
    { key: "new", label: t("dash.stageNew"), n: count(OPEN), tone: "info" },
    { key: "active", label: t("dash.stageActive"), n: count(ACTIVE), tone: "warn" },
    { key: "done", label: t("dash.stageDone"), n: done, tone: "good" },
  ];
  return (
    <Card className="dash-grow" title={<span className="dash-title"><Icon name="reports" size={14} />{t("dash.stages")}</span>} actions={<strong className="dash-resolved">{t("dash.resolvedPct", { pct: f.number(Math.round(pct(done) * 100)) })}</strong>}>
      <div className="stack dash-stages">
        {stages.map((s) => (
          <div key={s.key} className="dash-stage">
            <div className="dash-stage__head"><span>{s.label} ({f.number(s.n)})</span><strong>{f.number(Math.round(pct(s.n) * 100))}%</strong></div>
            <div className="dash-stage__track" role="progressbar" aria-label={s.label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(pct(s.n) * 100)}>
              <span className={`dash-stage__fill dash-stage__fill--${s.tone}`} style={{ inlineSize: `${pct(s.n) * 100}%` }} />
            </div>
          </div>
        ))}
        <p className="muted">{t("dashboard.reopened", { count: d.reopenedComplaints })}</p>
      </div>
    </Card>
  );
}

function Recent({ total }: { total: number }) {
  const t = useT();
  const f = useFormat();
  const recent = useLoad(async () => ok(api.GET("/complaints", { params: { query: { limit: 6 } } })), []);
  return (
    <Card className="dash-grow" title={<span className="dash-title"><Icon name="history" size={14} />{t("dashboard.recent")}</span>} actions={<Link to="/complaints" className="dash-viewall">{t("dash.viewAllN", { count: f.number(total) })}</Link>}>
      <Async state={recent}>
        {(rows) => rows.length === 0 ? <p className="muted">{t("empty")}</p> : (
          <div className="dash-scroll">
            <table className="dash-table">
              <thead><tr><th>{t("reference")}</th><th>{t("status")}</th><th className="dash-table__end">{t("action")}</th></tr></thead>
              <tbody>
                {rows.map((c) => {
                  const waiting = OPEN.includes(c.status);
                  const working = ACTIVE.includes(c.status);
                  const label = waiting ? t("dash.assignComplaint") : working ? t("dash.openComplaint") : t("dash.viewComplaint");
                  return (
                    <tr key={c.id}>
                      <td><Link to={`/complaints/${c.id}`} className="dash-table__id">{c.reference}</Link><span className="dash-table__sub clip">{c.description}</span></td>
                      <td><ComplaintStatusChip status={c.status} /></td>
                      <td className="dash-table__end"><Link to={`/complaints/${c.id}`} className={`dash-act${waiting ? " dash-act--brand" : working ? " dash-act--warn" : ""}`} title={label} aria-label={`${label}: ${c.reference}`}>
                        <Icon name={waiting ? "directory" : working ? "jobs" : "eye"} size={15} />
                      </Link></td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </Async>
    </Card>
  );
}

function OverdueAlert({ d }: { d: S["Summary"] }) {
  const t = useT();
  const f = useFormat();
  const team = (id: string | null) => d.teamWorkload.find((w) => w.teamId === id)?.team;
  if (d.overdueJobs.length === 0) return null;
  const shown = d.overdueJobs.slice(0, 3);
  const top = shown[0];
  return (
    <section className="dash-alert" aria-label={t("dash.overdueAttention")}>
      <div className="dash-alert__head">
        <span className="dash-alert__tag"><Icon name="alert" size={13} />{t("dash.overdueAttention")}</span>
        <strong>{t("dash.over", { hours: f.hours(top.hoursOverdue) })}</strong>
      </div>
      <ul className="dash-alert__list">
        {shown.map((j) => (
          <li key={j.jobId}>
            <div className="dash-alert__row"><Link to={`/jobs/${j.jobId}`}><strong>{j.number}</strong></Link><span className="dash-alert__pri">{t(`priority.${j.priority}` as Key)}</span></div>
            {team(j.teamId) && <span className="muted">{t("dash.assignedCrew", { team: team(j.teamId) ?? "" })}</span>}
            <span className="muted">{t("reference")}: {j.reference}{shown.length > 1 ? ` · ${t("dash.over", { hours: f.hours(j.hoursOverdue) })}` : ""}</span>
          </li>
        ))}
      </ul>
      <Link to={`/jobs/${top.jobId}`} className="dash-alert__btn"><Icon name="bolt" size={14} />{t("dash.openJob")}</Link>
      {d.overdueJobs.length > shown.length && <Link to="/jobs" className="dash-alert__more">{t("dash.moreJobs")} ({f.number(d.overdueJobs.length)})</Link>}
    </section>
  );
}

function Rating({ d }: { d: S["Summary"] }) {
  const t = useT();
  const f = useFormat();
  if (d.ratingCount === 0 || d.averageRating == null) return null;
  const filled = Math.round(d.averageRating);
  const done = d.byStatus.filter((r) => ["Resolved", "Closed"].includes(r.key)).reduce((n, r) => n + r.count, 0);
  const feedbackRate = done === 0 ? 0 : Math.min(100, Math.round((d.ratingCount / done) * 100));
  return (
    <section className="dash-rating" aria-label={t("dash.citizenRating")}>
      <div>
        <span className="dash-kpi__label">{t("dash.citizenRating")}</span>
        <strong className="dash-rating__value">{f.number(d.averageRating, { maximumFractionDigits: 1 })} <small className="muted">{t("dash.outOf5")}</small></strong>
        <span className="dash-kpi__hint">{t("dash.basedOn", { count: d.ratingCount })}</span>
      </div>
      <div className="dash-rating__side">
        <span className="dash-stars" aria-hidden="true">{[1, 2, 3, 4, 5].map((i) => <span key={i} className={i <= filled ? "dash-star dash-star--on" : "dash-star"}><Icon name="star" size={15} /></span>)}</span>
        <small className="muted">{t("dash.feedbackRate", { pct: f.number(feedbackRate) })}</small>
      </div>
    </section>
  );
}

function QuickActions({ menus }: { menus: string[] }) {
  const t = useT();
  return (
    <Card title={t("dash.quick")}>
      <div className="dash-quick">
        {menus.includes("notices") && <Link to="/notices"><Icon name="notices" size={18} />{t("dash.sendNotice")}</Link>}
        {menus.includes("reports") && <Link to="/reports"><Icon name="reports" size={18} />{t("dash.openReports")}</Link>}
      </div>
    </Card>
  );
}

function TrendsTab({ has, d }: { has: (id: string) => boolean; d: S["Summary"] | undefined }) {
  const t = useT();
  const f = useFormat();
  const { locations } = useLookups();
  // The summary lists only sectors that have complaints; the sectors with none are added so the whole picture shows.
  const counted = (d?.bySector ?? []) as S["CountRow"][];
  const quiet = locations.filter((l) => l.kind.toLowerCase() === "sector" && l.isActive && !counted.some((r) => r.key === l.id || r.name === l.name))
    .map((l): S["CountRow"] => ({ key: l.id, name: l.name, count: 0 }));
  const bySector = [...counted, ...quiet];
  const max = Math.max(1, ...bySector.map((r) => r.count));
  return (
    <div className="dash-grid dash-grid--wide">
      {has("trends") ? <Trends /> : <span />}
      {has("workload") && d && (
        <Card className="dash-grow" title={t("dashboard.bySector")}>
          <div className="stack dash-stages dash-scroll">
            {bySector.map((r) => (
              <div key={r.key} className="dash-stage">
                <div className={`dash-stage__head${r.count > 0 ? " dash-stage__head--on" : ""}`}><span>{r.name}</span><strong>{t("dash.complaintsN", { count: f.number(r.count) })}</strong></div>
                <div className="dash-stage__track dash-stage__track--thick"><span className={`dash-stage__fill dash-stage__fill--${r.count > 0 ? "brand" : "none"}`} style={{ inlineSize: `${(r.count / max) * 100}%` }} /></div>
              </div>
            ))}
          </div>
        </Card>
      )}
    </div>
  );
}

function FinanceTab({ has, spend }: { has: (id: string) => boolean; spend: Loaded<S["SpendWidget"] | undefined> }) {
  const t = useT();
  const left: ReactNode[] = [];
  const right: ReactNode[] = [];
  if (has("spend")) left.push(<Async key="spend" state={spend}>{(s) => (s ? <Spend s={s} /> : null)}</Async>);
  if (has("charges")) left.push(<Async key="charges" state={spend}>{(s) => (s ? <ChargesWaiting s={s} /> : null)}</Async>);
  if (has("lowStock")) right.push(<Async key="low" state={spend}>{(s) => (s ? <LowStock s={s} /> : null)}</Async>);
  return (
    <div className="dash-grid">
      <div className="stack">{left}</div>
      <div className="stack">{right}</div>
    </div>
  );
}

function WorkloadTab({ has, d }: { has: (id: string) => boolean; d: S["Summary"] | undefined }) {
  return (
    <div className="dash-grid dash-grid--even">
      {has("workload") && d ? <TeamWorkload d={d} /> : <span />}
      {has("delivery") && <Delivery />}
    </div>
  );
}

function TeamWorkload({ d }: { d: S["Summary"] }) {
  const t = useT();
  const f = useFormat();
  return (
    <Card title={t("dashboard.workload")}>
      <Table caption={t("dashboard.workload")} rows={d.teamWorkload} rowKey={(r) => r.teamId ?? "none"}
        columns={[
          { key: "team", header: t("team"), render: (r) => <strong>{r.team}</strong> },
          { key: "open", header: t("dashboard.workloadOpen"), render: (r) => <span className="dash-pill">{f.number(r.open)}</span>, numeric: true },
          { key: "prog", header: t("dashboard.workloadProgress"), render: (r) => <span className="dash-pill dash-pill--warn">{f.number(r.inProgress)}</span>, numeric: true },
          { key: "pend", header: t("dashboard.workloadPending"), render: (r) => <span className="dash-pill dash-pill--dim">{f.number(r.pendingVerification)}</span>, numeric: true },
        ]} />
    </Card>
  );
}

function Stat({ label, value, hint, tone }: { label: string; value: string; hint?: string; tone?: "brand" | "warn" }) {
  return (
    <div className="dash-stat">
      <span className="dash-kpi__label">{label}</span>
      <strong className={`dash-stat__value${tone ? ` dash-stat__value--${tone}` : ""}`}>{value}</strong>
      {hint && <span className="dash-kpi__hint">{hint}</span>}
    </div>
  );
}

function Spend({ s }: { s: S["SpendWidget"] }) {
  const t = useT();
  const f = useFormat();
  return (
    <Card title={t("dashboard.spend")} actions={<Link to="/reports">{t("viewAll")}</Link>}>
      <div className="stack">
        <section className="dash-stats" aria-label={t("dashboard.spend")}>
          <Stat label={t("reports.jobs")} value={f.number(s.jobs)} />
          <Stat label={t("reports.estimated")} value={f.money(s.estimated)} />
          <Stat label={t("reports.actual")} value={f.money(s.actual)} tone="brand" />
        </section>
        {s.byCategory.length > 0 && <h3 className="dash-sub">{t("dash.costByCategory")}</h3>}
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
    <Card title={t("dash.stockTitle")} actions={items.length === 0 ? <span className="dash-ok">{t("dash.stockOk")}</span> : <Link to="/stock">{t("viewAll")}</Link>}>
      {items.length === 0 ? (
        <div className="dash-empty">
          <span className="dash-empty__icon"><Icon name="stock" size={22} /></span>
          <strong>{t("dashboard.noLowStock")}</strong>
          <span className="muted">{t("dash.stockDesc")}</span>
        </div>
      ) : (
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
    <section className="dash-stats dash-stats--one" aria-label={t("menu.charges")}>
      <Stat label={t("dashboard.chargesWaiting")} value={f.number(s.chargesWaiting ?? 0)} hint={t("charges.status.Pending")} tone="warn" />
    </section>
  );
}

function Delivery() {
  const t = useT();
  const f = useFormat();
  const stats = useLoad(() => ok(api.GET("/reports/delivery", { params: { query: {} } })), []);
  return (
    <Card title={t("dash.comms")}>
      <Async state={stats}>
        {(rows) => rows.length === 0 ? <p className="muted">{t("dashboard.noDelivery")}</p> : (
          <div className="stack">
            {rows.map((r) => {
              // Sent counts too: for the in-app inbox "sent" is as delivered as it gets, and a provider's receipt may never come.
              const delivered = r.sent + r.delivered + r.read;
              const pct = Math.round(r.failureRate * 100);
              return (
                <dl key={r.channel} className="dash-facts">
                  <div><dt>{t("channel")}</dt><dd>{r.channel}</dd></div>
                  <div><dt>{t("dash.totalSent")}</dt><dd className="dash-facts__brand">{f.number(r.total)}</dd></div>
                  <div><dt>{t("notices.delivered")}</dt><dd className="dash-facts__good">{t("dash.deliveredPct", { count: f.number(delivered), pct: f.number(r.total === 0 ? 0 : Math.round((delivered / r.total) * 100)) })}</dd></div>
                  <div><dt>{t("notices.failed")}</dt><dd>{f.number(r.failed)}</dd></div>
                  <div><dt>{t("dashboard.failureRate")}</dt><dd className={r.failureRate > 0.1 ? "dash-facts__bad" : "dash-facts__good"}>{f.number(pct)}%</dd></div>
                </dl>
              );
            })}
          </div>
        )}
      </Async>
    </Card>
  );
}
