import { Alert, Button, Card, SelectField, TextAreaField } from "@urbanfix/ui";
import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api, ok, upload, type S } from "../api";
import { Async, ErrorNote, JobStatusChip, PageHeader, ReasonDialog, usePeople } from "../components";
import { CostEditor, toDraft, toInput } from "../CostEditor";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { useLookups } from "../lookups";
import { canAssign } from "../roles";
import { ApprovalNote, ChargeableCard, ComparisonCard, MaterialsCard } from "./JobExtras";
import { useSession } from "../session";

const ASSIGNABLE = ["Open", "Accepted", "InProgress"];

/** One job: assignment, progress, evidence side by side, sign-off, estimate and actual costs (WP-06, WP-07, WP-08). */
export function JobDetailPage() {
  const { id = "" } = useParams();
  const t = useT();
  const f = useFormat();
  const { role, me } = useSession();
  const { nameOf } = usePeople();
  const { teamName } = useLookups();
  // The server records who a job went to as "user:<id>" / "team:<id>"; show names instead.
  const noteText = (note: string) => (note.startsWith("user:") ? nameOf(note.slice(5)) : note.startsWith("team:") ? teamName(note.slice(5)) : note);
  const detail = useLoad(() => ok(api.GET("/jobs/{id}", { params: { path: { id } } })), [id]);
  const items = useLoad(() => ok(api.GET("/price-items", {})).catch(() => []), []);

  return (
    <Async state={detail}>
      {(d) => {
        const job = d.job;
        const mine = job.assignedUserId === me?.userId && role === "Technician";
        const supervising = canAssign(role);
        const priced = items.data ?? [];
        const actuals = d.actuals as S["CostLineDto"][];
        // The cost editors keep their own lines once mounted (so "Saved" stays visible); only a change of job status remounts them.
        const refresh = detail.reload;
        return (
          <>
            <PageHeader title={job.number} actions={<JobStatusChip status={job.status} />} />
            <p><Link to={`/complaints/${job.complaintId}`}>{t("job.openComplaint")}</Link></p>
            {job.instructions && <Alert>{job.instructions}</Alert>}

            <div className="grid-2">
              <div className="stack">
                {supervising && ASSIGNABLE.includes(job.status) && <AssignCard job={job} onDone={refresh} />}
                {mine && <TechnicianCard job={job} onDone={refresh} />}
                {supervising && job.status === "PendingVerification" && <ReviewCard job={job} note={d.completionNote} onDone={refresh} />}
                {d.checklist.length > 0 && (
                  <Card title={t("job.checklist")}>
                    <ul className="checklist">{d.checklist.map((c, i) => <li key={i}><span aria-hidden="true">{c.done ? "☑" : "☐"}</span> {c.label}<span className="uf-sr-only"> {c.done ? t("job.done") : t("job.notDone")}</span></li>)}</ul>
                  </Card>
                )}
                <EvidenceCard job={job} evidence={d.evidence} canAdd={mine || supervising} onDone={refresh} />
                {supervising && ["PendingVerification", "Completed", "Returned"].includes(job.status) && <ComparisonCard job={job} />}
                {supervising && <ChargeableCard job={job} onDone={refresh} />}
              </div>

              <div className="stack">
                <Card title={t("job.estimate")}>
                  <div className="stack">
                    {d.estimate && <ApprovalNote estimate={d.estimate} />}
                    {d.estimate ? <p className="muted">{t("job.estimateVersion", { version: d.estimate.version, who: nameOf(d.estimate.createdBy) })}{d.estimate.note ? ` — ${d.estimate.note}` : ""}</p> : <p className="muted">{t("job.noEstimate")}</p>}
                    <CostEditor key={`est-${job.status}`} heading={t("job.estimate")} items={priced} saveLabel={t("job.saveEstimate")} note
                      readOnly={!(["Open", "Accepted"].includes(job.status) && (mine || supervising))}
                      initial={(d.estimate?.lines ?? []).map(toDraft)}
                      onSave={async (lines, note) => { await ok(api.POST("/jobs/{id}/estimates", { params: { path: { id } }, body: { note: note || null, lines: lines.map(toInput) } })); refresh(); }} />
                  </div>
                </Card>

                <Card title={t("job.actuals")}>
                  <div className="stack">
                    {job.requiresCosts && <p className="muted">{t("job.costsRequired")}</p>}
                    <CostEditor key={`act-${job.status}`} heading={t("job.actuals")} items={priced} saveLabel={t("job.saveActuals")}
                      readOnly={!((mine && job.status === "InProgress") || (supervising && ["InProgress", "PendingVerification"].includes(job.status)))}
                      initial={actuals.map(toDraft)}
                      onSave={async (lines) => { await ok(api.PUT("/jobs/{id}/costs", { params: { path: { id } }, body: { lines: lines.map(toInput) } })); refresh(); }} />
                    <Variance v={d.variance} />
                  </div>
                </Card>

                <MaterialsCard job={job} canRequest={mine} />

                <Card title={t("job.timeline")}>
                  <ol className="timeline">
                    {d.timeline.map((u) => (
                      <li key={u.id}>
                        <strong>{t(`update.${u.kind}` as Key)}{u.toStatus ? ` → ${t(`jobStatus.${u.toStatus}` as Key)}` : ""}</strong>
                        <span className="muted">{f.dateTime(u.createdAt)} · {nameOf(u.authorId)}</span>
                        {u.note && <span>{noteText(u.note)}</span>}
                        {u.latitude != null && u.longitude != null && <span className="muted" dir="ltr">{u.latitude.toFixed(5)}, {u.longitude.toFixed(5)}</span>}
                      </li>
                    ))}
                  </ol>
                </Card>
              </div>
            </div>
          </>
        );
      }}
    </Async>
  );
}

function Variance({ v }: { v: S["VarianceDto"] }) {
  const t = useT();
  const f = useFormat();
  if (v.estimated == null && v.actual === 0) return null;
  const over = (v.difference ?? 0) > 0;
  return (
    <dl className="facts" aria-label={t("job.variance")}>
      <dt>{t("job.estimated")}</dt><dd>{v.estimated == null ? "—" : f.money(v.estimated)}</dd>
      <dt>{t("job.actual")}</dt><dd>{f.money(v.actual)}</dd>
      <dt>{t("job.difference")}</dt>
      <dd className={over ? "over" : ""}>
        {v.difference == null ? "—" : `${over ? "+" : ""}${f.money(v.difference)}${v.percent == null ? "" : ` (${over ? "+" : ""}${f.number(v.percent)}%)`}`}
        {over && <span> — {t("job.overrun")}</span>}
      </dd>
    </dl>
  );
}

function AssignCard({ job, onDone }: { job: S["JobDto"]; onDone: () => void }) {
  const t = useT();
  const { teams } = useLookups();
  const { list } = usePeople();
  const action = useAction();
  const [teamId, setTeamId] = useState(job.assignedTeamId ?? "");
  const [userId, setUserId] = useState(job.assignedUserId ?? "");
  const technicians = list.filter((p) => p.role === "Technician" && (!teamId || p.teamIds.includes(teamId)));
  const submit = () => action.run(async () => {
    await ok(api.POST("/jobs/{id}/assign", { params: { path: { id: job.id } }, body: { teamId: teamId || null, userId: userId || null } }));
    onDone();
  });
  return (
    <Card title={t("job.assign")}>
      <form className="stack" onSubmit={(e) => { e.preventDefault(); void submit(); }}>
        <SelectField label={t("team")} placeholder={t("choose")} value={teamId} onChange={(e) => { setTeamId(e.target.value); setUserId(""); }}
          options={teams.filter((x) => x.isActive).map((x) => ({ value: x.id, label: x.name }))} />
        <SelectField label={t("job.technician")} placeholder={t("choose")} value={userId} onChange={(e) => setUserId(e.target.value)}
          options={technicians.map((p) => ({ value: p.userId, label: p.displayName }))} />
        <ErrorNote error={action.error} />
        <Button type="submit" loading={action.busy} disabled={!teamId && !userId}>{job.assignedUserId ? t("job.reassign") : t("job.assign")}</Button>
      </form>
    </Card>
  );
}

function TechnicianCard({ job, onDone }: { job: S["JobDto"]; onDone: () => void }) {
  const t = useT();
  const action = useAction();
  const [note, setNote] = useState("");
  const [dialog, setDialog] = useState<"cannot" | undefined>();
  const call = (fn: () => Promise<unknown>) => action.run(async () => { await fn(); setNote(""); onDone(); });
  const path = { params: { path: { id: job.id } } };

  return (
    <Card title={t("job.work")}>
      <div className="stack">
        {job.status === "Open" && <Button onClick={() => void call(() => ok(api.POST("/jobs/{id}/accept", path)))} loading={action.busy}>{t("job.accept")}</Button>}
        {job.status === "Accepted" && <Button onClick={() => void call(() => ok(api.POST("/jobs/{id}/start", { ...path, body: { note: null, latitude: null, longitude: null } })))} loading={action.busy}>{t("job.start")}</Button>}
        {job.status === "InProgress" && (
          <>
            <TextAreaField label={t("job.note")} value={note} onChange={(e) => setNote(e.target.value)} rows={2} />
            <div className="row wrap">
              <Button variant="secondary" disabled={!note.trim()} loading={action.busy}
                onClick={() => void call(() => ok(api.POST("/jobs/{id}/updates", { ...path, body: { note, latitude: null, longitude: null, checklistDone: null } })))}>{t("job.addNote")}</Button>
              <Button loading={action.busy} onClick={() => void call(() => ok(api.POST("/jobs/{id}/complete", { ...path, body: { note: note || null } })))}>{t("job.complete")}</Button>
              <Button variant="danger" onClick={() => { action.clear(); setDialog("cannot"); }}>{t("job.cannot")}</Button>
            </div>
          </>
        )}
        {job.status === "Accepted" && <Button variant="danger" onClick={() => { action.clear(); setDialog("cannot"); }}>{t("job.cannot")}</Button>}
        <ErrorNote error={action.error} />
        <MissingEvidence error={action.error} />
        <ReasonDialog open={dialog === "cannot"} title={t("job.cannot")} busy={action.busy} error={action.error} onCancel={() => setDialog(undefined)}
          onConfirm={(reason) => void call(async () => { await ok(api.POST("/jobs/{id}/cannot-complete", { ...path, body: { reason } })); setDialog(undefined); })} />
      </div>
    </Card>
  );
}

/** Lists what is still missing when a job cannot be completed yet (JB-05). */
function MissingEvidence({ error }: { error: { code: string; body?: unknown } | undefined }) {
  const t = useT();
  const missing = error?.code === "evidence_missing" ? (error.body as { missing?: string[] } | undefined)?.missing : undefined;
  if (!missing?.length) return null;
  return <Alert tone="warning"><ul>{missing.map((m) => <li key={m}>{t(`missing.${m}` as Key)}</li>)}</ul></Alert>;
}

function ReviewCard({ job, note, onDone }: { job: S["JobDto"]; note: string | null; onDone: () => void }) {
  const t = useT();
  const action = useAction();
  const [returning, setReturning] = useState(false);
  const path = { params: { path: { id: job.id } } };
  return (
    <Card title={t("job.review")}>
      <div className="stack">
        {note && <p>{note}</p>}
        <div className="row wrap">
          <Button loading={action.busy} onClick={() => void action.run(async () => { await ok(api.POST("/jobs/{id}/sign-off", path)); onDone(); })}>{t("job.signOff")}</Button>
          <Button variant="danger" onClick={() => { action.clear(); setReturning(true); }}>{t("job.return")}</Button>
        </div>
        <ErrorNote error={action.error} />
        <ReasonDialog open={returning} title={t("job.return")} busy={action.busy} error={action.error} onCancel={() => setReturning(false)}
          onConfirm={(reason) => void action.run(async () => { await ok(api.POST("/jobs/{id}/return", { ...path, body: { reason } })); setReturning(false); onDone(); })} />
      </div>
    </Card>
  );
}

function EvidenceCard({ job, evidence, canAdd, onDone }: { job: S["JobDto"]; evidence: S["EvidenceMediaDto"][]; canAdd: boolean; onDone: () => void }) {
  const t = useT();
  const action = useAction();
  const [stage, setStage] = useState("before");
  const open = ["Accepted", "InProgress", "PendingVerification"].includes(job.status);
  const by = (s: string) => evidence.filter((e) => e.stage === s);
  const Column = ({ stageKey }: { stageKey: string }) => (
    <section aria-label={t(`stage.${stageKey}` as Key)}>
      <h3>{t(`stage.${stageKey}` as Key)}</h3>
      {by(stageKey).length === 0 ? <p className="muted">{t("job.noPhotos")}</p> : (
        <ul className="gallery">{by(stageKey).map((e) => <li key={e.id}><a href={e.url} target="_blank" rel="noreferrer"><img src={e.thumbUrl ?? e.url} alt={t(`stage.${stageKey}` as Key)} loading="lazy" /></a></li>)}</ul>
      )}
    </section>
  );
  return (
    <Card title={t("job.evidence")}>
      <div className="stack">
        <div className="before-after"><Column stageKey="before" /><Column stageKey="after" /></div>
        {by("progress").length > 0 && <Column stageKey="progress" />}
        {canAdd && open && (
          <form className="stack" onSubmit={(e) => e.preventDefault()}>
            <SelectField label={t("job.stage")} value={stage} onChange={(e) => setStage(e.target.value)}
              options={["before", "after", "progress"].map((s) => ({ value: s, label: t(`stage.${s}` as Key) }))} />
            <label className="stack">
              <span className="uf-field__label">{t("job.addPhoto")}</span>
              <input type="file" accept="image/jpeg,image/png,image/webp" disabled={action.busy}
                onChange={(e) => { const file = e.target.files?.[0]; if (file) void action.run(async () => { await upload(`/jobs/${job.id}/media?stage=${stage}`, file); onDone(); }); e.target.value = ""; }} />
            </label>
            <ErrorNote error={action.error} />
          </form>
        )}
      </div>
    </Card>
  );
}
