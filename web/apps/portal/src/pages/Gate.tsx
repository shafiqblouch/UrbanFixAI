import { Alert, Button, Card, EmptyState, StatusChip, Table, TextField, type Column, type Status } from "@urbanfix/ui";
import { useState } from "react";
import { api, ok, type S } from "../api";
import { Async, PageHeader } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";

type Event = S["EventDto"];
type Pass = S["PassDto"];
type GateDto = S["GateDto"];

const TABS = ["inside", "log", "passes", "checkpoints"] as const;
type Tab = (typeof TABS)[number];

const passTone: Record<string, Status> = {
  Valid: "resolved", NotYetValid: "open", Pending: "inProgress", Expired: "rejected", UsedUp: "rejected", Cancelled: "rejected", Denied: "rejected",
};

/** Gate security oversight for managers: who is inside, the crossing log, visitor passes and checkpoints (GT-01). */
export function GatePage() {
  const t = useT();
  const [tab, setTab] = useState<Tab>("inside");
  const features = useLoad(() => ok(api.GET("/features", {})), []);
  return (
    <>
      <PageHeader title={t("menu.gate")} subtitle={t("gate.subtitle")} />
      <Async state={features}>
        {(flags) => !flags.gate ? (
          <EnableGate onEnabled={features.reload} />
        ) : (
          <>
            <div className="tabs" role="tablist" aria-label={t("menu.gate")}>
              {TABS.map((x) => (
                <button key={x} role="tab" aria-selected={tab === x} className={tab === x ? "tab tab--active" : "tab"} onClick={() => setTab(x)}>
                  {t(`gate.${x}` as Key)}
                </button>
              ))}
            </div>
            {tab === "inside" && <Inside />}
            {tab === "log" && <Log />}
            {tab === "passes" && <Passes />}
            {tab === "checkpoints" && <Checkpoints />}
          </>
        )}
      </Async>
    </>
  );
}

/** When the society has never switched gate security on, a manager can turn it on from here (self-service feature flag). */
function EnableGate({ onEnabled }: { onEnabled: () => void }) {
  const t = useT();
  const action = useAction();
  const enable = () => action.run(async () => {
    await ok(api.PUT("/my/features/{feature}", { params: { path: { feature: "gate" } }, body: { enabled: true } }));
    onEnabled();
  });
  return (
    <Card title={t("gate.disabledTitle")}>
      <p className="muted">{t("gate.disabledHint")}</p>
      {action.error && <Alert tone="danger">{t("error.generic")}</Alert>}
      <Button onClick={() => void enable()} loading={action.busy}>{t("gate.enable")}</Button>
    </Card>
  );
}

function useTime() {
  const f = useFormat();
  return (iso: string) => { try { return f.dateTime(iso); } catch { return iso; } };
}

/** The people and vehicles whose latest crossing was an allowed entry. */
function Inside() {
  const t = useT();
  const time = useTime();
  const state = useLoad<Event[]>(() => ok(api.GET("/gate/inside", {})), []);
  const columns: Column<Event>[] = [
    { key: "who", header: t("gate.col.who"), render: (e) => e.subjectName || e.vehiclePlate || t("gate.col.visitor") },
    { key: "house", header: t("gate.col.house"), render: (e) => e.houseLabel ?? "—" },
    { key: "plate", header: t("gate.col.plate"), render: (e) => e.vehiclePlate ?? "—" },
    { key: "time", header: t("gate.col.since"), render: (e) => time(e.recordedAt) },
  ];
  return (
    <Async state={state}>
      {(rows) => rows.length === 0
        ? <EmptyState title={t("gate.nobodyInside")} />
        : <Card><Table caption={t("gate.insideNow")} columns={columns} rows={rows} rowKey={(e) => e.id} /></Card>}
    </Async>
  );
}

/** The recent entry/exit log. */
function Log() {
  const t = useT();
  const time = useTime();
  const state = useLoad<Event[]>(() => ok(api.GET("/gate/events", { params: { query: { limit: 200 } } })), []);
  const columns: Column<Event>[] = [
    { key: "time", header: t("gate.col.time"), render: (e) => time(e.recordedAt) },
    { key: "dir", header: t("gate.col.direction"), render: (e) => <StatusChip status={e.direction === "in" ? "open" : "assigned"} label={t(`gate.${e.direction}` as Key)} /> },
    { key: "who", header: t("gate.col.who"), render: (e) => e.subjectName || e.vehiclePlate || t("gate.col.visitor") },
    { key: "house", header: t("gate.col.house"), render: (e) => e.houseLabel ?? "—" },
    { key: "decision", header: t("gate.col.decision"), render: (e) => <StatusChip status={e.decision === "denied" ? "rejected" : "resolved"} label={t(`gate.${e.decision}` as Key)} /> },
  ];
  return (
    <Async state={state}>
      {(rows) => rows.length === 0
        ? <EmptyState title={t("gate.noEvents")} />
        : <Card><Table caption={t("gate.recent")} columns={columns} rows={rows} rowKey={(e) => e.id} /></Card>}
    </Async>
  );
}

/** Pre-authorised visitor passes; a manager can cancel one. */
function Passes() {
  const t = useT();
  const time = useTime();
  const state = useLoad<Pass[]>(() => ok(api.GET("/gate/passes", {})), []);
  const act = useAction();
  const cancel = (id: string) => void act.run(async () => { await ok(api.POST("/gate/passes/{id}/cancel", { params: { path: { id } } })); state.reload(); });
  const columns: Column<Pass>[] = [
    { key: "code", header: t("gate.col.code"), render: (p) => p.code },
    { key: "visitor", header: t("gate.col.visitor"), render: (p) => p.visitorName },
    { key: "house", header: t("gate.col.house"), render: (p) => p.houseLabel ?? "—" },
    { key: "kind", header: t("gate.col.kind"), render: (p) => t(`gate.kind.${p.kind}` as Key) },
    { key: "valid", header: t("gate.col.valid"), render: (p) => `${time(p.validFrom)} – ${time(p.validTo)}` },
    { key: "status", header: t("gate.col.status"), render: (p) => <StatusChip status={passTone[p.state] ?? "open"} label={t(`gate.passState.${p.state}` as Key)} /> },
    { key: "act", header: "", render: (p) => p.status === "active" ? <Button variant="secondary" onClick={() => cancel(p.id)} disabled={act.busy}>{t("gate.cancelPass")}</Button> : null },
  ];
  return (
    <Async state={state}>
      {(rows) => rows.length === 0
        ? <EmptyState title={t("gate.noPasses")} />
        : <Card><Table caption={t("gate.passes")} columns={columns} rows={rows} rowKey={(p) => p.id} /></Card>}
    </Async>
  );
}

/** The society's manned gates; a manager adds them and turns them on or off. */
function Checkpoints() {
  const t = useT();
  const state = useLoad<GateDto[]>(() => ok(api.GET("/gates", {})), []);
  const act = useAction();
  const [name, setName] = useState("");

  const add = () => void act.run(async () => {
    if (!name.trim()) return;
    await ok(api.POST("/gates", { body: { name: name.trim(), isActive: true } }));
    setName(""); state.reload();
  });
  const toggle = (g: GateDto) => void act.run(async () => {
    await ok(api.PUT("/gates/{id}", { params: { path: { id: g.id } }, body: { name: g.name, isActive: !g.isActive } }));
    state.reload();
  });

  const columns: Column<GateDto>[] = [
    { key: "name", header: t("gate.gateName"), render: (g) => g.name },
    { key: "status", header: t("gate.col.status"), render: (g) => <StatusChip status={g.isActive ? "resolved" : "rejected"} label={t(g.isActive ? "gate.active" : "gate.inactive")} /> },
    { key: "act", header: "", render: (g) => <Button variant="secondary" onClick={() => toggle(g)} disabled={act.busy}>{t(g.isActive ? "gate.deactivate" : "gate.activate")}</Button> },
  ];

  return (
    <>
      <Card>
        <form className="form-row" onSubmit={(e) => { e.preventDefault(); add(); }}>
          <TextField label={t("gate.gateName")} value={name} onChange={(e) => setName(e.target.value)} />
          <Button type="submit" disabled={act.busy || !name.trim()}>{t("gate.addGate")}</Button>
        </form>
        {act.error && <Alert tone="danger">{act.error.code}</Alert>}
      </Card>
      <Async state={state}>
        {(rows) => rows.length === 0
          ? <EmptyState title={t("gate.noGates")} />
          : <Card><Table caption={t("gate.checkpoints")} columns={columns} rows={rows} rowKey={(g) => g.id} /></Card>}
      </Async>
    </>
  );
}
