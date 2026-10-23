import { Alert, Button, Card, EmptyState, SelectField, Table, TextField } from "@urbanfix/ui";
import { useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, ErrorNote } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { useHouseOptions } from "./Directory";

/** Marks the repair as chargeable to a house, for accounts to review once the work is finished (JB-10). */
export function ChargeableCard({ job, onDone }: { job: S["JobDto"]; onDone: () => void }) {
  const t = useT();
  const action = useAction();
  const { options } = useHouseOptions();
  const [house, setHouse] = useState(job.chargeHouseId ?? "");
  const label = options.find((o) => o.value === job.chargeHouseId)?.label;
  const save = (chargeable: boolean) => action.run(async () => {
    await ok(api.POST("/jobs/{id}/chargeable", { params: { path: { id: job.id } }, body: { chargeable, houseId: chargeable ? house : null } }));
    onDone();
  });
  return (
    <Card title={t("job.chargeable")}>
      <div className="stack">
        <p className="muted">{job.chargeable ? t("job.chargeableOn", { house: label ?? "…" }) : t("job.chargeableHint")}</p>
        <SelectField label={t("house")} placeholder={t("choose")} value={house} onChange={(e) => setHouse(e.target.value)} options={options} />
        <ErrorNote error={action.error} />
        <div className="row wrap">
          <Button onClick={() => void save(true)} loading={action.busy} disabled={house === ""}>{job.chargeable ? t("job.changeHouse") : t("job.markChargeable")}</Button>
          {job.chargeable && <Button variant="secondary" onClick={() => void save(false)} loading={action.busy}>{t("job.notChargeable")}</Button>}
        </div>
      </div>
    </Card>
  );
}

/** The AI's opinion on the before and after photos. It only advises: signing off or returning never waits for it (JB-09). */
export function ComparisonCard({ job }: { job: S["JobDto"] }) {
  const t = useT();
  const f = useFormat();
  const comparison = useLoad(() => ok(api.GET("/jobs/{id}/comparison", { params: { path: { id: job.id } } })).catch(() => null), [job.id, job.status]);
  const action = useAction();
  const run = () => action.run(async () => { await ok(api.POST("/jobs/{id}/comparison", { params: { path: { id: job.id } } })); comparison.reload(); });
  return (
    <Card title={t("job.comparison")} actions={<Button variant="secondary" onClick={() => void run()} loading={action.busy}>{t("job.compare")}</Button>}>
      <div className="stack">
        <Alert tone="info">{t("job.comparisonAdvisory")}</Alert>
        <ErrorNote error={action.error} />
        <Async state={comparison}>
          {(c) => c === null ? <p className="muted">{t("job.noComparison")}</p> : (
            <dl className="facts">
              <dt>{t("job.sameLocation")}</dt><dd>{f.number(Math.round(c.sameLocationLikelihood * 100))}%</dd>
              <dt>{t("job.whatChanged")}</dt><dd>{c.changeNote}</dd>
              <dt>{t("job.aiConfidence")}</dt><dd>{f.number(Math.round(c.confidence * 100))}%</dd>
              {c.needsMoreInfo && <><dt /><dd><strong>{t("job.needsMorePhotos")}</strong></dd></>}
              {c.degraded && <><dt /><dd className="muted">{t("job.comparisonDegraded")}</dd></>}
            </dl>
          )}
        </Async>
      </div>
    </Card>
  );
}

/** What was issued from the store against what was recorded as used, and the technician's request for more (JC-07, TA-09). */
export function MaterialsCard({ job, canRequest }: { job: S["JobDto"]; canRequest: boolean }) {
  const t = useT();
  const f = useFormat();
  const lines = useLoad(() => ok(api.GET("/stock/jobs/{jobId}/materials", { params: { path: { jobId: job.id } } })).catch(() => []), [job.id, job.status]);
  const requests = useLoad(() => ok(api.GET("/stock/requests", { params: { query: { jobId: job.id } } })).catch(() => []), [job.id]);
  const open = ["Open", "Accepted", "InProgress"].includes(job.status);
  return (
    <Card title={t("job.materials")}>
      <div className="stack">
        <Async state={lines}>
          {(rows) => rows.length === 0 ? <p className="muted">{t("job.noMaterials")}</p> : (
            <Table
              caption={t("job.materials")}
              rows={rows as S["MaterialLine"][]}
              rowKey={(l) => l.itemId}
              columns={[
                { key: "item", header: t("stock.item"), render: (l) => `${l.item} (${l.unit})` },
                { key: "issued", header: t("job.issued"), numeric: true, render: (l) => f.number(l.issued) },
                { key: "used", header: t("job.used"), numeric: true, render: (l) => f.number(l.used) },
                { key: "returned", header: t("job.returned"), numeric: true, render: (l) => f.number(l.returned) },
                { key: "diff", header: t("stock.difference"), numeric: true, render: (l) => (l.flagged ? <strong className="over">{f.number(l.unexplained)}</strong> : "0") },
              ]}
            />
          )}
        </Async>
        <Async state={requests}>
          {(rows) => rows.length === 0 ? null : (
            <ul className="timeline">
              {(rows as S["RequestDto"][]).map((r) => (
                <li key={r.id}>
                  <span><strong>{t(`stock.requestStatus.${r.status}` as Key)}</strong> · {r.lines.map((l) => `${l.item} × ${f.number(l.quantity)}`).join(", ")}</span>
                  {r.decisionNote && <span className="muted">{r.decisionNote}</span>}
                </li>
              ))}
            </ul>
          )}
        </Async>
        {canRequest && open && <RequestForm jobId={job.id} onSent={requests.reload} />}
      </div>
    </Card>
  );
}

function RequestForm({ jobId, onSent }: { jobId: string; onSent: () => void }) {
  const t = useT();
  const items = useLoad(() => ok(api.GET("/stock")).catch(() => []), []);
  const action = useAction();
  const [item, setItem] = useState("");
  const [qty, setQty] = useState("");
  const [note, setNote] = useState("");
  const submit = (e: FormEvent) => {
    e.preventDefault();
    void action.run(async () => {
      await ok(api.POST("/stock/requests", { body: { jobId, note: note || null, lines: [{ itemId: item, quantity: Number(qty) }] } }));
      setItem(""); setQty(""); setNote("");
      onSent();
    });
  };
  return (
    <form className="stack" onSubmit={submit}>
      <strong>{t("job.requestMaterials")}</strong>
      <div className="queue__fields">
        <SelectField label={t("stock.item")} placeholder={t("choose")} value={item} onChange={(e) => setItem(e.target.value)}
          options={((items.data ?? []) as S["StockLevelDto"][]).map((i) => ({ value: i.itemId, label: `${i.item} (${i.unit})` }))} />
        <TextField type="number" min="0" step="any" label={t("stock.quantity")} value={qty} onChange={(e) => setQty(e.target.value)} />
        <TextField label={t("job.requestNote")} value={note} onChange={(e) => setNote(e.target.value)} />
      </div>
      <ErrorNote error={action.error} />
      <div className="row"><Button type="submit" loading={action.busy} disabled={item === "" || Number(qty) <= 0}>{t("job.sendRequest")}</Button></div>
    </form>
  );
}

/** Where the estimate stands with the approvers, so a technician knows why work cannot start yet (JC-03). */
export function ApprovalNote({ estimate }: { estimate: NonNullable<S["EstimateDto"]> }) {
  const t = useT();
  if (estimate.approvalStatus === "NotRequired") return null;
  const tone = estimate.approvalStatus === "Approved" ? "success" : estimate.approvalStatus === "Pending" ? "warning" : "danger";
  return (
    <Alert tone={tone}>
      {t(`job.approval.${estimate.approvalStatus}` as Key)}
      {estimate.approvalStatus === "Pending" && <> <Link to="/approvals">{t("menu.approvals")}</Link></>}
    </Alert>
  );
}

/** Jobs that cost more than estimated, over the society's threshold (JC-05). */
export function Overruns() {
  const t = useT();
  const f = useFormat();
  const list = useLoad(() => ok(api.GET("/jobs/overruns")), []);
  return (
    <Async state={list}>
      {(rows) => rows.length === 0 ? <EmptyState title={t("reports.noOverruns")} /> : (
        <Table
          caption={t("reports.overruns")}
          rows={rows}
          rowKey={(r) => r.jobId}
          columns={[
            { key: "job", header: t("job"), render: (r) => <Link to={`/jobs/${r.jobId}`}>{r.number} ({r.reference})</Link> },
            { key: "est", header: t("reports.estimated"), numeric: true, render: (r) => f.money(r.estimated) },
            { key: "act", header: t("reports.actual"), numeric: true, render: (r) => f.money(r.actual) },
            { key: "diff", header: t("reports.variance"), numeric: true, render: (r) => <strong className="over">{f.money(r.difference)} ({f.number(r.percent)}%)</strong> },
            { key: "since", header: t("reports.flagged"), render: (r) => f.dateTime(r.flaggedAt) },
          ]}
        />
      )}
    </Async>
  );
}
