import { Button, Card, EmptyState, SelectField } from "@urbanfix/ui";
import { useState } from "react";
import { Link } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, ErrorNote, PageHeader, ReasonDialog, usePeople } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";

const STATUSES = ["Pending", "Approved", "Rejected", "Expired", "Cancelled"] as const;

/** Estimates waiting for a manager or committee member's yes or no, and the history of answers (ID-10, JC-03). */
export function ApprovalsPage() {
  const t = useT();
  const [status, setStatus] = useState<(typeof STATUSES)[number]>("Pending");
  const list = useLoad(() => ok(api.GET("/approvals", { params: { query: { status } } })), [status]);
  return (
    <>
      <PageHeader title={t("menu.approvals")} actions={
        <SelectField label={t("approvals.status")} value={status} onChange={(e) => setStatus(e.target.value as typeof status)}
          options={STATUSES.map((s) => ({ value: s, label: t(`approvals.status.${s}` as Key) }))} />
      } />
      <Async state={list}>
        {(rows) => rows.length === 0 ? <EmptyState title={t(status === "Pending" ? "approvals.empty" : "approvals.emptyOther")} /> : (
          <ul className="queue">
            {rows.map((a) => <ApprovalRow key={a.id} approval={a} onDone={list.reload} />)}
          </ul>
        )}
      </Async>
    </>
  );
}

function ApprovalRow({ approval: a, onDone }: { approval: S["ApprovalDto"]; onDone: () => void }) {
  const t = useT();
  const f = useFormat();
  const { nameOf } = usePeople();
  const action = useAction();
  const [rejecting, setRejecting] = useState(false);
  const pending = a.status === "Pending";

  const decide = (approve: boolean, note: string | null) => action.run(async () => {
    await ok(api.POST("/approvals/{id}/decision", { params: { path: { id: a.id } }, body: { approve, note } }));
    setRejecting(false);
    onDone();
  });

  return (
    <li>
      <Card title={a.jobNumber ? t("approvals.estimateFor", { number: a.jobNumber }) : t("menu.approvals")}>
        <div className="queue__head">
          <strong>{f.money(a.amount)}</strong>
          {a.estimateVersion != null && <span className="muted">{t("approvals.version", { version: a.estimateVersion })}</span>}
          {a.jobId && <Link to={`/jobs/${a.jobId}`}>{a.reference ?? a.jobNumber}</Link>}
          {!pending && <span>{t(`approvals.status.${a.status}` as Key)}</span>}
        </div>
        <p className="muted">{t("approvals.requestedBy", { who: nameOf(a.requestedBy), when: f.dateTime(a.requestedAt) })}</p>
        {pending && <p className="muted">{t("approvals.expires", { when: f.dateTime(a.expiresAt) })}</p>}
        {pending && a.approvalsNeeded > 1 && <p>{t("approvals.needs", { have: a.decisions.filter((d) => d.approve).length, need: a.approvalsNeeded })}</p>}
        {a.decisions.length > 0 && (
          <ul className="timeline">
            {a.decisions.map((d) => (
              <li key={`${d.userId}-${d.decidedAt}`}>
                <span>{t(d.approve ? "approvals.decisionYes" : "approvals.decisionNo", { who: nameOf(d.userId) })} <small className="muted">{f.dateTime(d.decidedAt)}</small></span>
                {d.note && <span>{d.note}</span>}
              </li>
            ))}
          </ul>
        )}
        {!pending && a.note && <p>{a.note}</p>}
        {pending && (
          <>
            <ErrorNote error={action.error} />
            <div className="row wrap">
              <Button onClick={() => void decide(true, null)} loading={action.busy}>{t("approvals.approve")}</Button>
              <Button variant="danger" onClick={() => { action.clear(); setRejecting(true); }}>{t("approvals.reject")}</Button>
            </div>
            <ReasonDialog open={rejecting} title={t("approvals.rejectTitle")} busy={action.busy} error={action.error}
              onCancel={() => setRejecting(false)} onConfirm={(reason) => void decide(false, reason)}>
              <p className="muted">{t("approvals.noteHint")}</p>
            </ReasonDialog>
          </>
        )}
      </Card>
    </li>
  );
}
