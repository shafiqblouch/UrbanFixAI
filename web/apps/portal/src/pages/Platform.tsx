import { Alert, Button, Card, EmptyState, ProgressBar, SelectField, Sheet, Table, TextField } from "@urbanfix/ui";
import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, ErrorNote, PageHeader } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { useSession } from "../session";

const TABS = ["organizations", "quality", "usage", "support"] as const;
type Tab = (typeof TABS)[number];

/** Running the societies on the platform: onboarding, switches, AI quality and cost, and support access (PA-01 to PA-05). */
export function PlatformPage() {
  const t = useT();
  const [tab, setTab] = useState<Tab>("organizations");
  return (
    <>
      <PageHeader title={t("menu.platform")} />
      <div className="tabs" role="tablist" aria-label={t("menu.platform")}>
        {TABS.map((x) => (
          <button key={x} role="tab" aria-selected={tab === x} className={tab === x ? "tab tab--active" : "tab"} onClick={() => setTab(x)}>{t(`platform.${x}` as Key)}</button>
        ))}
      </div>
      {tab === "organizations" && <Organizations />}
      {tab === "quality" && <Quality />}
      {tab === "usage" && <Usage />}
      {tab === "support" && <SupportSessions />}
    </>
  );
}

// Societies --------------------------------------------------------------------------------------------------

function Organizations() {
  const t = useT();
  const f = useFormat();
  const list = useLoad(() => ok(api.GET("/admin/organizations/overview")), []);
  const [open, setOpen] = useState<S["PlatformOrganizationDto"]>();
  return (
    <div className="stack">
      <NewOrganization onCreated={list.reload} />
      <Async state={list}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("platform.noOrganizations")} /> : (
          <Table
            caption={t("platform.organizations")}
            rows={rows}
            rowKey={(o) => o.id}
            columns={[
              { key: "name", header: t("name"), render: (o) => <strong>{o.name}</strong> },
              { key: "slug", header: t("platform.slug"), render: (o) => o.slug },
              { key: "members", header: t("platform.members"), numeric: true, render: (o) => f.number(o.members) },
              { key: "houses", header: t("directory.houses"), numeric: true, render: (o) => f.number(o.houses) },
              { key: "complaints", header: t("menu.complaints"), numeric: true, render: (o) => f.number(o.complaints) },
              { key: "last", header: t("platform.lastComplaint"), render: (o) => f.date(o.lastComplaintAt) },
              { key: "open", header: t("action"), render: (o) => <Button variant="secondary" onClick={() => setOpen(o)}>{t("platform.manage")}</Button> },
            ]}
          />
        )}
      </Async>
      <OrganizationSheet org={open} onClose={() => setOpen(undefined)} />
    </div>
  );
}

function NewOrganization({ onCreated }: { onCreated: () => void }) {
  const t = useT();
  const action = useAction();
  const blank = { name: "", slug: "", managerName: "", managerEmail: "", managerPassword: "", displayName: "", template: "society", timezone: "Asia/Karachi", locale: "en" };
  const [form, setForm] = useState(blank);
  const [done, setDone] = useState<string>();
  const set = (k: keyof typeof blank, v: string) => setForm((f) => ({ ...f, [k]: v }));
  const submit = (e: FormEvent) => {
    e.preventDefault();
    setDone(undefined);
    void action.run(async () => {
      const created = await ok(api.POST("/admin/organizations", {
        body: {
          name: form.name, slug: form.slug, managerEmail: form.managerEmail, managerName: form.managerName, managerPassword: form.managerPassword,
          displayName: form.displayName || null, locationTemplate: form.template, timezone: form.timezone, locale: form.locale,
        },
      }));
      setDone(created.name);
      setForm(blank);
      onCreated();
    });
  };
  return (
    <Card title={t("platform.newOrganization")}>
      <form className="stack" onSubmit={submit}>
        <p className="muted">{t("platform.newHint")}</p>
        <div className="queue__fields">
          <TextField label={t("name")} value={form.name} required onChange={(e) => set("name", e.target.value)} />
          <TextField label={t("platform.slug")} hint={t("platform.slugHint")} value={form.slug} required onChange={(e) => set("slug", e.target.value.toLowerCase())} />
          <TextField label={t("config.displayName")} value={form.displayName} onChange={(e) => set("displayName", e.target.value)} />
          <SelectField label={t("platform.structure")} value={form.template} onChange={(e) => set("template", e.target.value)}
            options={[{ value: "society", label: t("config.template.society") }, { value: "building", label: t("config.template.building") }]} />
          <TextField label={t("config.timezone")} value={form.timezone} required onChange={(e) => set("timezone", e.target.value)} />
          <SelectField label={t("config.defaultLanguage")} value={form.locale} onChange={(e) => set("locale", e.target.value)} options={[{ value: "en", label: "English" }, { value: "ur", label: "اردو" }]} />
          <TextField label={t("platform.managerName")} value={form.managerName} required onChange={(e) => set("managerName", e.target.value)} />
          <TextField type="email" label={t("platform.managerEmail")} value={form.managerEmail} required onChange={(e) => set("managerEmail", e.target.value)} />
          <TextField type="password" label={t("platform.managerPassword")} value={form.managerPassword} required minLength={10} onChange={(e) => set("managerPassword", e.target.value)} />
        </div>
        <ErrorNote error={action.error} />
        {done && <Alert tone="success">{t("platform.created", { name: done })}</Alert>}
        <div className="row"><Button type="submit" loading={action.busy}>{t("platform.create")}</Button></div>
      </form>
    </Card>
  );
}

function OrganizationSheet({ org, onClose }: { org: S["PlatformOrganizationDto"] | undefined; onClose: () => void }) {
  const t = useT();
  return (
    <Sheet open={org !== undefined} title={org?.name ?? ""} onClose={onClose}>
      {org && (
        <div className="stack">
          <Onboarding id={org.id} />
          <Features id={org.id} />
          <AiCap id={org.id} />
          <StartSupport org={org} />
          <p className="muted">{t("platform.orgNote")}</p>
        </div>
      )}
    </Sheet>
  );
}

function Onboarding({ id }: { id: string }) {
  const t = useT();
  const state = useLoad(() => ok(api.GET("/admin/organizations/{id}/onboarding", { params: { path: { id } } })), [id]);
  return (
    <Card title={t("platform.onboarding")}>
      <Async state={state}>
        {(o) => {
          const steps = o.steps as S["OnboardingStep"][];
          return (
            <div className="stack">
              <ProgressBar label={o.readyForUse ? t("platform.ready") : t("platform.notReady")} value={steps.filter((s) => s.done).length / steps.length} />
              <ul className="checklist">
                {steps.map((s) => (
                  <li key={s.key}><span aria-hidden="true">{s.done ? "☑" : "☐"}</span> {t(`platform.step.${s.key}` as Key)} <span className="muted">({s.count})</span><span className="uf-sr-only"> {s.done ? t("job.done") : t("job.notDone")}</span></li>
                ))}
              </ul>
            </div>
          );
        }}
      </Async>
    </Card>
  );
}

function Features({ id }: { id: string }) {
  const t = useT();
  const list = useLoad(() => ok(api.GET("/admin/organizations/{id}/features", { params: { path: { id } } })), [id]);
  const action = useAction();
  const flip = (feature: string, enabled: boolean) => action.run(async () => { await ok(api.PUT("/admin/organizations/{id}/features/{feature}", { params: { path: { id, feature } }, body: { enabled } })); list.reload(); });
  return (
    <Card title={t("platform.features")}>
      <div className="stack">
        <p className="muted">{t("platform.featuresHint")}</p>
        <ErrorNote error={action.error} />
        <Async state={list}>
          {(rows) => (
            <ul className="checklist">
              {rows.map((r) => (
                <li key={r.feature}>
                  <label className="row">
                    <input type="checkbox" checked={r.enabled} disabled={action.busy} onChange={(e) => void flip(r.feature, e.target.checked)} />
                    {t(`platform.feature.${r.feature}` as Key)} {r.enabled !== r.default && <span className="muted">({t("platform.changed")})</span>}
                  </label>
                </li>
              ))}
            </ul>
          )}
        </Async>
      </div>
    </Card>
  );
}

function AiCap({ id }: { id: string }) {
  const t = useT();
  const action = useAction();
  const [cap, setCap] = useState("");
  const [saved, setSaved] = useState(false);
  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSaved(false);
    void action.run(async () => { await ok(api.PUT("/admin/organizations/{id}/ai-cap", { params: { path: { id } }, body: { monthlyTokenCap: Number(cap) } })); setSaved(true); });
  };
  return (
    <Card title={t("platform.aiCap")}>
      <form className="stack" onSubmit={submit}>
        <TextField type="number" min="0" label={t("policy.aiMonthlyTokenCap")} hint={t("policy.aiMonthlyTokenCapHint")} value={cap} onChange={(e) => setCap(e.target.value)} required />
        <ErrorNote error={action.error} />
        {saved && <Alert tone="success">{t("saved")}</Alert>}
        <div className="row"><Button type="submit" loading={action.busy} disabled={cap === ""}>{t("save")}</Button></div>
      </form>
    </Card>
  );
}

function StartSupport({ org }: { org: S["PlatformOrganizationDto"] }) {
  const t = useT();
  const action = useAction();
  const navigate = useNavigate();
  const { enterSupport } = useSession();
  const [reason, setReason] = useState("");
  const [minutes, setMinutes] = useState("30");
  const submit = (e: FormEvent) => {
    e.preventDefault();
    void action.run(async () => {
      const opened = await ok(api.POST("/admin/organizations/{id}/support-sessions", { params: { path: { id: org.id } }, body: { reason, minutes: Number(minutes) } }));
      await enterSupport(opened.token, opened.sessionId);
      navigate("/");
    });
  };
  return (
    <Card title={t("platform.support")}>
      <form className="stack" onSubmit={submit}>
        <Alert tone="warning">{t("platform.supportWarning")}</Alert>
        <TextField label={t("platform.supportReason")} hint={t("platform.supportReasonHint")} value={reason} minLength={10} maxLength={500} required onChange={(e) => setReason(e.target.value)} />
        <TextField type="number" min="1" max="120" label={t("platform.supportMinutes")} value={minutes} onChange={(e) => setMinutes(e.target.value)} />
        <ErrorNote error={action.error} />
        <div className="row"><Button type="submit" variant="danger" loading={action.busy} disabled={reason.trim().length < 10}>{t("platform.supportStart")}</Button></div>
      </form>
    </Card>
  );
}

// AI quality ---------------------------------------------------------------------------------------------------

function Quality() {
  const t = useT();
  const f = useFormat();
  const rows = useLoad(() => ok(api.GET("/admin/ai/quality", { params: { query: {} } })), []);
  return (
    <Async state={rows}>
      {(list) => list.length === 0 ? <EmptyState title={t("platform.noQuality")} /> : (
        <Table
          caption={t("platform.quality")}
          rows={list as S["AiQualityRow"][]}
          rowKey={(r) => `${r.organizationId}-${r.category}-${r.language ?? ""}`}
          columns={[
            { key: "o", header: t("platform.organization"), render: (r) => r.organization },
            { key: "c", header: t("category"), render: (r) => r.category },
            { key: "l", header: t("summary.language"), render: (r) => r.language ?? "—" },
            { key: "t", header: t("config.samples"), numeric: true, render: (r) => f.number(r.total) },
            { key: "a", header: t("platform.acceptRate"), numeric: true, render: (r) => `${f.number(Math.round(r.acceptRate * 100))}%` },
            { key: "x", header: t("platform.overrideRate"), numeric: true, render: (r) => (r.overrideRate > 0.3 ? <strong className="over">{f.number(Math.round(r.overrideRate * 100))}%</strong> : `${f.number(Math.round(r.overrideRate * 100))}%`) },
          ]}
        />
      )}
    </Async>
  );
}

// AI usage -----------------------------------------------------------------------------------------------------

function Usage() {
  const t = useT();
  const f = useFormat();
  const usage = useLoad(() => ok(api.GET("/admin/ai/usage", { params: { query: {} } })), []);
  return (
    <Async state={usage}>
      {(u) => (
        <div className="stack">
          <p className="muted">{t("platform.usageMonth", { month: f.date(u.monthStart) })}</p>
          <Table
            caption={t("platform.usage")}
            rows={u.organizations as S["AiUsageRow"][]}
            rowKey={(r) => r.organizationId}
            columns={[
              { key: "o", header: t("platform.organization"), render: (r) => r.organization },
              { key: "c", header: t("platform.calls"), numeric: true, render: (r) => f.number(r.calls) },
              { key: "tok", header: t("platform.tokens"), numeric: true, render: (r) => f.number(r.tokensThisMonth) },
              { key: "cap", header: t("platform.cap"), numeric: true, render: (r) => f.number(r.monthlyCap) },
              { key: "used", header: t("platform.used"), numeric: true, render: (r) => (r.overCap ? <strong className="over">{f.number(Math.round(r.usedFraction * 100))}%</strong> : `${f.number(Math.round(r.usedFraction * 100))}%`) },
              { key: "cost", header: t("platform.cost"), numeric: true, render: (r) => (r.costUsd == null ? "—" : `$${r.costUsd.toFixed(4)}`) },
              { key: "per", header: t("platform.perComplaint"), numeric: true, render: (r) => (r.costPerComplaintUsd == null ? "—" : `$${r.costPerComplaintUsd.toFixed(4)}`) },
            ]}
          />
        </div>
      )}
    </Async>
  );
}

// Support sessions -------------------------------------------------------------------------------------------

function SupportSessions() {
  const t = useT();
  const f = useFormat();
  const list = useLoad(() => ok(api.GET("/admin/support-sessions")), []);
  const action = useAction();
  const end = (id: string) => action.run(async () => { await api.POST("/admin/support-sessions/{id}/end", { params: { path: { id } } }); list.reload(); });
  return (
    <div className="stack">
      <p className="muted">{t("platform.supportLogHint")}</p>
      <ErrorNote error={action.error} />
      <Async state={list}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("platform.noSupport")} /> : (
          <Table
            caption={t("platform.support")}
            rows={rows as S["SupportSessionRow"][]}
            rowKey={(r) => r.id}
            columns={[
              { key: "o", header: t("platform.organization"), render: (r) => r.organization },
              { key: "a", header: t("platform.who"), render: (r) => r.admin },
              { key: "r", header: t("reason"), render: (r) => r.reason },
              { key: "s", header: t("when"), render: (r) => f.dateTime(r.startedAt) },
              { key: "st", header: t("status"), render: (r) => (r.active ? t("platform.supportActive", { until: f.dateTime(r.expiresAt) }) : t("platform.supportEnded")) },
              { key: "x", header: t("action"), render: (r) => (r.active ? <Button variant="ghost" onClick={() => void end(r.id)} loading={action.busy}>{t("platform.supportEnd")}</Button> : null) },
            ]}
          />
        )}
      </Async>
    </div>
  );
}
