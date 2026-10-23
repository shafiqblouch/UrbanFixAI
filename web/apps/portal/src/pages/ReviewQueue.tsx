import { Button, Card, EmptyState, SelectField } from "@urbanfix/ui";
import { useState } from "react";
import { Link } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, ComplaintStatusChip, ErrorNote, PageHeader, PriorityTag, ReasonDialog } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { useLookups } from "../lookups";
import { canAssign } from "../roles";
import { useSession } from "../session";

const PRIORITIES = ["Low", "Normal", "High", "Urgent"] as const;

/**
 * Supervisor queue with the AI's suggestions pre-filled (WP-04). One click on "Accept and assign" reviews the complaint, applies the
 * category and priority shown, and raises a job for the suggested team; any field can be changed first.
 */
export function ReviewQueuePage() {
  const t = useT();
  const queue = useLoad(() => ok(api.GET("/review-queue", { params: { query: { limit: 100 } } })), []);
  return (
    <>
      <PageHeader title={t("menu.review")} />
      <Async state={queue}>
        {(items) => items.length === 0 ? <EmptyState title={t("review.empty")} /> : (
          <ul className="queue">
            {items.map((item) => <QueueRow key={item.complaint.id} item={item} onDone={queue.reload} />)}
          </ul>
        )}
      </Async>
    </>
  );
}

function QueueRow({ item, onDone }: { item: S["QueueItemDto"]; onDone: () => void }) {
  const t = useT();
  const f = useFormat();
  const { role } = useSession();
  const lookups = useLookups();
  const c = item.complaint;
  const action = useAction();
  const [categoryId, setCategoryId] = useState(item.suggestedCategoryId ?? c.categoryId ?? "");
  const [priority, setPriority] = useState(item.suggestedCategoryId && !c.categoryId ? item.suggestedPriority ?? c.priority : c.priority);
  const [teamId, setTeamId] = useState(item.suggestedTeamId ?? "");
  const [rejecting, setRejecting] = useState(false);
  const assignable = canAssign(role);

  const acceptAndAssign = () => action.run(async () => {
    if (c.status === "New") await ok(api.POST("/complaints/{id}/transition", { params: { path: { id: c.id } }, body: { to: "UnderReview", reason: null, mergedIntoId: null } }));
    await ok(api.POST("/complaints/{id}/triage", { params: { path: { id: c.id } }, body: { acceptAi: false, categoryId: categoryId || null, priority, locationId: null, locationText: null } }));
    await ok(api.POST("/complaints/{id}/jobs", { params: { path: { id: c.id } }, body: { instructions: null, assignedTeamId: teamId || null } }));
    onDone();
  });

  const reviewOnly = () => action.run(async () => {
    if (c.status === "New") await ok(api.POST("/complaints/{id}/transition", { params: { path: { id: c.id } }, body: { to: "UnderReview", reason: null, mergedIntoId: null } }));
    await ok(api.POST("/complaints/{id}/triage", { params: { path: { id: c.id } }, body: { acceptAi: false, categoryId: categoryId || null, priority, locationId: null, locationText: null } }));
    onDone();
  });

  const reject = (reason: string) => action.run(async () => {
    await ok(api.POST("/complaints/{id}/transition", { params: { path: { id: c.id } }, body: { to: "Rejected", reason, mergedIntoId: null } }));
    setRejecting(false);
    onDone();
  });

  return (
    <li>
      <Card>
        <div className="queue__head">
          <Link to={`/complaints/${c.id}`}><strong>{c.reference}</strong></Link>
          <ComplaintStatusChip status={c.status} />
          <PriorityTag priority={c.priority} />
          <span className="muted">{f.dateTime(c.createdAt)}</span>
        </div>
        <p>{c.description}</p>
        {c.aiSummary && (
          <p className="ai-note">
            <strong>{t("ai.title")}:</strong> {c.aiSummary}
            {c.aiConfidence != null && <span className="muted"> ({Math.round(c.aiConfidence * 100)}%)</span>}
          </p>
        )}
        <div className="queue__fields">
          <SelectField label={t("category")} placeholder={t("choose")} value={categoryId} onChange={(e) => setCategoryId(e.target.value)}
            options={lookups.categories.filter((x) => x.isActive).map((x) => ({ value: x.id, label: lookups.categoryName(x.id) }))} />
          <SelectField label={t("priority")} value={priority} onChange={(e) => setPriority(e.target.value)}
            options={PRIORITIES.map((p) => ({ value: p, label: t(`priority.${p}` as Key) }))} />
          {assignable && (
            <SelectField label={t("team")} placeholder={t("review.noTeam")} value={teamId} onChange={(e) => setTeamId(e.target.value)}
              options={lookups.teams.filter((x) => x.isActive).map((x) => ({ value: x.id, label: x.name }))} />
          )}
        </div>
        <ErrorNote error={action.error} />
        <div className="row wrap">
          {assignable
            ? <Button onClick={() => void acceptAndAssign()} loading={action.busy} disabled={!categoryId}>{t("review.acceptAssign")}</Button>
            : <Button onClick={() => void reviewOnly()} loading={action.busy} disabled={!categoryId}>{t("review.markReviewed")}</Button>}
          <Button variant="danger" onClick={() => { action.clear(); setRejecting(true); }}>{t("move.Rejected")}</Button>
        </div>
        <ReasonDialog open={rejecting} title={t("move.Rejected")} busy={action.busy} error={action.error} onCancel={() => setRejecting(false)} onConfirm={(r) => void reject(r)} />
      </Card>
    </li>
  );
}
