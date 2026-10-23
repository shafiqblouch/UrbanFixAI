import { Alert, Button, Card, SelectField, Table, TextField } from "@urbanfix/ui";
import { useState } from "react";
import { api, ok, type S } from "../api";
import { Async, ErrorNote, PageHeader, usePeople } from "../components";
import { useAction, useLoad } from "../hooks";
import { useT, type Key } from "../i18n";
import { useLookups } from "../lookups";
import { AutoRoute, Escalation, Places, Prices, Sla, Society } from "./ConfigMore";

type Tab = "society" | "places" | "departments" | "teams" | "categories" | "routing" | "sla" | "escalation" | "autoRoute" | "prices";
const TABS: Tab[] = ["society", "places", "departments", "teams", "categories", "routing", "sla", "escalation", "autoRoute", "prices"];

/** Departments, teams, categories with evidence rules and checklists, and routing rules (WP-13). Changes apply from the next complaint. */
export function ConfigPage() {
  const t = useT();
  const [tab, setTab] = useState<Tab>("society");
  return (
    <>
      <PageHeader title={t("menu.config")} />
      <div className="tabs" role="tablist" aria-label={t("menu.config")}>
        {TABS.map((x) => (
          <button key={x} role="tab" aria-selected={tab === x} className={tab === x ? "tab tab--active" : "tab"} onClick={() => setTab(x)}>{t(`config.${x}` as Key)}</button>
        ))}
      </div>
      <Alert>{t("config.appliesNext")}</Alert>
      {tab === "society" && <Society />}
      {tab === "places" && <Places />}
      {tab === "sla" && <Sla />}
      {tab === "escalation" && <Escalation />}
      {tab === "autoRoute" && <AutoRoute />}
      {tab === "prices" && <Prices />}
      {tab === "departments" && <Departments />}
      {tab === "teams" && <Teams />}
      {tab === "categories" && <Categories />}
      {tab === "routing" && <Routing />}
    </>
  );
}

function Departments() {
  const t = useT();
  const { departments, reload } = useLookups();
  const action = useAction();
  const [name, setName] = useState("");
  const [nameUr, setNameUr] = useState("");
  const add = () => action.run(async () => { await ok(api.POST("/departments", { body: { name, nameUr: nameUr || null, isActive: null } })); setName(""); setNameUr(""); reload(); });
  const toggle = (d: S["DepartmentDto"]) => action.run(async () => { await ok(api.PUT("/departments/{id}", { params: { path: { id: d.id } }, body: { name: d.name, nameUr: d.nameUr, isActive: !d.isActive } })); reload(); });
  return (
    <Card>
      <div className="stack">
        <Table caption={t("config.departments")} rows={departments} rowKey={(d) => d.id}
          columns={[
            { key: "n", header: t("name"), render: (d) => d.name },
            { key: "u", header: t("nameUr"), render: (d) => d.nameUr ?? "—" },
            { key: "a", header: t("status"), render: (d) => <Button variant="ghost" onClick={() => void toggle(d)}>{d.isActive ? t("active") : t("inactive")}</Button> },
          ]} />
        <form className="row wrap form-inline" onSubmit={(e) => { e.preventDefault(); void add(); }}>
          <TextField label={t("name")} value={name} onChange={(e) => setName(e.target.value)} required />
          <TextField label={t("nameUr")} dir="rtl" lang="ur" value={nameUr} onChange={(e) => setNameUr(e.target.value)} />
          <Button type="submit" loading={action.busy}>{t("add")}</Button>
        </form>
        <ErrorNote error={action.error} />
      </div>
    </Card>
  );
}

function Teams() {
  const t = useT();
  const { teams, departments, reload } = useLookups();
  const { list, nameOf } = usePeople();
  const action = useAction();
  const [name, setName] = useState("");
  const [departmentId, setDepartmentId] = useState("");
  const [member, setMember] = useState<Record<string, string>>({});
  const add = () => action.run(async () => { await ok(api.POST("/teams", { body: { departmentId, name } })); setName(""); reload(); });
  const addMember = (teamId: string) => action.run(async () => {
    await ok(api.POST("/teams/{teamId}/members", { params: { path: { teamId } }, body: { userId: member[teamId], isSupervisor: false } }));
    setMember((m) => ({ ...m, [teamId]: "" }));
    reload();
  });
  return (
    <div className="stack">
      {teams.map((team) => (
        <Card key={team.id} title={`${team.name}${team.isActive ? "" : ` (${t("inactive")})`}`}>
          <div className="stack">
            <p className="muted">{departments.find((d) => d.id === team.departmentId)?.name}</p>
            <ul>{team.members.map((m) => <li key={m.id}>{nameOf(m.userId)}</li>)}</ul>
            <form className="row wrap form-inline" onSubmit={(e) => { e.preventDefault(); void addMember(team.id); }}>
              <SelectField label={t("config.addMember")} placeholder={t("choose")} value={member[team.id] ?? ""} onChange={(e) => setMember((m) => ({ ...m, [team.id]: e.target.value }))}
                options={list.filter((p) => p.role === "Technician" && !team.members.some((m) => m.userId === p.userId)).map((p) => ({ value: p.userId, label: p.displayName }))} />
              <Button type="submit" disabled={!member[team.id]} loading={action.busy}>{t("add")}</Button>
            </form>
          </div>
        </Card>
      ))}
      <Card title={t("config.newTeam")}>
        <form className="row wrap form-inline" onSubmit={(e) => { e.preventDefault(); void add(); }}>
          <TextField label={t("name")} value={name} onChange={(e) => setName(e.target.value)} required />
          <SelectField label={t("department")} placeholder={t("choose")} value={departmentId} required onChange={(e) => setDepartmentId(e.target.value)}
            options={departments.filter((d) => d.isActive).map((d) => ({ value: d.id, label: d.name }))} />
          <Button type="submit" loading={action.busy} disabled={!departmentId}>{t("add")}</Button>
        </form>
        <ErrorNote error={action.error} />
      </Card>
    </div>
  );
}

const RULES = ["photo", "checklist", "note", "none"] as const;

function Categories() {
  const t = useT();
  const { categories, departments, reload } = useLookups();
  const action = useAction();
  const [editing, setEditing] = useState<string>();
  const blank = { key: "", nameEn: "", nameUr: "", departmentId: "", priority: "Normal", rule: "photo", checklist: "", requiresCosts: false };
  const [form, setForm] = useState(blank);

  const edit = (c: S["CategoryDto"]) => {
    setEditing(c.id);
    setForm({ key: c.key, nameEn: c.nameEn, nameUr: c.nameUr ?? "", departmentId: c.departmentId ?? "", priority: c.defaultPriority, rule: c.evidenceRule,
      checklist: (c.checklist as unknown as string[]).join("\n"), requiresCosts: c.requiresCosts });
  };
  const save = () => action.run(async () => {
    const body = {
      key: form.key, nameEn: form.nameEn, nameUr: form.nameUr || null, departmentId: form.departmentId || null, defaultPriority: form.priority, evidenceRule: form.rule,
      checklist: form.checklist.split("\n").map((s) => s.trim()).filter(Boolean), isActive: null, requiresCosts: form.requiresCosts,
    };
    if (editing) await ok(api.PUT("/categories/{id}", { params: { path: { id: editing } }, body }));
    else await ok(api.POST("/categories", { body }));
    setEditing(undefined); setForm(blank); reload();
  });
  const seed = () => action.run(async () => { await ok(api.POST("/categories/seed-templates")); reload(); });
  const set = (patch: Partial<typeof blank>) => setForm((f) => ({ ...f, ...patch }));

  return (
    <div className="stack">
      <Card>
        <Table caption={t("config.categories")} rows={categories} rowKey={(c) => c.id}
          columns={[
            { key: "k", header: t("config.key"), render: (c) => c.key },
            { key: "n", header: t("name"), render: (c) => c.nameEn },
            { key: "p", header: t("priority"), render: (c) => t(`priority.${c.defaultPriority}` as Key) },
            { key: "e", header: t("config.evidence"), render: (c) => t(`rule.${c.evidenceRule}` as Key) },
            { key: "c", header: t("config.requiresCosts"), render: (c) => (c.requiresCosts ? t("yes") : t("no")) },
            { key: "a", header: "", render: (c) => <Button variant="ghost" onClick={() => edit(c)}>{t("edit")}</Button> },
          ]} />
        {categories.length === 0 && <Button variant="secondary" onClick={() => void seed()} loading={action.busy}>{t("config.seed")}</Button>}
      </Card>
      <Card title={editing ? t("config.editCategory") : t("config.newCategory")}>
        <form className="stack form" onSubmit={(e) => { e.preventDefault(); void save(); }}>
          <TextField label={t("config.key")} value={form.key} onChange={(e) => set({ key: e.target.value })} required pattern="[a-z0-9_\-]{1,64}" hint={t("config.keyHint")} />
          <TextField label={t("name")} value={form.nameEn} onChange={(e) => set({ nameEn: e.target.value })} required />
          <TextField label={t("nameUr")} dir="rtl" lang="ur" value={form.nameUr} onChange={(e) => set({ nameUr: e.target.value })} />
          <SelectField label={t("department")} placeholder={t("choose")} value={form.departmentId} onChange={(e) => set({ departmentId: e.target.value })}
            options={departments.filter((d) => d.isActive).map((d) => ({ value: d.id, label: d.name }))} />
          <SelectField label={t("priority")} value={form.priority} onChange={(e) => set({ priority: e.target.value })}
            options={["Low", "Normal", "High", "Urgent"].map((p) => ({ value: p, label: t(`priority.${p}` as Key) }))} />
          <SelectField label={t("config.evidence")} value={form.rule} onChange={(e) => set({ rule: e.target.value })}
            options={RULES.map((r) => ({ value: r, label: t(`rule.${r}` as Key) }))} hint={t(`rule.${form.rule as (typeof RULES)[number]}.hint` as Key)} />
          <label className="stack">
            <span className="uf-field__label">{t("config.checklist")}</span>
            <textarea className="uf-field__input uf-field__input--multiline" rows={4} value={form.checklist} onChange={(e) => set({ checklist: e.target.value })} />
            <span className="uf-field__hint">{t("config.checklistHint")}</span>
          </label>
          <label className="row"><input type="checkbox" checked={form.requiresCosts} onChange={(e) => set({ requiresCosts: e.target.checked })} /> {t("config.requiresCosts")}</label>
          <ErrorNote error={action.error} />
          <div className="row">
            <Button type="submit" loading={action.busy}>{t("save")}</Button>
            {editing && <Button type="button" variant="ghost" onClick={() => { setEditing(undefined); setForm(blank); }}>{t("cancel")}</Button>}
          </div>
        </form>
      </Card>
    </div>
  );
}

function Routing() {
  const t = useT();
  const { categories, teams, locations, categoryName, teamName } = useLookups();
  const rules = useRules();
  const action = useAction();
  const [form, setForm] = useState({ name: "", categoryId: "", locationId: "", teamId: "", sortOrder: "0" });
  const add = () => action.run(async () => {
    await ok(api.POST("/routing-rules", { body: { name: form.name, categoryId: form.categoryId || null, locationId: form.locationId || null, teamId: form.teamId, sortOrder: Number(form.sortOrder) || 0, isActive: true } }));
    setForm({ name: "", categoryId: "", locationId: "", teamId: "", sortOrder: "0" });
    rules.reload();
  });
  const toggle = (r: S["RuleDto"]) => action.run(async () => {
    await ok(api.PUT("/routing-rules/{id}", { params: { path: { id: r.id } }, body: { name: r.name, categoryId: r.categoryId, locationId: r.locationId, teamId: r.teamId, sortOrder: r.sortOrder, isActive: !r.isActive } }));
    rules.reload();
  });
  const locName = (id: string | null) => (id ? locations.find((l) => l.id === id)?.name ?? "—" : t("routing.anywhere"));
  return (
    <div className="stack">
      <Alert>{t("routing.explain")}</Alert>
      <Card>
        <Async state={rules}>
          {(list) => (
            <Table caption={t("config.routing")} rows={list} rowKey={(r) => r.id}
              columns={[
                { key: "n", header: t("name"), render: (r) => r.name },
                { key: "c", header: t("category"), render: (r) => (r.categoryId ? categoryName(r.categoryId) : t("routing.anyCategory")) },
                { key: "l", header: t("location"), render: (r) => locName(r.locationId) },
                { key: "t", header: t("team"), render: (r) => teamName(r.teamId) },
                { key: "o", header: t("routing.order"), render: (r) => r.sortOrder, numeric: true },
                { key: "a", header: t("status"), render: (r) => <Button variant="ghost" onClick={() => void toggle(r)}>{r.isActive ? t("active") : t("inactive")}</Button> },
              ]} />
          )}
        </Async>
      </Card>
      <Card title={t("routing.new")}>
        <form className="stack form" onSubmit={(e) => { e.preventDefault(); void add(); }}>
          <TextField label={t("name")} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          <SelectField label={t("category")} placeholder={t("routing.anyCategory")} value={form.categoryId} onChange={(e) => setForm({ ...form, categoryId: e.target.value })}
            options={categories.map((c) => ({ value: c.id, label: categoryName(c.id) }))} />
          <SelectField label={t("location")} placeholder={t("routing.anywhere")} value={form.locationId} onChange={(e) => setForm({ ...form, locationId: e.target.value })}
            options={locations.map((l) => ({ value: l.id, label: "—".repeat(l.depth) + " " + l.name }))} />
          <SelectField label={t("team")} placeholder={t("choose")} value={form.teamId} required onChange={(e) => setForm({ ...form, teamId: e.target.value })}
            options={teams.filter((x) => x.isActive).map((x) => ({ value: x.id, label: x.name }))} />
          <TextField label={t("routing.order")} type="number" value={form.sortOrder} onChange={(e) => setForm({ ...form, sortOrder: e.target.value })} />
          <ErrorNote error={action.error} />
          <Button type="submit" loading={action.busy} disabled={!form.teamId}>{t("add")}</Button>
        </form>
      </Card>
    </div>
  );
}

function useRules() {
  return useLoad(() => ok(api.GET("/routing-rules")), []);
}
