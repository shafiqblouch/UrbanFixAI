import { Alert, Button, Card, EmptyState, SelectField, Sheet, Table, TextField } from "@urbanfix/ui";
import { useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { api, ok, type ApiError, type S } from "../api";
import { Async, ErrorNote, PageHeader, ReasonDialog } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";

const STATUSES = ["Pending", "Approved", "Adjusted", "Rejected"] as const;

/** Finished repairs to be charged to a house, for accounts to approve, adjust or reject (JC-11, WP-18). Posting to bills waits for billing. */
export function ChargesPage() {
  const t = useT();
  const [status, setStatus] = useState<(typeof STATUSES)[number]>("Pending");
  const list = useLoad(() => ok(api.GET("/charges", { params: { query: { status } } })), [status]);
  return (
    <>
      <PageHeader title={t("menu.charges")} actions={
        <SelectField label={t("approvals.status")} value={status} onChange={(e) => setStatus(e.target.value as typeof status)}
          options={STATUSES.map((s) => ({ value: s, label: t(`charges.status.${s}` as Key) }))} />
      } />
      <Alert tone="info">{t("charges.notPostedYet")}</Alert>
      <Async state={list}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("charges.empty")} /> : (
          <ul className="queue">{rows.map((c) => <ChargeRow key={c.id} charge={c} onDone={list.reload} />)}</ul>
        )}
      </Async>
    </>
  );
}

function ChargeRow({ charge: c, onDone }: { charge: S["ChargeDto"]; onDone: () => void }) {
  const t = useT();
  const f = useFormat();
  const action = useAction();
  const [rejecting, setRejecting] = useState(false);
  const [adjusting, setAdjusting] = useState(false);
  const pending = c.status === "Pending";

  const decide = (body: S["ChargeDecision"]) => action.run(async () => {
    await ok(api.POST("/charges/{id}/decision", { params: { path: { id: c.id } }, body }));
    setRejecting(false); setAdjusting(false);
    onDone();
  });

  return (
    <li>
      <Card title={t("charges.repair", { number: c.jobNumber, reference: c.reference })}>
        <div className="queue__head">
          <strong>{c.houseLabel}</strong>
          <Link to={`/jobs/${c.jobId}`}>{c.jobNumber}</Link>
          <span>{t(`charges.status.${c.status}` as Key)}</span>
        </div>
        <p>{c.description}</p>
        <p className="muted">
          {c.residents.length === 0 ? t("charges.noResident") : c.residents.map((r) => `${r.name} (${t(`charges.relation.${r.relation}` as Key)})`).join(", ")}
        </p>
        <Table
          caption={t("charges.costs")}
          rows={c.costs}
          rowKey={(l) => `${l.kind}-${l.description}-${l.amount}`}
          columns={[
            { key: "kind", header: t("charges.kind"), render: (l) => l.kind },
            { key: "desc", header: t("description"), render: (l) => l.description },
            { key: "qty", header: t("stock.quantity"), numeric: true, render: (l) => f.number(l.quantity) },
            { key: "amount", header: t("charges.amount"), numeric: true, render: (l) => f.money(l.amount) },
          ]}
        />
        <p><strong>{t("charges.proposed", { amount: f.money(c.proposedAmount) })}</strong>{c.finalAmount != null && c.finalAmount !== c.proposedAmount && ` → ${t("charges.final", { amount: f.money(c.finalAmount) })}`}</p>
        {c.evidence.length > 0 && (
          <ul className="gallery">
            {c.evidence.map((e) => <li key={e.url}><img src={e.url} alt={t(`charges.stage.${e.stage}` as Key)} loading="lazy" /></li>)}
          </ul>
        )}
        {c.note && <p className="muted">{c.note}</p>}
        {pending && (
          <>
            <ErrorNote error={action.error} />
            <div className="row wrap">
              <Button onClick={() => void decide({ action: "approve", amount: null, note: null })} loading={action.busy}>{t("charges.approve")}</Button>
              <Button variant="secondary" onClick={() => { action.clear(); setAdjusting(true); }}>{t("charges.adjust")}</Button>
              <Button variant="danger" onClick={() => { action.clear(); setRejecting(true); }}>{t("charges.reject")}</Button>
            </div>
            <ReasonDialog open={rejecting} title={t("charges.reject")} busy={action.busy} error={action.error} onCancel={() => setRejecting(false)}
              onConfirm={(note) => void decide({ action: "reject", amount: null, note })} />
            <AdjustSheet open={adjusting} proposed={c.proposedAmount} busy={action.busy} error={action.error} onCancel={() => setAdjusting(false)}
              onConfirm={(amount, note) => void decide({ action: "adjust", amount, note })} />
          </>
        )}
      </Card>
    </li>
  );
}

function AdjustSheet({ open, proposed, busy, error, onCancel, onConfirm }: {
  open: boolean; proposed: number; busy: boolean; error: ApiError | undefined;
  onCancel: () => void; onConfirm: (amount: number, note: string) => void;
}) {
  const t = useT();
  const [amount, setAmount] = useState(String(proposed));
  const [note, setNote] = useState("");
  const valid = amount.trim() !== "" && Number(amount) >= 0 && note.trim() !== "";
  const submit = (e: FormEvent) => { e.preventDefault(); if (valid) onConfirm(Number(amount), note.trim()); };
  return (
    <Sheet open={open} title={t("charges.adjust")} onClose={onCancel}>
      <form className="stack" onSubmit={submit}>
        <TextField type="number" min="0" step="0.01" label={t("charges.finalAmount")} value={amount} onChange={(e) => setAmount(e.target.value)} required />
        <TextField label={t("reason")} value={note} onChange={(e) => setNote(e.target.value)} required />
        <ErrorNote error={error} />
        <div className="row">
          <Button type="submit" loading={busy} disabled={!valid}>{t("confirm")}</Button>
          <Button type="button" variant="ghost" onClick={onCancel}>{t("cancel")}</Button>
        </div>
      </form>
    </Sheet>
  );
}
