import { Alert, Button, Card, EmptyState, SelectField, Sheet, Table, TextField } from "@urbanfix/ui";
import { useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, ErrorNote, PageHeader, ReasonDialog, usePeople } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";

const TABS = ["levels", "requests", "counts"] as const;
type Tab = (typeof TABS)[number];

/** The store: what is on the shelf, materials technicians ask for, and physical counts (JC-06 to JC-09, TA-09). */
export function StockPage() {
  const t = useT();
  const [tab, setTab] = useState<Tab>("levels");
  return (
    <>
      <PageHeader title={t("menu.stock")} />
      <div className="tabs" role="tablist" aria-label={t("menu.stock")}>
        {TABS.map((x) => (
          <button key={x} role="tab" aria-selected={tab === x} className={tab === x ? "tab tab--active" : "tab"} onClick={() => setTab(x)}>{t(`stock.${x}` as Key)}</button>
        ))}
      </div>
      {tab === "levels" && <Levels />}
      {tab === "requests" && <Requests />}
      {tab === "counts" && <Counts />}
    </>
  );
}

// Levels ------------------------------------------------------------------------------------------------------

type Mode = "receive" | "adjust" | "threshold" | "history";

function Levels() {
  const t = useT();
  const f = useFormat();
  const levels = useLoad(() => ok(api.GET("/stock")), []);
  const [open, setOpen] = useState<{ mode: Mode; item: S["StockLevelDto"] }>();
  const done = () => { setOpen(undefined); levels.reload(); };
  return (
    <Async state={levels}>
      {(rows) => rows.length === 0 ? <EmptyState title={t("stock.noItems")} /> : (
        <>
          <Table
            caption={t("stock.levels")}
            rows={rows}
            rowKey={(r) => r.itemId}
            columns={[
              { key: "item", header: t("stock.item"), render: (r) => <strong>{r.item}</strong> },
              { key: "unit", header: t("stock.unit"), render: (r) => r.unit },
              { key: "onHand", header: t("stock.onHand"), numeric: true, render: (r) => f.number(r.onHand) },
              { key: "low", header: t("stock.lowAt"), numeric: true, render: (r) => (r.lowThreshold == null ? "—" : f.number(r.lowThreshold)) },
              { key: "flag", header: t("status"), render: (r) => (r.low ? <strong className="over">{t("stock.low")}</strong> : <span className="muted">{t("stock.fine")}</span>) },
              {
                key: "actions", header: t("action"),
                render: (r) => (
                  <div className="row wrap">
                    <Button variant="secondary" onClick={() => setOpen({ mode: "receive", item: r })}>{t("stock.receive")}</Button>
                    <Button variant="ghost" onClick={() => setOpen({ mode: "adjust", item: r })}>{t("stock.adjust")}</Button>
                    <Button variant="ghost" onClick={() => setOpen({ mode: "threshold", item: r })}>{t("stock.threshold")}</Button>
                    <Button variant="ghost" onClick={() => setOpen({ mode: "history", item: r })}>{t("stock.history")}</Button>
                  </div>
                ),
              },
            ]}
          />
          <ItemSheet open={open} onClose={() => setOpen(undefined)} onDone={done} />
        </>
      )}
    </Async>
  );
}

function ItemSheet({ open, onClose, onDone }: { open: { mode: Mode; item: S["StockLevelDto"] } | undefined; onClose: () => void; onDone: () => void }) {
  const t = useT();
  const f = useFormat();
  const action = useAction();
  const [value, setValue] = useState("");
  const [note, setNote] = useState("");
  const mode = open?.mode;
  const item = open?.item;
  const movements = useLoad(() => (mode === "history" && item ? ok(api.GET("/stock/items/{id}/movements", { params: { path: { id: item.itemId } } })) : Promise.resolve([])), [mode, item?.itemId]);

  const amount = Number(value);
  const valid = value.trim() !== "" && Number.isFinite(amount) && (mode === "receive" ? amount > 0 : mode === "adjust" ? amount !== 0 && note.trim() !== "" : amount >= 0);

  function submit(e: FormEvent) {
    e.preventDefault();
    if (!item || !mode) return;
    void action.run(async () => {
      if (mode === "receive") await ok(api.POST("/stock/receipts", { body: { itemId: item.itemId, quantity: amount, reason: note.trim() || null } }));
      else if (mode === "adjust") await ok(api.POST("/stock/adjustments", { body: { itemId: item.itemId, quantity: amount, reason: note.trim() } }));
      else await ok(api.PUT("/stock/items/{id}/threshold", { params: { path: { id: item.itemId } }, body: { lowThreshold: amount } }));
      setValue(""); setNote("");
      onDone();
    });
  }

  return (
    <Sheet open={open !== undefined} title={item ? `${t(`stock.${mode}` as Key)}: ${item.item}` : ""} onClose={() => { action.clear(); onClose(); }}>
      {mode === "history" ? (
        <Async state={movements}>
          {(rows) => rows.length === 0 ? <EmptyState title={t("stock.noMovements")} /> : (
            <Table
              caption={t("stock.history")}
              rows={rows}
              rowKey={(m) => m.id}
              columns={[
                { key: "when", header: t("when"), render: (m) => f.dateTime(m.createdAt) },
                { key: "kind", header: t("stock.kind"), render: (m) => t(`stock.kind.${m.kind}` as Key) },
                { key: "qty", header: t("stock.quantity"), numeric: true, render: (m) => f.number(m.quantity) },
                { key: "why", header: t("reason"), render: (m) => m.reason },
              ]}
            />
          )}
        </Async>
      ) : (
        <form className="stack" onSubmit={submit}>
          <p className="muted">{t("stock.nowOnHand", { quantity: f.number(item?.onHand ?? 0), unit: item?.unit ?? "" })}</p>
          <TextField type="number" step="any" label={t(mode === "threshold" ? "stock.lowAt" : mode === "adjust" ? "stock.change" : "stock.quantity")}
            hint={mode === "adjust" ? t("stock.changeHint") : mode === "threshold" ? t("stock.thresholdHint") : undefined}
            value={value} onChange={(e) => setValue(e.target.value)} required />
          {mode !== "threshold" && <TextField label={t("reason")} value={note} onChange={(e) => setNote(e.target.value)} required={mode === "adjust"} />}
          <ErrorNote error={action.error} />
          <div className="row">
            <Button type="submit" loading={action.busy} disabled={!valid}>{t("save")}</Button>
            <Button type="button" variant="ghost" onClick={onClose}>{t("cancel")}</Button>
          </div>
        </form>
      )}
    </Sheet>
  );
}

// Requests ----------------------------------------------------------------------------------------------------

const REQUEST_STATUSES = ["Open", "Fulfilled", "Rejected", "Cancelled"] as const;

function Requests() {
  const t = useT();
  const [status, setStatus] = useState<(typeof REQUEST_STATUSES)[number]>("Open");
  const list = useLoad(() => ok(api.GET("/stock/requests", { params: { query: { status } } })), [status]);
  return (
    <>
      <div className="row">
        <SelectField label={t("approvals.status")} value={status} onChange={(e) => setStatus(e.target.value as typeof status)}
          options={REQUEST_STATUSES.map((s) => ({ value: s, label: t(`stock.requestStatus.${s}` as Key) }))} />
      </div>
      <Async state={list}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("stock.noRequests")} /> : (
          <ul className="queue">{rows.map((r) => <RequestRow key={r.id} request={r} onDone={list.reload} />)}</ul>
        )}
      </Async>
    </>
  );
}

function RequestRow({ request: r, onDone }: { request: S["RequestDto"]; onDone: () => void }) {
  const t = useT();
  const f = useFormat();
  const { nameOf } = usePeople();
  const action = useAction();
  const [rejecting, setRejecting] = useState(false);
  const open = r.status === "Open";
  const short = r.lines.some((l) => l.quantity > l.onHand);

  const fulfil = () => action.run(async () => { await ok(api.POST("/stock/requests/{id}/fulfil", { params: { path: { id: r.id } }, body: { lines: null } })); onDone(); });
  const reject = (reason: string) => action.run(async () => {
    await ok(api.POST("/stock/requests/{id}/reject", { params: { path: { id: r.id } }, body: { text: reason } }));
    setRejecting(false);
    onDone();
  });

  return (
    <li>
      <Card title={t("stock.requestFor", { number: r.jobNumber })}>
        <div className="queue__head">
          <Link to={`/jobs/${r.jobId}`}>{r.jobNumber}</Link>
          <span>{t(`stock.requestStatus.${r.status}` as Key)}</span>
          <span className="muted">{t("approvals.requestedBy", { who: nameOf(r.requestedBy), when: f.dateTime(r.createdAt) })}</span>
        </div>
        {r.note && <p>{r.note}</p>}
        <Table
          caption={t("stock.requestFor", { number: r.jobNumber })}
          rows={r.lines}
          rowKey={(l) => l.itemId}
          columns={[
            { key: "item", header: t("stock.item"), render: (l) => `${l.item} (${l.unit})` },
            { key: "asked", header: t("stock.asked"), numeric: true, render: (l) => f.number(l.quantity) },
            { key: "onHand", header: t("stock.onHand"), numeric: true, render: (l) => (l.quantity > l.onHand && open ? <strong className="over">{f.number(l.onHand)}</strong> : f.number(l.onHand)) },
          ]}
        />
        {r.decisionNote && <p className="muted">{r.decisionNote}</p>}
        {open && (
          <>
            {short && <Alert tone="warning">{t("stock.notEnough")}</Alert>}
            <ErrorNote error={action.error} />
            <div className="row wrap">
              <Button onClick={() => void fulfil()} loading={action.busy} disabled={short}>{t("stock.issueAll")}</Button>
              <Button variant="danger" onClick={() => { action.clear(); setRejecting(true); }}>{t("stock.refuse")}</Button>
            </div>
            <ReasonDialog open={rejecting} title={t("stock.refuse")} busy={action.busy} error={action.error} onCancel={() => setRejecting(false)} onConfirm={(reason) => void reject(reason)} />
          </>
        )}
      </Card>
    </li>
  );
}

// Counts ------------------------------------------------------------------------------------------------------

function Counts() {
  const t = useT();
  const f = useFormat();
  const { nameOf } = usePeople();
  const levels = useLoad(() => ok(api.GET("/stock")), []);
  const counts = useLoad(() => ok(api.GET("/stock/counts")), []);
  const action = useAction();
  const [counted, setCounted] = useState<Record<string, string>>({});
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [note, setNote] = useState("");

  return (
    <div className="stack">
      <Async state={levels}>
        {(items) => {
          const entered = items.filter((i) => (counted[i.itemId] ?? "").trim() !== "");
          const differs = (i: S["StockLevelDto"]) => Number(counted[i.itemId]) !== i.onHand;
          const complete = entered.length > 0 && entered.every((i) => Number(counted[i.itemId]) >= 0 && (!differs(i) || (reasons[i.itemId] ?? "").trim() !== ""));
          const submit = (e: FormEvent) => {
            e.preventDefault();
            void action.run(async () => {
              await ok(api.POST("/stock/counts", {
                body: { note: note.trim() || null, lines: entered.map((i) => ({ itemId: i.itemId, counted: Number(counted[i.itemId]), reason: differs(i) ? reasons[i.itemId].trim() : null })) },
              }));
              setCounted({}); setReasons({}); setNote("");
              levels.reload(); counts.reload();
            });
          };
          return (
            <Card title={t("stock.newCount")}>
              <form className="stack" onSubmit={submit}>
                <p className="muted">{t("stock.countHint")}</p>
                {items.map((i) => (
                  <div key={i.itemId} className="queue__fields">
                    <strong>{i.item} <span className="muted">({t("stock.onHandIs", { quantity: f.number(i.onHand), unit: i.unit })})</span></strong>
                    <TextField type="number" step="any" min="0" label={t("stock.counted")} value={counted[i.itemId] ?? ""} onChange={(e) => setCounted((c) => ({ ...c, [i.itemId]: e.target.value }))} />
                    {(counted[i.itemId] ?? "").trim() !== "" && differs(i) && (
                      <TextField label={t("stock.whyDifferent")} value={reasons[i.itemId] ?? ""} required onChange={(e) => setReasons((r) => ({ ...r, [i.itemId]: e.target.value }))} />
                    )}
                  </div>
                ))}
                <TextField label={t("stock.countNote")} value={note} onChange={(e) => setNote(e.target.value)} />
                <ErrorNote error={action.error} />
                <div className="row"><Button type="submit" loading={action.busy} disabled={!complete}>{t("stock.saveCount")}</Button></div>
              </form>
            </Card>
          );
        }}
      </Async>
      <Async state={counts}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("stock.noCounts")} /> : (
          <ul className="queue">
            {rows.map((c) => (
              <li key={c.id}>
                <Card title={t("stock.countOn", { when: f.dateTime(c.countedAt), who: nameOf(c.countedBy) })}>
                  {c.note && <p>{c.note}</p>}
                  <Table
                    caption={t("stock.counts")}
                    rows={c.lines}
                    rowKey={(l) => l.itemId}
                    columns={[
                      { key: "item", header: t("stock.item"), render: (l) => levels.data?.find((i) => i.itemId === l.itemId)?.item ?? "—" },
                      { key: "expected", header: t("stock.expected"), numeric: true, render: (l) => f.number(l.expected) },
                      { key: "counted", header: t("stock.counted"), numeric: true, render: (l) => f.number(l.counted) },
                      { key: "diff", header: t("stock.difference"), numeric: true, render: (l) => (l.difference === 0 ? "0" : <strong className="over">{f.number(l.difference)}</strong>) },
                      { key: "why", header: t("reason"), render: (l) => l.reason ?? "" },
                    ]}
                  />
                </Card>
              </li>
            ))}
          </ul>
        )}
      </Async>
    </div>
  );
}
