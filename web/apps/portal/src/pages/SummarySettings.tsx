import { Alert, Button, Card, EmptyState, SelectField, Table, TextField } from "@urbanfix/ui";
import { useState, type FormEvent } from "react";
import { api, download, ok, type S } from "../api";
import { Async, ErrorNote, usePeople } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { canConfigure, canSeeCosts } from "../roles";
import { useSession } from "../session";

const startOf = (d: string) => (d ? new Date(`${d}T00:00:00`).toISOString() : undefined);
const endOf = (d: string) => (d ? new Date(`${d}T23:59:59`).toISOString() : undefined);

/** Download the summary for any dates, read the monthly ones, and (for managers) say who gets them and when (WP-20, DB-04, DB-05). */
export function SummarySettings() {
  const { role } = useSession();
  return (
    <div className="stack">
      <DownloadNow />
      <PastSummaries manage={canConfigure(role)} />
      {canConfigure(role) && <Schedule />}
    </div>
  );
}

function DownloadNow() {
  const t = useT();
  const { role } = useSession();
  const action = useAction();
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [lang, setLang] = useState("");
  const get = (path: string, query: Record<string, string | undefined>) => action.run(() => download(path, query));
  const range = { from: startOf(from), to: endOf(to), lang: lang || undefined };
  return (
    <Card title={t("summary.downloadNow")}>
      <div className="stack">
        <p className="muted">{t("summary.downloadHint")}</p>
        <div className="queue__fields">
          <TextField type="date" label={t("from")} value={from} onChange={(e) => setFrom(e.target.value)} />
          <TextField type="date" label={t("to")} value={to} onChange={(e) => setTo(e.target.value)} />
          <SelectField label={t("summary.language")} placeholder={t("summary.mine")} value={lang} onChange={(e) => setLang(e.target.value)} options={[{ value: "en", label: "English" }, { value: "ur", label: "اردو" }]} />
        </div>
        <ErrorNote error={action.error} />
        <div className="row wrap">
          <Button onClick={() => void get("/exports/summary", { format: "pdf", ...range })} loading={action.busy}>{t("summary.pdf")}</Button>
          <Button variant="secondary" onClick={() => void get("/exports/summary", { format: "csv", ...range })} loading={action.busy}>{t("summary.csv")}</Button>
          {canSeeCosts(role) && <Button variant="secondary" onClick={() => void get("/exports/costs", { groupBy: "category", from: range.from, to: range.to })} loading={action.busy}>{t("summary.costsCsv")}</Button>}
        </div>
      </div>
    </Card>
  );
}

function PastSummaries({ manage }: { manage: boolean }) {
  const t = useT();
  const f = useFormat();
  const list = useLoad(() => ok(api.GET("/reports/summaries")), []);
  const action = useAction();
  const [period, setPeriod] = useState(() => { const d = new Date(); d.setMonth(d.getMonth() - 1); return d.toISOString().slice(0, 7); });
  const make = () => action.run(async () => { await ok(api.POST("/reports/summaries", { body: { period } })); list.reload(); });
  return (
    <Card title={t("summary.monthly")}>
      <div className="stack">
        <ErrorNote error={action.error} />
        <Async state={list}>
          {(rows) => rows.length === 0 ? <EmptyState title={t("summary.none")} /> : (
            <Table
              caption={t("summary.monthly")}
              rows={rows}
              rowKey={(r) => r.id}
              columns={[
                { key: "period", header: t("summary.month"), render: (r) => <strong>{r.period}</strong> },
                { key: "kind", header: t("summary.made"), render: (r) => `${r.scheduled ? t("summary.scheduled") : t("summary.byHand")} · ${f.dateTime(r.createdAt)}` },
                { key: "costs", header: t("summary.costsIncluded"), render: (r) => (r.includesCosts ? t("yes") : t("no")) },
                { key: "to", header: t("summary.sentTo"), numeric: true, render: (r) => f.number(r.recipients) },
                { key: "get", header: t("action"), render: (r) => <Button variant="ghost" onClick={() => void action.run(() => download(`/reports/summaries/${r.id}/download`))}>{t("summary.download")}</Button> },
              ]}
            />
          )}
        </Async>
        {manage && (
          <form className="row wrap" onSubmit={(e) => { e.preventDefault(); void make(); }}>
            <TextField type="month" label={t("summary.makeFor")} value={period} onChange={(e) => setPeriod(e.target.value)} />
            <Button type="submit" variant="secondary" loading={action.busy} disabled={period === ""}>{t("summary.make")}</Button>
          </form>
        )}
      </div>
    </Card>
  );
}

const READERS = ["Manager", "Committee", "Supervisor", "Office"];

function Schedule() {
  const t = useT();
  const schedule = useLoad(() => ok(api.GET("/reports/summary-schedule")), []);
  return (
    <Card title={t("summary.schedule")}>
      <Async state={schedule}>{(s) => <ScheduleForm initial={s} onSaved={schedule.reload} />}</Async>
    </Card>
  );
}

function ScheduleForm({ initial, onSaved }: { initial: S["ScheduleDto"]; onSaved: () => void }) {
  const t = useT();
  const { list } = usePeople();
  const action = useAction();
  const [form, setForm] = useState({ enabled: initial.enabled, day: String(initial.dayOfMonth), language: initial.language ?? "", recipients: initial.recipientUserIds as string[] });
  const [saved, setSaved] = useState(false);
  const people = list.filter((p) => READERS.includes(p.role));
  const toggle = (id: string) => setForm((f) => ({ ...f, recipients: f.recipients.includes(id) ? f.recipients.filter((x) => x !== id) : [...f.recipients, id] }));
  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSaved(false);
    void action.run(async () => {
      await ok(api.PUT("/reports/summary-schedule", { body: { enabled: form.enabled, dayOfMonth: Number(form.day), language: form.language || null, recipientUserIds: form.recipients } }));
      setSaved(true);
      onSaved();
    });
  };
  return (
    <form className="stack" onSubmit={submit}>
      <p className="muted">{t("summary.scheduleHint")}</p>
      <label className="row"><input type="checkbox" checked={form.enabled} onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> {t("summary.enabled")}</label>
      <div className="queue__fields">
        <TextField type="number" min="1" max="28" label={t("summary.day")} hint={t("summary.dayHint")} value={form.day} onChange={(e) => setForm({ ...form, day: e.target.value })} />
        <SelectField label={t("summary.language")} placeholder={t("summary.societyLanguage")} value={form.language} onChange={(e) => setForm({ ...form, language: e.target.value })} options={[{ value: "en", label: "English" }, { value: "ur", label: "اردو" }]} />
      </div>
      <fieldset>
        <legend>{t("summary.recipients")}</legend>
        {people.length === 0 ? <p className="muted">{t("summary.noPeople")}</p> : (
          <ul className="checklist">
            {people.map((p) => (
              <li key={p.userId}><label className="row"><input type="checkbox" checked={form.recipients.includes(p.userId)} onChange={() => toggle(p.userId)} /> {p.displayName} <span className="muted">({t(`role.${p.role}` as Key)})</span></label></li>
            ))}
          </ul>
        )}
        <p className="muted">{t("summary.costsNote")}</p>
      </fieldset>
      <ErrorNote error={action.error} />
      {saved && <Alert tone="success">{t("saved")}</Alert>}
      <div className="row"><Button type="submit" loading={action.busy}>{t("save")}</Button></div>
    </form>
  );
}
