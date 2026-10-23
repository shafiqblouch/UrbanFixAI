import { Alert, Button, Card, SelectField, Table, TextAreaField, TextField } from "@urbanfix/ui";
import { useState } from "react";
import { DuplicatesCard } from "./Duplicates";
import { FeedbackCard } from "./Feedback";
import { Link, useParams } from "react-router-dom";
import { ApiError, api, ok, okPaged, type S } from "../api";
import { Async, ComplaintStatusChip, ErrorNote, JobStatusChip, PageHeader, PriorityTag, ReasonDialog, usePeople } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { useLookups } from "../lookups";
import { canAssign, canTriage } from "../roles";
import { useSession } from "../session";

const PRIORITIES = ["Low", "Normal", "High", "Urgent"] as const;

/** Mirrors the server's lifecycle rules for which moves must carry a reason; the server remains the authority. */
const needsReason = (from: S["ComplaintStatus"], to: S["ComplaintStatus"]) =>
  to === "Rejected" || to === "Merged" || to === "OnHold" || (to === "UnderReview" && from !== "New" && from !== "OnHold");

/** One complaint with its timeline, evidence, AI suggestions (every field editable), jobs and audit trail (WP-03, WP-06). */
export function ComplaintDetailPage() {
  const { id = "" } = useParams();
  const t = useT();
  const f = useFormat();
  const { role, me } = useSession();
  const { nameOf } = usePeople();
  const lookups = useLookups();
  const detail = useLoad(() => ok(api.GET("/complaints/{id}", { params: { path: { id } } })), [id]);
  const suggestions = useLoad(() => (canTriage(role) ? ok(api.GET("/complaints/{id}/ai-suggestions", { params: { path: { id } } })) : Promise.resolve([])), [id, role]);
  const audit = useLoad(
    () => (role === "Manager" || role === "Committee" ? ok(api.GET("/audit", { params: { query: { entityType: "Complaint", entityId: id, limit: 50 } } })) : Promise.resolve([])),
    [id, role]);

  const refresh = () => { detail.reload(); suggestions.reload(); audit.reload(); };

  return (
    <Async state={detail}>
      {(d) => {
        const c = d.complaint;
        return (
          <>
            <PageHeader title={c.reference} actions={<ComplaintStatusChip status={c.status} />} />
            <div className="grid-2">
              <div className="stack">
                <Card title={t("complaint.details")}>
                  <dl className="facts">
                    <dt>{t("description")}</dt><dd>{c.description}</dd>
                    <dt>{t("category")}</dt><dd>{lookups.categoryName(c.categoryId)}</dd>
                    <dt>{t("priority")}</dt><dd><PriorityTag priority={c.priority} /></dd>
                    <dt>{t("reporter")}</dt><dd>{nameOf(c.reportedByUserId)}</dd>
                    <dt>{t("channel")}</dt><dd>{t(`channel.${c.channel}` as Key)}</dd>
                    <dt>{t("location")}</dt><dd>{c.locationText ?? "—"}</dd>
                    <dt>{t("created")}</dt><dd>{f.dateTime(c.createdAt)}</dd>
                    {c.statusReason && <><dt>{t("reason")}</dt><dd>{c.statusReason}</dd></>}
                  </dl>
                </Card>

                <FeedbackCard complaint={c} feedback={d.feedback} canGive={c.reportedByUserId === me?.userId} onSaved={refresh} />

                {canTriage(role) && <DuplicatesCard complaint={c} onChanged={refresh} />}

                {d.media.length > 0 && (
                  <Card title={t("complaint.media")}>
                    <ul className="gallery">
                      {d.media.map((m) => (
                        <li key={m.id}>
                          {m.kind === "audio" ? <audio controls src={m.url} aria-label={t("complaint.voice")} /> : (
                            <a href={m.url} target="_blank" rel="noreferrer"><img src={m.thumbUrl ?? m.url} alt={t("complaint.photo")} loading="lazy" /></a>
                          )}
                        </li>
                      ))}
                    </ul>
                  </Card>
                )}

                <Card title={t("complaint.timeline")}>
                  <ol className="timeline">
                    {d.history.map((h, i) => (
                      <li key={i}>
                        <span><ComplaintStatusChip status={h.to} /></span>
                        <span className="muted">{f.dateTime(h.occurredAt)} · {nameOf(h.actorUserId)}</span>
                        {h.reason && <span>{h.reason}</span>}
                      </li>
                    ))}
                  </ol>
                </Card>
              </div>

              <div className="stack">
                {canTriage(role) && <AiCard complaint={c} suggestions={suggestions.data ?? []} onChanged={refresh} />}
                <Actions complaint={c} next={d.allowedNext} onChanged={refresh} />
                <JobsCard complaint={c} jobs={d.jobs} onChanged={refresh} />
                {(audit.data?.length ?? 0) > 0 && (
                  <Card title={t("complaint.audit")}>
                    <Table caption={t("complaint.audit")} rows={audit.data ?? []} rowKey={(a) => a.id}
                      columns={[
                        { key: "when", header: t("when"), render: (a) => f.dateTime(a.occurredAt) },
                        { key: "who", header: t("who"), render: (a) => nameOf(a.actorUserId) },
                        { key: "action", header: t("action"), render: (a) => a.action },
                      ]} />
                  </Card>
                )}
              </div>
            </div>
          </>
        );
      }}
    </Async>
  );
}

function AiCard({ complaint, suggestions, onChanged }: { complaint: S["ComplaintDto"]; suggestions: S["SuggestionDto"][]; onChanged: () => void }) {
  const t = useT();
  const lookups = useLookups();
  const action = useAction();
  const latest = [...suggestions].reverse().find((s) => s.status === "done");
  const failed = suggestions.filter((s) => s.status === "failed").length;
  const [categoryId, setCategoryId] = useState(complaint.categoryId ?? "");
  const [priority, setPriority] = useState(complaint.priority);
  const [locationText, setLocationText] = useState(complaint.locationText ?? "");
  const editable = ["New", "UnderReview", "OnHold"].includes(complaint.status);
  const suggestedCategory = lookups.categories.find((c) => c.key === latest?.categoryKey);

  const save = () => action.run(async () => {
    await ok(api.POST("/complaints/{id}/triage", { params: { path: { id: complaint.id } }, body: { acceptAi: false, categoryId: categoryId || null, priority, locationId: null, locationText } }));
    onChanged();
  });
  const accept = () => action.run(async () => {
    await ok(api.POST("/complaints/{id}/triage", { params: { path: { id: complaint.id } }, body: { acceptAi: true, categoryId: null, priority: null, locationId: null, locationText: null } }));
    onChanged();
  });

  return (
    <Card title={t("ai.title")}>
      <div className="stack">
        {latest ? (
          <dl className="facts">
            <dt>{t("ai.summary")}</dt><dd>{latest.summary}</dd>
            <dt>{t("ai.category")}</dt><dd>{suggestedCategory ? lookups.categoryName(suggestedCategory.id) : latest.categoryKey}</dd>
            <dt>{t("ai.confidence")}</dt><dd>{latest.confidence == null ? "—" : `${Math.round(latest.confidence * 100)}%`}</dd>
            <dt>{t("ai.model")}</dt><dd dir="ltr">{latest.model} · {latest.promptVersion}</dd>
          </dl>
        ) : <p className="muted">{complaint.aiStatus === "failed" ? t("ai.failed") : t("ai.pending")}</p>}
        {failed > 0 && latest && <p className="muted">{t("ai.retries", { count: failed })}</p>}

        {editable && (
          <form className="stack" onSubmit={(e) => { e.preventDefault(); void save(); }}>
            <SelectField label={t("category")} placeholder={t("choose")} value={categoryId} onChange={(e) => setCategoryId(e.target.value)}
              options={lookups.categories.map((c) => ({ value: c.id, label: lookups.categoryName(c.id) }))} />
            <SelectField label={t("priority")} value={priority} onChange={(e) => setPriority(e.target.value)}
              options={PRIORITIES.map((p) => ({ value: p, label: t(`priority.${p}` as Key) }))} />
            <TextField label={t("location")} value={locationText} onChange={(e) => setLocationText(e.target.value)} />
            <ErrorNote error={action.error} />
            <div className="row">
              <Button type="submit" loading={action.busy}>{t("save")}</Button>
              {suggestedCategory && <Button type="button" variant="secondary" onClick={() => void accept()} disabled={action.busy}>{t("ai.accept")}</Button>}
            </div>
          </form>
        )}
      </div>
    </Card>
  );
}

function Actions({ complaint, next, onChanged }: { complaint: S["ComplaintDto"]; next: S["ComplaintStatus"][]; onChanged: () => void }) {
  const t = useT();
  const action = useAction();
  const [target, setTarget] = useState<S["ComplaintStatus"]>();
  const [mergeRef, setMergeRef] = useState("");
  if (next.length === 0) return null;

  async function confirm(reason: string) {
    if (!target) return;
    const done = await action.run(async () => {
      let mergedIntoId: string | null = null;
      if (target === "Merged") {
        const found = await okPaged(api.GET("/complaints", { params: { query: { q: mergeRef.trim(), limit: 5 } } }));
        const match = found.items.find((c) => c.reference.toLowerCase() === mergeRef.trim().toLowerCase());
        if (!match) throw new ApiError(400, "merge_target_required");
        mergedIntoId = match.id;
      }
      await ok(api.POST("/complaints/{id}/transition", { params: { path: { id: complaint.id } }, body: { to: target, reason, mergedIntoId } }));
      return true;
    });
    if (done) { setTarget(undefined); onChanged(); }
  }

  return (
    <Card title={t("complaint.actions")}>
      <div className="row wrap">
        {next.map((s) => (
          <Button key={s} variant={s === "Rejected" ? "danger" : "secondary"} onClick={() => { action.clear(); setTarget(s); }}>{t(`move.${s}` as Key)}</Button>
        ))}
      </div>
      <ReasonDialog open={!!target} title={target ? t(`move.${target}` as Key) : ""} required={!!target && needsReason(complaint.status, target)}
        busy={action.busy} error={action.error} onCancel={() => setTarget(undefined)} onConfirm={(r) => void confirm(r)}>
        {target === "Merged" && <TextField label={t("complaint.mergeInto")} hint={t("complaint.mergeHint")} value={mergeRef} onChange={(e) => setMergeRef(e.target.value)} required />}
      </ReasonDialog>
    </Card>
  );
}

function JobsCard({ complaint, jobs, onChanged }: { complaint: S["ComplaintDto"]; jobs: S["JobDto"][]; onChanged: () => void }) {
  const t = useT();
  const { role } = useSession();
  const { teamName, teams } = useLookups();
  const { nameOf } = usePeople();
  const action = useAction();
  const [instructions, setInstructions] = useState("");
  const [teamId, setTeamId] = useState("");
  const canCreate = canAssign(role) && complaint.status === "UnderReview";
  if (jobs.length === 0 && !canCreate) return null;

  const create = () => action.run(async () => {
    await ok(api.POST("/complaints/{id}/jobs", { params: { path: { id: complaint.id } }, body: { instructions: instructions || null, assignedTeamId: teamId || null } }));
    setInstructions("");
    onChanged();
  });

  return (
    <Card title={t("complaint.jobs")}>
      <div className="stack">
        {jobs.length > 0 && (
          <Table caption={t("complaint.jobs")} rows={jobs} rowKey={(j) => j.id}
            columns={[
              { key: "n", header: t("job"), render: (j) => <Link to={`/jobs/${j.id}`}>{j.number}</Link> },
              { key: "s", header: t("status"), render: (j) => <JobStatusChip status={j.status} /> },
              { key: "t", header: t("team"), render: (j) => teamName(j.assignedTeamId) },
              { key: "u", header: t("assignee"), render: (j) => nameOf(j.assignedUserId) },
            ]} />
        )}
        {canCreate && (
          <form className="stack" onSubmit={(e) => { e.preventDefault(); void create(); }}>
            <TextAreaField label={t("job.instructions")} value={instructions} onChange={(e) => setInstructions(e.target.value)} rows={2} />
            <SelectField label={t("team")} placeholder={t("job.suggestedTeam")} value={teamId} onChange={(e) => setTeamId(e.target.value)}
              options={teams.filter((x) => x.isActive).map((x) => ({ value: x.id, label: x.name }))} />
            <ErrorNote error={action.error} />
            <Button type="submit" loading={action.busy}>{t("job.create")}</Button>
          </form>
        )}
        {complaint.status === "UnderReview" && !canCreate && <Alert>{t("job.waiting")}</Alert>}
      </div>
    </Card>
  );
}
