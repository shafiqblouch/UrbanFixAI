import { Button, Card, EmptyState } from "@urbanfix/ui";
import { useState } from "react";
import { Link } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, ErrorNote, ReasonDialog } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";

/**
 * Possible duplicates of one complaint, side by side, for a supervisor to group or dismiss; and the incident the complaint belongs to
 * (WP-10, DP-01 to DP-05). Nothing is ever merged without a person saying so.
 */
export function DuplicatesCard({ complaint, onChanged }: { complaint: S["ComplaintDto"]; onChanged: () => void }) {
  const t = useT();
  const f = useFormat();
  const suggestions = useLoad(() => ok(api.GET("/complaints/{id}/duplicates", { params: { path: { id: complaint.id } } })), [complaint.id, complaint.incidentId]);
  const incident = useLoad(() => (complaint.incidentId ? ok(api.GET("/incidents/{id}", { params: { path: { id: complaint.incidentId } } })) : Promise.resolve(undefined)), [complaint.incidentId]);
  const action = useAction();
  const [dismissing, setDismissing] = useState<S["SuggestionDto2"]>();

  const changed = () => { suggestions.reload(); incident.reload(); onChanged(); };
  const check = () => action.run(async () => { await ok(api.POST("/complaints/{id}/duplicate-check", { params: { path: { id: complaint.id } } })); suggestions.reload(); });
  const group = (s: S["SuggestionDto2"]) => action.run(async () => {
    await ok(api.POST("/duplicate-suggestions/{id}/decision", { params: { path: { id: s.id } }, body: { accept: true, note: null } }));
    changed();
  });
  const dismiss = (s: S["SuggestionDto2"], note: string) => action.run(async () => {
    await api.POST("/duplicate-suggestions/{id}/decision", { params: { path: { id: s.id } }, body: { accept: false, note } });
    setDismissing(undefined);
    changed();
  });
  const remove = (incidentId: string, complaintId: string) => action.run(async () => {
    await api.DELETE("/incidents/{id}/complaints/{complaintId}", { params: { path: { id: incidentId, complaintId } } });
    changed();
  });

  return (
    <Card title={t("duplicates.title")} actions={<Button variant="secondary" onClick={() => void check()} loading={action.busy}>{t("duplicates.check")}</Button>}>
      <ErrorNote error={action.error} />
      {incident.data && (
        <div className="stack">
          <p><strong>{t("duplicates.incident", { title: incident.data.title })}</strong> <span className="muted">({t(`duplicates.incidentStatus.${incident.data.status}` as Key)})</span></p>
          <ul className="timeline">
            {(incident.data.complaints as S["IncidentComplaint"][]).map((c) => (
              <li key={c.id}>
                <span>
                  {c.id === complaint.id ? <strong>{c.reference}</strong> : <Link to={`/complaints/${c.id}`}>{c.reference}</Link>}
                  {" · "}{t(`complaintStatus.${c.status}` as Key)}{c.isPrimary && ` · ${t("duplicates.primary")}`}
                </span>
                {c.id !== complaint.id && <Button variant="ghost" onClick={() => void remove(incident.data!.id, c.id)}>{t("duplicates.remove")}</Button>}
              </li>
            ))}
          </ul>
          <p className="muted">{t("duplicates.incidentNote")}</p>
        </div>
      )}
      <Async state={suggestions}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("duplicates.none")} /> : (
          <ul className="queue">
            {rows.map((s) => (
              <li key={s.id}>
                <div className="before-after">
                  <div>
                    <strong>{complaint.reference}</strong>
                    <p>{complaint.description}</p>
                    <small className="muted">{f.dateTime(complaint.createdAt)}</small>
                  </div>
                  <div>
                    <Link to={`/complaints/${s.complaintId}`}><strong>{s.reference}</strong></Link>
                    <p>{s.description}</p>
                    <small className="muted">{f.dateTime(s.createdAt)}</small>
                  </div>
                </div>
                <p className="ai-note">
                  <strong>{t("duplicates.likely", { percent: Math.round(s.score * 100) })}</strong> {s.reason}
                  {s.distanceMetres != null && <span className="muted"> · {t("duplicates.metres", { metres: Math.round(s.distanceMetres) })}</span>}
                </p>
                <div className="row wrap">
                  <Button onClick={() => void group(s)} loading={action.busy}>{t("duplicates.group")}</Button>
                  <Button variant="ghost" onClick={() => { action.clear(); setDismissing(s); }}>{t("duplicates.dismiss")}</Button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </Async>
      <ReasonDialog open={dismissing !== undefined} required={false} title={t("duplicates.dismiss")} busy={action.busy} error={action.error}
        onCancel={() => setDismissing(undefined)} onConfirm={(note) => dismissing && void dismiss(dismissing, note)}>
        <p className="muted">{t("duplicates.dismissNote")}</p>
      </ReasonDialog>
    </Card>
  );
}
