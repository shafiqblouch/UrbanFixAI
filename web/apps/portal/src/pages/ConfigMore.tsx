import { Alert, Button, Card, EmptyState, SelectField, Sheet, Table, TextField } from "@urbanfix/ui";
import { useState, type FormEvent } from "react";
import { api, ok, type S } from "../api";
import { Async, ErrorNote } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { useLookups } from "../lookups";

// Society settings and policies (OR-01, OR-12, WP-12, WP-14) ---------------------------------------------------

const POLICY_NUMBERS: { key: keyof S["OrgPolicy"]; label: Key; hint?: Key; min?: number; step?: string }[] = [
  { key: "estimateApprovalLimit", label: "policy.estimateApprovalLimit", hint: "policy.estimateApprovalLimitHint", min: 0 },
  { key: "secondApproverAbove", label: "policy.secondApproverAbove", hint: "policy.secondApproverAboveHint", min: 0 },
  { key: "approvalExpiryHours", label: "policy.approvalExpiryHours", min: 1 },
  { key: "estimateWaitHours", label: "policy.estimateWaitHours", min: 1 },
  { key: "overrunPercent", label: "policy.overrunPercent", min: 0 },
  { key: "autoConfirmDays", label: "policy.autoConfirmDays", min: 1 },
  { key: "reminderDays", label: "policy.reminderDays", min: 1 },
  { key: "aiMonthlyTokenCap", label: "policy.aiMonthlyTokenCap", hint: "policy.aiMonthlyTokenCapHint", min: 0 },
  { key: "repeatedRepairCount", label: "policy.repeatedRepairCount", min: 2 },
  { key: "repeatedRepairMonths", label: "policy.repeatedRepairMonths", min: 1 },
  { key: "duplicateRadiusMetres", label: "policy.duplicateRadiusMetres", min: 0 },
  { key: "duplicateWindowDays", label: "policy.duplicateWindowDays", min: 1 },
  { key: "duplicateThresholdPercent", label: "policy.duplicateThresholdPercent", min: 0 },
];

export function Society() {
  const t = useT();
  const settings = useLoad(() => ok(api.GET("/organization/settings")), []);
  const policy = useLoad(() => ok(api.GET("/organization/policies")), []);
  return (
    <div className="stack">
      <Async state={settings}>{(s) => <SettingsForm initial={s} onSaved={settings.reload} />}</Async>
      <Card title={t("config.policies")}>
        <Async state={policy}>{(p) => <PolicyForm initial={p} onSaved={policy.reload} />}</Async>
      </Card>
    </div>
  );
}

function SettingsForm({ initial, onSaved }: { initial: S["SettingsDto"]; onSaved: () => void }) {
  const t = useT();
  const action = useAction();
  const [form, setForm] = useState(initial);
  const [saved, setSaved] = useState(false);
  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSaved(false);
    void action.run(async () => { await ok(api.PUT("/organization/settings", { body: form })); setSaved(true); onSaved(); });
  };
  return (
    <Card title={t("config.society")}>
      <form className="stack" onSubmit={submit}>
        <div className="queue__fields">
          <TextField label={t("config.displayName")} value={form.displayName ?? ""} onChange={(e) => setForm({ ...form, displayName: e.target.value || null })} />
          <TextField label={t("config.timezone")} value={form.timezone} required onChange={(e) => setForm({ ...form, timezone: e.target.value })} />
          <SelectField label={t("config.defaultLanguage")} value={form.locale} onChange={(e) => setForm({ ...form, locale: e.target.value })}
            options={[{ value: "en", label: "English" }, { value: "ur", label: "اردو" }]} />
          <TextField label={t("config.primaryColor")} hint={t("config.primaryColorHint")} value={form.primaryColor ?? ""} onChange={(e) => setForm({ ...form, primaryColor: e.target.value || null })} />
        </div>
        <p className="muted">{t("config.template", { template: t(`config.template.${form.locationTemplate}` as Key) })}</p>
        <ErrorNote error={action.error} />
        {saved && <Alert tone="success">{t("saved")}</Alert>}
        <div className="row"><Button type="submit" loading={action.busy}>{t("save")}</Button></div>
      </form>
    </Card>
  );
}

function PolicyForm({ initial, onSaved }: { initial: S["OrgPolicy"]; onSaved: () => void }) {
  const t = useT();
  const action = useAction();
  const [form, setForm] = useState(initial);
  const [saved, setSaved] = useState(false);
  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSaved(false);
    void action.run(async () => { await ok(api.PUT("/organization/policies", { body: form })); setSaved(true); onSaved(); });
  };
  return (
    <form className="stack" onSubmit={submit}>
      <div className="queue__fields">
        <SelectField label={t("policy.unknownSender")} value={form.unknownSender} onChange={(e) => setForm({ ...form, unknownSender: e.target.value })}
          options={["unverified_queue", "guest", "otp_registration"].map((v) => ({ value: v, label: t(`policy.unknownSender.${v}` as Key) }))} />
        {POLICY_NUMBERS.map((n) => (
          <TextField key={n.key} type="number" min={n.min} step="any" label={t(n.label)} hint={n.hint ? t(n.hint) : undefined}
            value={String(form[n.key] as number)} onChange={(e) => setForm({ ...form, [n.key]: Number(e.target.value) })} />
        ))}
      </div>
      <label className="row"><input type="checkbox" checked={form.closureNeedsResident} onChange={(e) => setForm({ ...form, closureNeedsResident: e.target.checked })} /> {t("policy.closureNeedsResident")}</label>
      <label className="row"><input type="checkbox" checked={form.autoConfirm} onChange={(e) => setForm({ ...form, autoConfirm: e.target.checked })} /> {t("policy.autoConfirm")}</label>
      <ErrorNote error={action.error} />
      {saved && <Alert tone="success">{t("saved")}</Alert>}
      <div className="row"><Button type="submit" loading={action.busy}>{t("save")}</Button></div>
    </form>
  );
}

// Places and landmarks (OR-02, OR-03) --------------------------------------------------------------------------

const LEVELS: Record<string, string[]> = { society: ["sector", "street", "block", "house"], building: ["building", "floor", "unit"] };

export function Places() {
  const t = useT();
  const { locations, reload } = useLookups();
  const settings = useLoad(() => ok(api.GET("/organization/settings")), []);
  const action = useAction();
  const [form, setForm] = useState({ parentId: "", kind: "", name: "", nameUr: "" });
  const levels = LEVELS[settings.data?.locationTemplate ?? "society"] ?? LEVELS.society;
  const kind = form.kind || levels[0];

  const add = (e: FormEvent) => {
    e.preventDefault();
    void action.run(async () => {
      await ok(api.POST("/locations", { body: { parentId: form.parentId || null, kind, name: form.name, nameUr: form.nameUr || null, latitude: null, longitude: null } }));
      setForm({ ...form, name: "", nameUr: "" });
      reload();
    });
  };
  const toggle = (l: S["LocationDto"]) => action.run(async () => { await ok(api.PUT("/locations/{id}", { params: { path: { id: l.id } }, body: { name: null, nameUr: null, latitude: null, longitude: null, isActive: !l.isActive } })); reload(); });

  return (
    <div className="stack">
      <Card title={t("config.places")}>
        <div className="stack">
          {locations.length === 0 ? <EmptyState title={t("config.noPlaces")} /> : (
            <Table
              caption={t("config.places")}
              rows={locations}
              rowKey={(l) => l.id}
              columns={[
                { key: "name", header: t("name"), render: (l) => <span style={{ paddingInlineStart: `${l.depth * 1.25}rem` }}>{l.name}{l.nameUr ? <span className="muted"> · {l.nameUr}</span> : null}</span> },
                { key: "kind", header: t("config.level"), render: (l) => t(`config.level.${l.kind}` as Key) },
                { key: "active", header: t("status"), render: (l) => <Button variant="ghost" onClick={() => void toggle(l)}>{l.isActive ? t("active") : t("inactive")}</Button> },
              ]}
            />
          )}
          <form className="stack" onSubmit={add}>
            <div className="queue__fields">
              <SelectField label={t("config.inside")} placeholder={t("config.top")} value={form.parentId} onChange={(e) => setForm({ ...form, parentId: e.target.value })}
                options={locations.filter((l) => l.isActive).map((l) => ({ value: l.id, label: "—".repeat(l.depth) + " " + l.name }))} />
              <SelectField label={t("config.level")} value={kind} onChange={(e) => setForm({ ...form, kind: e.target.value })} options={levels.map((k) => ({ value: k, label: t(`config.level.${k}` as Key) }))} />
              <TextField label={t("name")} value={form.name} required onChange={(e) => setForm({ ...form, name: e.target.value })} />
              <TextField label={t("nameUr")} dir="rtl" lang="ur" value={form.nameUr} onChange={(e) => setForm({ ...form, nameUr: e.target.value })} />
            </div>
            <ErrorNote error={action.error} />
            <div className="row"><Button type="submit" loading={action.busy} disabled={form.name.trim() === ""}>{t("add")}</Button></div>
          </form>
        </div>
      </Card>
      <Landmarks />
    </div>
  );
}

function Landmarks() {
  const t = useT();
  const { locations } = useLookups();
  const list = useLoad(() => ok(api.GET("/landmarks", { params: { query: {} } })), []);
  const action = useAction();
  const [form, setForm] = useState({ name: "", nameUr: "", aliases: "", locationId: "" });
  const add = (e: FormEvent) => {
    e.preventDefault();
    void action.run(async () => {
      await ok(api.POST("/landmarks", { body: { name: form.name, nameUr: form.nameUr || null, aliases: form.aliases.split(",").map((a) => a.trim()).filter(Boolean), locationId: form.locationId || null, latitude: null, longitude: null, isActive: null } }));
      setForm({ name: "", nameUr: "", aliases: "", locationId: "" });
      list.reload();
    });
  };
  const toggle = (l: S["LandmarkDto"]) => action.run(async () => {
    await ok(api.PUT("/landmarks/{id}", { params: { path: { id: l.id } }, body: { name: l.name, nameUr: l.nameUr, aliases: l.aliases, locationId: l.locationId, latitude: l.latitude, longitude: l.longitude, isActive: !l.isActive } }));
    list.reload();
  });
  return (
    <Card title={t("config.landmarks")}>
      <div className="stack">
        <p className="muted">{t("config.landmarksHint")}</p>
        <Async state={list}>
          {(rows) => rows.length === 0 ? <EmptyState title={t("config.noLandmarks")} /> : (
            <Table
              caption={t("config.landmarks")}
              rows={rows}
              rowKey={(l) => l.id}
              columns={[
                { key: "name", header: t("name"), render: (l) => l.name },
                { key: "aliases", header: t("config.aliases"), render: (l) => l.aliases.join(", ") || "—" },
                { key: "where", header: t("location"), render: (l) => locations.find((x) => x.id === l.locationId)?.name ?? "—" },
                { key: "active", header: t("status"), render: (l) => <Button variant="ghost" onClick={() => void toggle(l)}>{l.isActive ? t("active") : t("inactive")}</Button> },
              ]}
            />
          )}
        </Async>
        <form className="stack" onSubmit={add}>
          <div className="queue__fields">
            <TextField label={t("name")} value={form.name} required onChange={(e) => setForm({ ...form, name: e.target.value })} />
            <TextField label={t("nameUr")} dir="rtl" lang="ur" value={form.nameUr} onChange={(e) => setForm({ ...form, nameUr: e.target.value })} />
            <TextField label={t("config.aliases")} hint={t("config.aliasesHint")} value={form.aliases} onChange={(e) => setForm({ ...form, aliases: e.target.value })} />
            <SelectField label={t("location")} placeholder={t("choose")} value={form.locationId} onChange={(e) => setForm({ ...form, locationId: e.target.value })}
              options={locations.filter((l) => l.isActive).map((l) => ({ value: l.id, label: "—".repeat(l.depth) + " " + l.name }))} />
          </div>
          <ErrorNote error={action.error} />
          <div className="row"><Button type="submit" loading={action.busy} disabled={form.name.trim() === ""}>{t("add")}</Button></div>
        </form>
      </div>
    </Card>
  );
}

// Response times (RT-06) ---------------------------------------------------------------------------------------

const PRIORITIES = ["Low", "Normal", "High", "Urgent"] as const;

export function Sla() {
  const t = useT();
  const f = useFormat();
  const { categories, categoryName } = useLookups();
  const list = useLoad(() => ok(api.GET("/sla-policies")), []);
  const action = useAction();
  const [form, setForm] = useState({ categoryId: "", priority: "Normal", respond: "4", resolve: "48", business: false });
  const add = (e: FormEvent) => {
    e.preventDefault();
    void action.run(async () => {
      await ok(api.POST("/sla-policies", { body: { categoryId: form.categoryId || null, priority: form.priority, respondHours: Number(form.respond), resolveHours: Number(form.resolve), businessHoursOnly: form.business, isActive: null } }));
      list.reload();
    });
  };
  const toggle = (s: S["SlaDto"]) => action.run(async () => {
    await ok(api.PUT("/sla-policies/{id}", { params: { path: { id: s.id } }, body: { categoryId: s.categoryId, priority: s.priority, respondHours: s.respondHours, resolveHours: s.resolveHours, businessHoursOnly: s.businessHoursOnly, isActive: !s.isActive } }));
    list.reload();
  });
  return (
    <Card title={t("config.sla")}>
      <div className="stack">
        <p className="muted">{t("config.slaHint")}</p>
        <Async state={list}>
          {(rows) => rows.length === 0 ? <EmptyState title={t("config.noSla")} /> : (
            <Table
              caption={t("config.sla")}
              rows={rows}
              rowKey={(s) => s.id}
              columns={[
                { key: "cat", header: t("category"), render: (s) => (s.categoryId ? categoryName(s.categoryId) : t("config.allCategories")) },
                { key: "pri", header: t("priority"), render: (s) => t(`priority.${s.priority}` as Key) },
                { key: "respond", header: t("config.respondHours"), numeric: true, render: (s) => f.number(s.respondHours) },
                { key: "resolve", header: t("config.resolveHours"), numeric: true, render: (s) => f.number(s.resolveHours) },
                { key: "biz", header: t("config.businessHours"), render: (s) => (s.businessHoursOnly ? t("yes") : t("no")) },
                { key: "active", header: t("status"), render: (s) => <Button variant="ghost" onClick={() => void toggle(s)}>{s.isActive ? t("active") : t("inactive")}</Button> },
              ]}
            />
          )}
        </Async>
        <form className="stack" onSubmit={add}>
          <div className="queue__fields">
            <SelectField label={t("category")} placeholder={t("config.allCategories")} value={form.categoryId} onChange={(e) => setForm({ ...form, categoryId: e.target.value })}
              options={categories.filter((c) => c.isActive).map((c) => ({ value: c.id, label: categoryName(c.id) }))} />
            <SelectField label={t("priority")} value={form.priority} onChange={(e) => setForm({ ...form, priority: e.target.value })} options={PRIORITIES.map((p) => ({ value: p, label: t(`priority.${p}` as Key) }))} />
            <TextField type="number" min="1" label={t("config.respondHours")} value={form.respond} onChange={(e) => setForm({ ...form, respond: e.target.value })} required />
            <TextField type="number" min="1" label={t("config.resolveHours")} value={form.resolve} onChange={(e) => setForm({ ...form, resolve: e.target.value })} required />
          </div>
          <label className="row"><input type="checkbox" checked={form.business} onChange={(e) => setForm({ ...form, business: e.target.checked })} /> {t("config.businessHours")}</label>
          <ErrorNote error={action.error} />
          <div className="row"><Button type="submit" loading={action.busy}>{t("add")}</Button></div>
        </form>
      </div>
    </Card>
  );
}

// Escalation (RT-07, OR-12) ------------------------------------------------------------------------------------

const ESCALATION_ROLES = ["Supervisor", "Manager", "Committee"] as const;

export function Escalation() {
  const t = useT();
  const levels = useLoad(() => ok(api.GET("/escalation-levels")), []);
  return (
    <Card title={t("config.escalation")}>
      <p className="muted">{t("config.escalationHint")}</p>
      <Async state={levels}>{(rows) => <EscalationForm initial={rows} onSaved={levels.reload} />}</Async>
    </Card>
  );
}

function EscalationForm({ initial, onSaved }: { initial: S["LevelDto"][]; onSaved: () => void }) {
  const t = useT();
  const action = useAction();
  const [rows, setRows] = useState(initial);
  const [saved, setSaved] = useState(false);
  const set = (i: number, patch: Partial<S["LevelDto"]>) => setRows(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const save = () => action.run(async () => { await ok(api.PUT("/escalation-levels", { body: rows })); setSaved(true); onSaved(); });
  return (
    <div className="stack">
      {rows.map((r, i) => (
        <div key={i} className="queue__fields">
          <strong>{t("config.levelN", { level: r.level })}</strong>
          <TextField type="number" min="1" label={t("config.afterHours")} value={String(r.afterHours)} onChange={(e) => { setSaved(false); set(i, { afterHours: Number(e.target.value) }); }} />
          <SelectField label={t("config.tellRole")} value={r.role} onChange={(e) => { setSaved(false); set(i, { role: e.target.value as S["Role"] }); }}
            options={ESCALATION_ROLES.map((x) => ({ value: x, label: t(`role.${x}` as Key) }))} />
          <Button variant="ghost" onClick={() => setRows(rows.filter((_, j) => j !== i).map((x, j) => ({ ...x, level: j + 1 })))}>{t("directory.removeHouse")}</Button>
        </div>
      ))}
      <ErrorNote error={action.error} />
      {saved && <Alert tone="success">{t("saved")}</Alert>}
      <div className="row">
        <Button variant="secondary" onClick={() => setRows([...rows, { level: rows.length + 1, afterHours: (rows.at(-1)?.afterHours ?? 0) + 12, role: "Manager" }])}>{t("config.addLevel")}</Button>
        <Button onClick={() => void save()} loading={action.busy}>{t("save")}</Button>
      </div>
    </div>
  );
}

// Automatic routing, gated (RT-02, AI-17) ----------------------------------------------------------------------

export function AutoRoute() {
  const t = useT();
  const f = useFormat();
  const list = useLoad(() => ok(api.GET("/reports/auto-route-readiness")), []);
  const action = useAction();
  const set = (r: S["ReadinessDto"], enabled: boolean) => action.run(async () => {
    await ok(api.PUT("/categories/{id}/auto-route", { params: { path: { id: r.categoryId } }, body: { enabled, threshold: null } }));
    list.reload();
  });
  return (
    <Card title={t("config.autoRoute")}>
      <div className="stack">
        <Alert tone="info">{t("config.autoRouteHint")}</Alert>
        <ErrorNote error={action.error} />
        <Async state={list}>
          {(rows) => (
            <Table
              caption={t("config.autoRoute")}
              rows={rows}
              rowKey={(r) => r.categoryId}
              columns={[
                { key: "name", header: t("category"), render: (r) => r.name },
                { key: "samples", header: t("config.samples"), numeric: true, render: (r) => `${f.number(r.samples)} / ${f.number(r.requiredSamples)}` },
                { key: "acc", header: t("config.accuracy"), numeric: true, render: (r) => `${r.accuracy == null ? "—" : `${Math.round(r.accuracy * 100)}%`} / ${Math.round(r.requiredAccuracy * 100)}%` },
                { key: "state", header: t("status"), render: (r) => (r.autoRoute ? t("config.autoOn") : r.ready ? t("config.ready") : t("config.notReady")) },
                {
                  key: "action", header: t("action"),
                  render: (r) => r.autoRoute
                    ? <Button variant="secondary" onClick={() => void set(r, false)} loading={action.busy}>{t("config.turnOff")}</Button>
                    : <Button onClick={() => void set(r, true)} loading={action.busy} disabled={!r.ready}>{t("config.turnOn")}</Button>,
                },
              ]}
            />
          )}
        </Async>
      </div>
    </Card>
  );
}

// Price list (JC-01, WP-17) ------------------------------------------------------------------------------------

export function Prices() {
  const t = useT();
  const f = useFormat();
  const list = useLoad(() => ok(api.GET("/price-items", { params: { query: { includeInactive: true } } })), []);
  const action = useAction();
  const [form, setForm] = useState({ name: "", nameUr: "", unit: "", rate: "" });
  const [rating, setRating] = useState<S["PriceItemDto"]>();
  const add = (e: FormEvent) => {
    e.preventDefault();
    void action.run(async () => {
      await ok(api.POST("/price-items", { body: { name: form.name, nameUr: form.nameUr || null, unit: form.unit, rate: Number(form.rate), effectiveFrom: null } }));
      setForm({ name: "", nameUr: "", unit: "", rate: "" });
      list.reload();
    });
  };
  const toggle = (i: S["PriceItemDto"]) => action.run(async () => { await ok(api.PUT("/price-items/{id}", { params: { path: { id: i.id } }, body: { name: null, nameUr: null, unit: null, isActive: !i.isActive } })); list.reload(); });
  return (
    <Card title={t("config.prices")}>
      <div className="stack">
        <p className="muted">{t("config.pricesHint")}</p>
        <ErrorNote error={action.error} />
        <Async state={list}>
          {(rows) => rows.length === 0 ? <EmptyState title={t("config.noPrices")} /> : (
            <Table
              caption={t("config.prices")}
              rows={rows}
              rowKey={(i) => i.id}
              columns={[
                { key: "name", header: t("name"), render: (i) => <strong>{i.name}</strong> },
                { key: "unit", header: t("stock.unit"), render: (i) => i.unit },
                { key: "rate", header: t("config.rate"), numeric: true, render: (i) => (i.currentRate ? f.money(i.currentRate.rate) : "—") },
                { key: "since", header: t("config.rateSince"), render: (i) => (i.currentRate ? f.date(i.currentRate.effectiveFrom) : "—") },
                { key: "newrate", header: t("config.newRate"), render: (i) => <Button variant="ghost" onClick={() => setRating(i)}>{t("config.changeRate")}</Button> },
                { key: "active", header: t("status"), render: (i) => <Button variant="ghost" onClick={() => void toggle(i)}>{i.isActive ? t("active") : t("inactive")}</Button> },
              ]}
            />
          )}
        </Async>
        <form className="stack" onSubmit={add}>
          <div className="queue__fields">
            <TextField label={t("name")} value={form.name} required onChange={(e) => setForm({ ...form, name: e.target.value })} />
            <TextField label={t("nameUr")} dir="rtl" lang="ur" value={form.nameUr} onChange={(e) => setForm({ ...form, nameUr: e.target.value })} />
            <TextField label={t("stock.unit")} value={form.unit} required onChange={(e) => setForm({ ...form, unit: e.target.value })} />
            <TextField type="number" min="0" step="0.01" label={t("config.rate")} value={form.rate} required onChange={(e) => setForm({ ...form, rate: e.target.value })} />
          </div>
          <div className="row"><Button type="submit" loading={action.busy} disabled={form.name.trim() === "" || form.unit.trim() === "" || form.rate === ""}>{t("add")}</Button></div>
        </form>
        <RateSheet item={rating} onClose={() => setRating(undefined)} onSaved={() => { setRating(undefined); list.reload(); }} />
      </div>
    </Card>
  );
}

function RateSheet({ item, onClose, onSaved }: { item: S["PriceItemDto"] | undefined; onClose: () => void; onSaved: () => void }) {
  const t = useT();
  const action = useAction();
  const [rate, setRate] = useState("");
  const [from, setFrom] = useState("");
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!item) return;
    void action.run(async () => {
      await ok(api.POST("/price-items/{id}/rates", { params: { path: { id: item.id } }, body: { rate: Number(rate), effectiveFrom: from ? new Date(`${from}T00:00:00`).toISOString() : null } }));
      setRate(""); setFrom("");
      onSaved();
    });
  };
  return (
    <Sheet open={item !== undefined} title={item ? t("config.changeRateFor", { name: item.name }) : ""} onClose={onClose}>
      <form className="stack" onSubmit={submit}>
        <p className="muted">{t("config.rateHint")}</p>
        <TextField type="number" min="0" step="0.01" label={t("config.newRate")} value={rate} required onChange={(e) => setRate(e.target.value)} />
        <TextField type="date" label={t("config.effectiveFrom")} hint={t("config.effectiveHint")} value={from} onChange={(e) => setFrom(e.target.value)} />
        <ErrorNote error={action.error} />
        <div className="row"><Button type="submit" loading={action.busy} disabled={rate === ""}>{t("save")}</Button><Button type="button" variant="ghost" onClick={onClose}>{t("cancel")}</Button></div>
      </form>
    </Sheet>
  );
}
