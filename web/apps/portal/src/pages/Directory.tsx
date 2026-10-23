import { Alert, Button, Card, EmptyState, SelectField, Sheet, Table, TextField } from "@urbanfix/ui";
import { useMemo, useState, type FormEvent } from "react";
import { api, ApiError, getToken, ok, type S } from "../api";
import { Async, ErrorNote, PageHeader } from "../components";
import { useAction, useDebounced, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { useLookups } from "../lookups";
import { canConfigure } from "../roles";
import { useSession } from "../session";

const TABS = ["search", "houses", "vehicles", "help", "import", "checks"] as const;
type Tab = (typeof TABS)[number];

/** The people and things of the society: houses and residents, vehicles, daily help, bulk import and duplicate warnings (WP-15, OR-06 to OR-11). */
export function DirectoryPage() {
  const t = useT();
  const { role } = useSession();
  const tabs = TABS.filter((x) => x !== "import" || canConfigure(role));
  const [tab, setTab] = useState<Tab>("search");
  return (
    <>
      <PageHeader title={t("menu.directory")} />
      <div className="tabs" role="tablist" aria-label={t("menu.directory")}>
        {tabs.map((x) => (
          <button key={x} role="tab" aria-selected={tab === x} className={tab === x ? "tab tab--active" : "tab"} onClick={() => setTab(x)}>{t(`directory.${x}` as Key)}</button>
        ))}
      </div>
      {tab === "search" && <Search />}
      {tab === "houses" && <Houses />}
      {tab === "vehicles" && <Vehicles />}
      {tab === "help" && <Help />}
      {tab === "import" && <Import />}
      {tab === "checks" && <Checks />}
    </>
  );
}

/** "Block A › House 7" for a house, from the location tree. */
function useHouseLabel() {
  const { locations } = useLookups();
  return useMemo(() => {
    const byId = new Map(locations.map((l) => [l.id, l.name]));
    return (h: Pick<S["HouseDto"], "locationPath">) => h.locationPath.split("/").filter(Boolean).map((id) => byId.get(id) ?? "").filter(Boolean).join(" › ");
  }, [locations]);
}

// Search -------------------------------------------------------------------------------------------------------

function Search() {
  const t = useT();
  const [q, setQ] = useState("");
  const term = useDebounced(q.trim());
  const result = useLoad(() => (term.length >= 2 ? ok(api.GET("/directory/search", { params: { query: { q: term } } })) : Promise.resolve(undefined)), [term]);
  return (
    <div className="stack">
      <TextField type="search" label={t("directory.searchLabel")} hint={t("directory.searchHint")} value={q} onChange={(e) => setQ(e.target.value)} />
      {result.data && (
        <>
          <Section title={t("directory.houses")} empty={result.data.houses.length === 0}>
            <ul>{(result.data.houses as S["HouseHit"][]).map((h) => <li key={h.id}>{h.label}</li>)}</ul>
          </Section>
          <Section title={t("directory.people")} empty={result.data.people.length === 0}>
            <ul>{(result.data.people as S["PersonHit"][]).map((p) => <li key={p.userId}><strong>{p.displayName}</strong> <span className="muted">{[p.phone, p.email].filter(Boolean).join(" · ")}</span></li>)}</ul>
          </Section>
          <Section title={t("directory.vehicles")} empty={result.data.vehicles.length === 0}>
            <ul>{(result.data.vehicles as S["VehicleDto"][]).map((v) => <li key={v.id}><strong>{v.plate}</strong> <span className="muted">{[t(`directory.vehicleType.${v.type}` as Key), v.houseLabel, v.stickerCode].filter(Boolean).join(" · ")}</span></li>)}</ul>
          </Section>
          <Section title={t("directory.help")} empty={result.data.help.length === 0}>
            <ul>{(result.data.help as S["HelpDto"][]).map((h) => <li key={h.id}><strong>{h.name}</strong> <span className="muted">{[t(`directory.helpRole.${h.role}` as Key), h.phone].filter(Boolean).join(" · ")}</span></li>)}</ul>
          </Section>
        </>
      )}
    </div>
  );
}

function Section({ title, empty, children }: { title: string; empty: boolean; children: React.ReactNode }) {
  const t = useT();
  return <Card title={title}>{empty ? <p className="muted">{t("directory.noMatches")}</p> : children}</Card>;
}

// Houses -------------------------------------------------------------------------------------------------------

function Houses() {
  const t = useT();
  const f = useFormat();
  const label = useHouseLabel();
  const houses = useLoad(() => ok(api.GET("/houses")), []);
  const [open, setOpen] = useState<S["HouseDto"]>();
  return (
    <Async state={houses}>
      {(rows) => rows.length === 0 ? <EmptyState title={t("directory.noHouses")} /> : (
        <>
          <Table
            caption={t("directory.houses")}
            rows={rows}
            rowKey={(h) => h.id}
            columns={[
              { key: "label", header: t("house"), render: (h) => <strong>{label(h)}</strong> },
              { key: "status", header: t("status"), render: (h) => t(`directory.houseStatus.${h.status}` as Key) },
              { key: "size", header: t("directory.plot"), numeric: true, render: (h) => (h.plotSizeSqYd == null ? "—" : f.number(h.plotSizeSqYd)) },
              { key: "who", header: t("directory.residents"), render: (h) => <Button variant="ghost" onClick={() => setOpen(h)}>{t("directory.showResidents")}</Button> },
            ]}
          />
          <Residents house={open} label={open ? label(open) : ""} onClose={() => setOpen(undefined)} />
        </>
      )}
    </Async>
  );
}

function Residents({ house, label, onClose }: { house: S["HouseDto"] | undefined; label: string; onClose: () => void }) {
  const t = useT();
  const f = useFormat();
  const links = useLoad(() => (house ? ok(api.GET("/houses/{houseId}/residents", { params: { path: { houseId: house.id } } })) : Promise.resolve([])), [house?.id]);
  const action = useAction();
  const end = (id: string) => action.run(async () => { await ok(api.POST("/resident-links/{id}/end", { params: { path: { id } }, body: { toDate: null } })); links.reload(); });
  return (
    <Sheet open={house !== undefined} title={label} onClose={onClose}>
      <ErrorNote error={action.error} />
      <Async state={links}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("directory.noResidents")} /> : (
          <ul className="timeline">
            {rows.map((r) => (
              <li key={r.id}>
                <span><strong>{t(`charges.relation.${r.relation}` as Key)}</strong> · {t("directory.since", { date: f.date(r.fromDate) })}{r.toDate && ` · ${t("directory.until", { date: f.date(r.toDate) })}`}</span>
                {!r.toDate && <Button variant="ghost" onClick={() => void end(r.id)} loading={action.busy}>{t("directory.moveOut")}</Button>}
              </li>
            ))}
          </ul>
        )}
      </Async>
      <p className="muted">{t("directory.addResidentsByImport")}</p>
    </Sheet>
  );
}

// Vehicles -----------------------------------------------------------------------------------------------------

const VEHICLE_TYPES = ["car", "motorbike", "rickshaw", "van", "other"] as const;
const STATUSES = ["active", "inactive"] as const;

export function useHouseOptions() {
  const label = useHouseLabel();
  const houses = useLoad(() => ok(api.GET("/houses")), []);
  return { houses: houses.data ?? [], options: (houses.data ?? []).map((h) => ({ value: h.id, label: label(h) })) };
}

function Vehicles() {
  const t = useT();
  const [q, setQ] = useState("");
  const term = useDebounced(q.trim());
  const list = useLoad(() => ok(api.GET("/vehicles", { params: { query: { q: term || undefined } } })), [term]);
  const [editing, setEditing] = useState<S["VehicleDto"] | "new">();
  return (
    <div className="stack">
      <div className="row wrap">
        <TextField type="search" label={t("directory.plate")} value={q} onChange={(e) => setQ(e.target.value)} />
        <Button onClick={() => setEditing("new")}>{t("directory.addVehicle")}</Button>
      </div>
      <Async state={list}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("directory.noVehicles")} /> : (
          <Table
            caption={t("directory.vehicles")}
            rows={rows}
            rowKey={(v) => v.id}
            columns={[
              { key: "plate", header: t("directory.plate"), render: (v) => <strong>{v.plate}</strong> },
              { key: "type", header: t("directory.type"), render: (v) => t(`directory.vehicleType.${v.type}` as Key) },
              { key: "house", header: t("house"), render: (v) => v.houseLabel ?? "—" },
              { key: "sticker", header: t("directory.sticker"), render: (v) => v.stickerCode ?? "—" },
              { key: "status", header: t("status"), render: (v) => t(`directory.status.${v.status}` as Key) },
              { key: "edit", header: t("action"), render: (v) => <Button variant="ghost" onClick={() => setEditing(v)}>{t("edit")}</Button> },
            ]}
          />
        )}
      </Async>
      <VehicleSheet value={editing} onClose={() => setEditing(undefined)} onSaved={() => { setEditing(undefined); list.reload(); }} />
    </div>
  );
}

function VehicleSheet({ value, onClose, onSaved }: { value: S["VehicleDto"] | "new" | undefined; onClose: () => void; onSaved: () => void }) {
  const t = useT();
  const action = useAction();
  const { options } = useHouseOptions();
  const existing = value && value !== "new" ? value : undefined;
  const [form, setForm] = useState({ plate: "", type: "car", houseId: "", stickerCode: "", status: "active", note: "" });
  const [loaded, setLoaded] = useState<unknown>();
  if (value !== loaded) {
    setLoaded(value);
    setForm(existing
      ? { plate: existing.plate, type: existing.type, houseId: existing.houseId ?? "", stickerCode: existing.stickerCode ?? "", status: existing.status, note: existing.note ?? "" }
      : { plate: "", type: "car", houseId: "", stickerCode: "", status: "active", note: "" });
  }
  const set = (k: keyof typeof form, v: string) => setForm((f) => ({ ...f, [k]: v }));
  const submit = (e: FormEvent) => {
    e.preventDefault();
    const body = { plate: form.plate, type: form.type, houseId: form.houseId || null, stickerCode: form.stickerCode || null, status: form.status, note: form.note || null };
    void action.run(async () => {
      if (existing) await ok(api.PUT("/vehicles/{id}", { params: { path: { id: existing.id } }, body }));
      else await ok(api.POST("/vehicles", { body }));
      onSaved();
    });
  };
  return (
    <Sheet open={value !== undefined} title={t(existing ? "directory.editVehicle" : "directory.addVehicle")} onClose={onClose}>
      <form className="stack" onSubmit={submit}>
        <TextField label={t("directory.plate")} value={form.plate} required onChange={(e) => set("plate", e.target.value)} />
        <SelectField label={t("directory.type")} value={form.type} onChange={(e) => set("type", e.target.value)} options={VEHICLE_TYPES.map((x) => ({ value: x, label: t(`directory.vehicleType.${x}` as Key) }))} />
        <SelectField label={t("house")} placeholder={t("directory.noHouse")} value={form.houseId} onChange={(e) => set("houseId", e.target.value)} options={options} />
        <TextField label={t("directory.sticker")} value={form.stickerCode} onChange={(e) => set("stickerCode", e.target.value)} />
        <SelectField label={t("status")} value={form.status} onChange={(e) => set("status", e.target.value)} options={STATUSES.map((x) => ({ value: x, label: t(`directory.status.${x}` as Key) }))} />
        <TextField label={t("directory.note")} value={form.note} onChange={(e) => set("note", e.target.value)} />
        <ErrorNote error={action.error} />
        <div className="row"><Button type="submit" loading={action.busy} disabled={form.plate.trim() === ""}>{t("save")}</Button><Button type="button" variant="ghost" onClick={onClose}>{t("cancel")}</Button></div>
      </form>
    </Sheet>
  );
}

// Daily help ---------------------------------------------------------------------------------------------------

const HELP_ROLES = ["maid", "driver", "cook", "gardener", "guard", "nanny", "other"] as const;
const DAYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] as const;

function Help() {
  const t = useT();
  const [q, setQ] = useState("");
  const term = useDebounced(q.trim());
  const list = useLoad(() => ok(api.GET("/daily-help", { params: { query: { q: term || undefined } } })), [term]);
  const [editing, setEditing] = useState<S["HelpDto"] | "new">();
  return (
    <div className="stack">
      <div className="row wrap">
        <TextField type="search" label={t("name")} value={q} onChange={(e) => setQ(e.target.value)} />
        <Button onClick={() => setEditing("new")}>{t("directory.addHelp")}</Button>
      </div>
      <Async state={list}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("directory.noHelp")} /> : (
          <Table
            caption={t("directory.help")}
            rows={rows}
            rowKey={(h) => h.id}
            columns={[
              { key: "name", header: t("name"), render: (h) => <strong>{h.name}</strong> },
              { key: "role", header: t("directory.helpRoleLabel"), render: (h) => t(`directory.helpRole.${h.role}` as Key) },
              { key: "phone", header: t("directory.phone"), render: (h) => h.phone ?? "—" },
              { key: "days", header: t("directory.days"), render: (h) => (h.allowedDays.length === 0 ? t("directory.everyDay") : h.allowedDays.map((d) => t(`directory.day.${d}` as Key)).join(", ")) },
              { key: "houses", header: t("directory.workedHouses"), numeric: true, render: (h) => h.houseIds.length },
              { key: "status", header: t("status"), render: (h) => t(`directory.status.${h.status}` as Key) },
              { key: "edit", header: t("action"), render: (h) => <Button variant="ghost" onClick={() => setEditing(h)}>{t("edit")}</Button> },
            ]}
          />
        )}
      </Async>
      <HelpSheet value={editing} onClose={() => setEditing(undefined)} onSaved={() => { setEditing(undefined); list.reload(); }} />
    </div>
  );
}

function HelpSheet({ value, onClose, onSaved }: { value: S["HelpDto"] | "new" | undefined; onClose: () => void; onSaved: () => void }) {
  const t = useT();
  const action = useAction();
  const { options } = useHouseOptions();
  const existing = value && value !== "new" ? value : undefined;
  const blank = { name: "", phone: "", idNumber: "", role: "maid", status: "active", days: [] as string[], from: "", to: "", houseIds: [] as string[] };
  const [form, setForm] = useState(blank);
  const [loaded, setLoaded] = useState<unknown>();
  const [warnings, setWarnings] = useState<string[]>([]);
  if (value !== loaded) {
    setLoaded(value);
    setWarnings([]);
    setForm(existing
      ? { name: existing.name, phone: existing.phone ?? "", idNumber: "", role: existing.role, status: existing.status, days: [...existing.allowedDays], from: existing.allowedFrom ?? "", to: existing.allowedTo ?? "", houseIds: [...existing.houseIds] }
      : blank);
  }
  const toggleDay = (d: string) => setForm((f) => ({ ...f, days: f.days.includes(d) ? f.days.filter((x) => x !== d) : [...f.days, d] }));
  const submit = (e: FormEvent) => {
    e.preventDefault();
    const body = {
      name: form.name, phone: form.phone || null, idNumber: form.idNumber || null, role: form.role, status: form.status, allowedDays: form.days,
      allowedFrom: form.from || null, allowedTo: form.to || null, houseIds: form.houseIds,
    };
    void action.run(async () => {
      const saved = existing ? await ok(api.PUT("/daily-help/{id}", { params: { path: { id: existing.id } }, body })) : await ok(api.POST("/daily-help", { body }));
      if (saved.warnings.length > 0) setWarnings(saved.warnings as string[]);
      else onSaved();
    });
  };
  return (
    <Sheet open={value !== undefined} title={t(existing ? "directory.editHelp" : "directory.addHelp")} onClose={onClose}>
      <form className="stack" onSubmit={submit}>
        <TextField label={t("name")} value={form.name} required onChange={(e) => setForm({ ...form, name: e.target.value })} />
        <TextField label={t("directory.phone")} value={form.phone} onChange={(e) => setForm({ ...form, phone: e.target.value })} />
        <TextField label={t("directory.idNumber")} hint={existing?.idNumberMasked ? t("directory.idKept", { masked: existing.idNumberMasked }) : t("directory.idHint")} value={form.idNumber} onChange={(e) => setForm({ ...form, idNumber: e.target.value })} />
        <SelectField label={t("directory.helpRoleLabel")} value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value })} options={HELP_ROLES.map((x) => ({ value: x, label: t(`directory.helpRole.${x}` as Key) }))} />
        <fieldset>
          <legend>{t("directory.days")}</legend>
          <div className="row wrap">
            {DAYS.map((d) => (
              <label key={d} className="row"><input type="checkbox" checked={form.days.includes(d)} onChange={() => toggleDay(d)} /> {t(`directory.day.${d}` as Key)}</label>
            ))}
          </div>
          <p className="muted">{t("directory.daysHint")}</p>
        </fieldset>
        <div className="queue__fields">
          <TextField type="time" label={t("directory.from")} value={form.from} onChange={(e) => setForm({ ...form, from: e.target.value })} />
          <TextField type="time" label={t("directory.to")} value={form.to} onChange={(e) => setForm({ ...form, to: e.target.value })} />
        </div>
        <fieldset>
          <legend>{t("directory.workedHouses")}</legend>
          <ul>{form.houseIds.map((id) => (
            <li key={id} className="row">{options.find((o) => o.value === id)?.label ?? id}
              <Button type="button" variant="ghost" onClick={() => setForm({ ...form, houseIds: form.houseIds.filter((x) => x !== id) })}>{t("directory.removeHouse")}</Button></li>
          ))}</ul>
          <SelectField label={t("directory.addHouse")} placeholder={t("choose")} value="" onChange={(e) => e.target.value && setForm({ ...form, houseIds: [...new Set([...form.houseIds, e.target.value])] })} options={options.filter((o) => !form.houseIds.includes(o.value))} />
        </fieldset>
        <SelectField label={t("status")} value={form.status} onChange={(e) => setForm({ ...form, status: e.target.value })} options={STATUSES.map((x) => ({ value: x, label: t(`directory.status.${x}` as Key) }))} />
        {warnings.length > 0 && (
          <Alert tone="warning">
            <p>{t("directory.savedWithWarnings")}</p>
            <ul>{warnings.map((w) => <li key={w}>{t(`directory.warning.${w}` as Key)}</li>)}</ul>
            <Button type="button" variant="secondary" onClick={onSaved}>{t("directory.understood")}</Button>
          </Alert>
        )}
        <ErrorNote error={action.error} />
        <div className="row"><Button type="submit" loading={action.busy} disabled={form.name.trim() === ""}>{t("save")}</Button><Button type="button" variant="ghost" onClick={onClose}>{t("cancel")}</Button></div>
      </form>
    </Sheet>
  );
}

// Import -------------------------------------------------------------------------------------------------------

/** Reads a chosen file as text (older browsers and test environments have no Blob.text). */
const readText = (file: File): Promise<string> =>
  typeof file.text === "function" ? file.text() : new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsText(file);
  });

const KINDS = ["houses", "residents", "vehicles", "help"] as const;

/** Upload a CSV, check it without saving, read the report by line, then import the good rows (OR-10). */
function Import() {
  const t = useT();
  const [kind, setKind] = useState<(typeof KINDS)[number]>("residents");
  const [csv, setCsv] = useState("");
  const [report, setReport] = useState<S["ImportReport"]>();
  const action = useAction();

  async function send(dryRun: boolean) {
    const res = await fetch(`/api/directory/import?kind=${kind}&dryRun=${dryRun}`, { method: "POST", body: csv, headers: { "Content-Type": "text/csv", ...(getToken() ? { Authorization: `Bearer ${getToken()}` } : {}) } });
    const body = (await res.json().catch(() => ({}))) as S["ImportReport"] & { error?: string };
    if (!res.ok) throw new ApiError(res.status, body.error ?? `http_${res.status}`);
    return body;
  }
  const run = (dryRun: boolean) => action.run(async () => setReport(await send(dryRun)));
  const onFile = async (file: File | undefined) => { if (file) { setCsv(await readText(file)); setReport(undefined); } };

  return (
    <div className="stack">
      <Alert tone="info">{t(`directory.importFormat.${kind}` as Key)}</Alert>
      <div className="queue__fields">
        <SelectField label={t("directory.importKind")} value={kind} onChange={(e) => { setKind(e.target.value as typeof kind); setReport(undefined); }}
          options={KINDS.map((k) => ({ value: k, label: t(`directory.importKind.${k}` as Key) }))} />
        <label className="uf-field">
          <span className="uf-field__label">{t("directory.csvFile")}</span>
          <input type="file" accept=".csv,text/csv" onChange={(e) => void onFile(e.target.files?.[0])} />
        </label>
      </div>
      <ErrorNote error={action.error} />
      <div className="row wrap">
        <Button variant="secondary" onClick={() => void run(true)} loading={action.busy} disabled={csv === ""}>{t("directory.check")}</Button>
        <Button onClick={() => void run(false)} loading={action.busy} disabled={csv === "" || report === undefined || !report.dryRun}>{t("directory.importNow")}</Button>
      </div>
      {report && <ImportReportView report={report} />}
    </div>
  );
}

function ImportReportView({ report }: { report: S["ImportReport"] }) {
  const t = useT();
  const f = useFormat();
  return (
    <Card title={t(report.dryRun ? "directory.reportCheck" : "directory.reportDone")}>
      <p>{t("directory.reportCounts", { rows: f.number(report.rows), created: f.number(report.created), updated: f.number(report.updated), unchanged: f.number(report.unchanged) })}</p>
      {report.issues.length === 0 ? <p>{t("directory.noIssues")}</p> : (
        <Table
          caption={t("directory.issues")}
          rows={report.issues as S["ImportIssue"][]}
          rowKey={(i) => `${i.line}-${i.field}-${i.message}`}
          columns={[
            { key: "line", header: t("directory.line"), numeric: true, render: (i) => i.line },
            { key: "field", header: t("directory.field"), render: (i) => i.field },
            { key: "msg", header: t("directory.problem"), render: (i) => i.message },
          ]}
        />
      )}
      {report.dryRun && report.issues.length > 0 && <p className="muted">{t("directory.badRowsSkipped")}</p>}
    </Card>
  );
}

// Duplicate checks ---------------------------------------------------------------------------------------------

function Checks() {
  const t = useT();
  const groups = useLoad(() => ok(api.GET("/directory/duplicates")), []);
  const action = useAction();
  const dismiss = (g: S["DuplicateGroup"]) => action.run(async () => { await api.POST("/directory/duplicates/dismiss", { body: { kind: g.kind, key: g.key } }); groups.reload(); });
  return (
    <div className="stack">
      <p className="muted">{t("directory.checksHint")}</p>
      <ErrorNote error={action.error} />
      <Async state={groups}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("directory.noChecks")} /> : (
          <ul className="queue">
            {rows.map((g) => (
              <li key={`${g.kind}-${g.key}`}>
                <Card title={g.description}>
                  <ul>{(g.items as string[]).map((i) => <li key={i}>{i}</li>)}</ul>
                  <Button variant="ghost" onClick={() => void dismiss(g)} loading={action.busy}>{t("directory.fineAsIs")}</Button>
                </Card>
              </li>
            ))}
          </ul>
        )}
      </Async>
    </div>
  );
}
