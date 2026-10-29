import { Button, EmptyState } from "@urbanfix/ui";
import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, ComplaintStatusChip, ErrorNote, PriorityTag, ReasonDialog } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { Icon } from "../icons";
import { useLookups } from "../lookups";
import { canAssign } from "../roles";
import { useSession } from "../session";

const PRIORITIES = ["Low", "Normal", "High", "Urgent"] as const;
/** Suggestions at or above this confidence can be accepted in one go. */
const HIGH_CONFIDENCE = 0.95;

type Item = S["QueueItemDto"];
interface Edit { categoryId: string; priority: string; teamId: string }

const defaults = (item: Item): Edit => ({
  categoryId: item.suggestedCategoryId ?? item.complaint.categoryId ?? "",
  priority: item.suggestedCategoryId && !item.complaint.categoryId ? item.suggestedPriority ?? item.complaint.priority : item.complaint.priority,
  teamId: item.suggestedTeamId ?? "",
});

/**
 * Supervisor queue with the AI's suggestions pre-filled (WP-04). Pick a complaint on the left, adjust its category, priority and crew
 * (on the card or in the inspector), then accept: it is reviewed, triaged and, for those who may assign, a job is raised for the team.
 */
export function ReviewQueuePage() {
  const queue = useLoad(() => ok(api.GET("/review-queue", { params: { query: { limit: 100 } } })), []);
  return <Async state={queue}>{(items) => <Workspace items={items} onDone={queue.reload} />}</Async>;
}

function Workspace({ items, onDone }: { items: Item[]; onDone: () => void }) {
  const t = useT();
  const f = useFormat();
  const { role } = useSession();
  const assignable = canAssign(role);
  const action = useAction();

  const [edits, setEdits] = useState<Record<string, Edit>>({});
  const [selectedId, setSelectedId] = useState<string | undefined>(items[0]?.complaint.id);
  const [view, setView] = useState<"split" | "stream">("split");
  const [q, setQ] = useState("");
  const [prio, setPrio] = useState("");
  const [rejectId, setRejectId] = useState<string>();
  const [toast, setToast] = useState<string>();

  const editOf = (item: Item): Edit => edits[item.complaint.id] ?? defaults(item);
  const change = (item: Item, patch: Partial<Edit>) => setEdits((e) => ({ ...e, [item.complaint.id]: { ...editOf(item), ...patch } }));

  const shown = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return items.filter((i) => (!prio || i.complaint.priority === prio)
      && (!needle || `${i.complaint.reference} ${i.complaint.description}`.toLowerCase().includes(needle)));
  }, [items, q, prio]);

  const selected = shown.find((i) => i.complaint.id === selectedId) ?? shown[0];
  const confident = items.filter((i) => i.suggestedCategoryId && (i.complaint.aiConfidence ?? 0) >= HIGH_CONFIDENCE);

  useEffect(() => {
    if (!toast) return;
    const id = window.setTimeout(() => setToast(undefined), 3500);
    return () => window.clearTimeout(id);
  }, [toast]);

  const process = async (item: Item) => {
    const c = item.complaint;
    const e = editOf(item);
    if (c.status === "New") await ok(api.POST("/complaints/{id}/transition", { params: { path: { id: c.id } }, body: { to: "UnderReview", reason: null, mergedIntoId: null } }));
    await ok(api.POST("/complaints/{id}/triage", { params: { path: { id: c.id } }, body: { acceptAi: false, categoryId: e.categoryId || null, priority: e.priority, locationId: null, locationText: null } }));
    if (assignable) await ok(api.POST("/complaints/{id}/jobs", { params: { path: { id: c.id } }, body: { instructions: null, assignedTeamId: e.teamId || null } }));
  };

  const accept = (item: Item) => action.run(async () => {
    await process(item);
    setToast(t("review.accepted", { ref: item.complaint.reference }));
    onDone();
  });

  const acceptConfident = () => action.run(async () => {
    for (const item of confident) await process(item);
    setToast(t("review.batchDone", { count: f.number(confident.length) }));
    onDone();
  });

  const reject = (reason: string) => action.run(async () => {
    if (!rejectId) return;
    await ok(api.POST("/complaints/{id}/transition", { params: { path: { id: rejectId } }, body: { to: "Rejected", reason, mergedIntoId: null } }));
    setRejectId(undefined);
    onDone();
  });

  // A = accept, R = reject, up and down arrows move through the queue (ignored while typing or while a dialog is open).
  useEffect(() => {
    const onKey = (ev: KeyboardEvent) => {
      const el = document.activeElement as HTMLElement | null;
      if (ev.ctrlKey || ev.metaKey || ev.altKey || rejectId || (el && ["INPUT", "TEXTAREA", "SELECT"].includes(el.tagName))) return;
      if (!selected) return;
      const at = shown.findIndex((i) => i.complaint.id === selected.complaint.id);
      if (ev.key === "ArrowDown" || ev.key === "ArrowUp") {
        ev.preventDefault();
        const next = shown[Math.min(shown.length - 1, Math.max(0, at + (ev.key === "ArrowDown" ? 1 : -1)))];
        if (next) setSelectedId(next.complaint.id);
      } else if (ev.key.toLowerCase() === "a" && editOf(selected).categoryId && !action.busy) void accept(selected);
      else if (ev.key.toLowerCase() === "r") { action.clear(); setRejectId(selected.complaint.id); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  const rejecting = items.find((i) => i.complaint.id === rejectId);

  return (
    <div className="rq">
      <div className="rq-head">
        <div className="rq-head__title">
          <span className="rq-head__icon"><Icon name="review" size={16} /></span>
          <div>
            <h1 className="rq-title">{t("review.title")}</h1>
            <p className="rq-sub">{t("review.sub")}</p>
          </div>
        </div>
        <div className="rq-keys" aria-label={t("review.hotkeys")}>
          <Icon name="bolt" size={13} />
          <strong>{t("review.hotkeys")}</strong>
          <span><kbd>A</kbd> {t("review.hkAccept")}</span>
          <span><kbd className="rq-kbd--bad">R</kbd> {t("review.hkReject")}</span>
          <span><kbd>↑</kbd><kbd>↓</kbd> {t("review.hkSelect")}</span>
        </div>
      </div>

      <div className="rq-bar">
        <div className="rq-bar__filters">
          <label className="rq-search">
            <Icon name="search" size={14} />
            <input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder={t("review.searchPh")} aria-label={t("review.searchPh")} />
          </label>
          <select className="rq-select" value={prio} onChange={(e) => setPrio(e.target.value)} aria-label={t("priority")}>
            <option value="">{t("review.allPriorities")}</option>
            {PRIORITIES.map((p) => <option key={p} value={p}>{t(`priority.${p}` as Key)}</option>)}
          </select>
        </div>
        <div className="rq-bar__actions">
          <button type="button" className="rq-batch" disabled={confident.length === 0 || action.busy} onClick={() => void acceptConfident()}>
            <Icon name="bolt" size={14} /> {t("review.acceptConfident", { count: f.number(confident.length) })}
          </button>
          <span className="rq-bar__sep" />
          <div className="rq-toggle" role="group" aria-label={t("review.view")}>
            <button type="button" className={view === "split" ? "rq-toggle__on" : ""} aria-pressed={view === "split"} onClick={() => setView("split")}>
              <Icon name="columns" size={14} /> <span>{t("review.split")}</span>
            </button>
            <button type="button" className={view === "stream" ? "rq-toggle__on" : ""} aria-pressed={view === "stream"} onClick={() => setView("stream")}>
              <Icon name="list" size={14} /> <span>{t("review.stream")}</span>
            </button>
          </div>
        </div>
      </div>

      {toast && <div className="rq-toast" role="status"><Icon name="check" size={14} /> {toast}</div>}
      <ErrorNote error={action.error} />

      <div className={`rq-body${view === "stream" ? " rq-body--stream" : ""}`}>
        <section className="rq-list" aria-label={t("menu.review")}>
          <div className="rq-list__count">
            <strong>{t("review.showing", { count: f.number(shown.length) })}</strong>
            <span><Icon name="config" size={12} /> {t("review.inline")}</span>
          </div>
          <div className="rq-cards">
            {shown.length === 0 ? (
              <EmptyState title={items.length === 0 ? t("review.empty") : t("review.noMatch")} />
            ) : shown.map((item) => (
              <Card key={item.complaint.id} item={item} edit={editOf(item)} change={(p) => change(item, p)} assignable={assignable}
                selected={view === "split" && selected?.complaint.id === item.complaint.id} busy={action.busy}
                onSelect={() => setSelectedId(item.complaint.id)} onAccept={() => void accept(item)}
                onReject={() => { action.clear(); setRejectId(item.complaint.id); }} />
            ))}
          </div>
        </section>

        {view === "split" && (
          <aside className="rq-inspector">
            {selected
              ? <Inspector item={selected} edit={editOf(selected)} change={(p) => change(selected, p)} assignable={assignable} busy={action.busy}
                  onAccept={() => void accept(selected)} onReject={() => { action.clear(); setRejectId(selected.complaint.id); }} />
              : <div className="rq-empty"><Icon name="inbox" size={34} /><p>{t("review.selectOne")}</p></div>}
          </aside>
        )}
      </div>

      <ReasonDialog open={!!rejecting} title={t("move.Rejected")} busy={action.busy} error={action.error}
        onCancel={() => setRejectId(undefined)} onConfirm={(r) => void reject(r)}>
        {rejecting && <p className="muted">{rejecting.complaint.reference}</p>}
      </ReasonDialog>
    </div>
  );
}

function Fields({ item, edit, change, assignable, wide }: { item: Item; edit: Edit; change: (p: Partial<Edit>) => void; assignable: boolean; wide?: boolean }) {
  const t = useT();
  const lookups = useLookups();
  const id = item.complaint.id + (wide ? "-i" : "-c");
  return (
    <div className={wide ? "rq-fields rq-fields--wide" : "rq-fields"} onClick={(e) => e.stopPropagation()}>
      <label className="rq-field" htmlFor={`${id}-cat`}>
        <span>{t("category")}</span>
        <select id={`${id}-cat`} value={edit.categoryId} onChange={(e) => change({ categoryId: e.target.value })}>
          <option value="">{t("choose")}</option>
          {lookups.categories.filter((x) => x.isActive).map((x) => <option key={x.id} value={x.id}>{lookups.categoryName(x.id)}</option>)}
        </select>
      </label>
      {wide ? (
        <div className="rq-field">
          <span>{t("review.urgency")}</span>
          <div className="rq-prio">
            {PRIORITIES.map((p) => (
              <button key={p} type="button" className={`rq-prio__btn rq-prio__btn--${p.toLowerCase()}${edit.priority === p ? " rq-prio__btn--on" : ""}`}
                aria-pressed={edit.priority === p} onClick={() => change({ priority: p })}>{t(`priority.${p}` as Key)}</button>
            ))}
          </div>
        </div>
      ) : (
        <label className="rq-field" htmlFor={`${id}-pri`}>
          <span>{t("priority")}</span>
          <select id={`${id}-pri`} value={edit.priority} onChange={(e) => change({ priority: e.target.value })}>
            {PRIORITIES.map((p) => <option key={p} value={p}>{t(`priority.${p}` as Key)}</option>)}
          </select>
        </label>
      )}
      {assignable && (
        <label className="rq-field" htmlFor={`${id}-team`}>
          <span>{wide ? t("review.dispatchCrew") : t("review.assignCrew")}</span>
          <select id={`${id}-team`} value={edit.teamId} onChange={(e) => change({ teamId: e.target.value })}>
            <option value="">{t("review.noTeam")}</option>
            {lookups.teams.filter((x) => x.isActive).map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
          </select>
        </label>
      )}
    </div>
  );
}

function Card({ item, edit, change, assignable, selected, busy, onSelect, onAccept, onReject }: {
  item: Item; edit: Edit; change: (p: Partial<Edit>) => void; assignable: boolean; selected: boolean; busy: boolean;
  onSelect: () => void; onAccept: () => void; onReject: () => void;
}) {
  const t = useT();
  const f = useFormat();
  const c = item.complaint;
  return (
    <article className={`rq-card${selected ? " rq-card--on" : ""}`} onClick={onSelect} aria-current={selected || undefined}>
      <div className="rq-card__top">
        <Link to={`/complaints/${c.id}`} className="rq-ref" onClick={(e) => e.stopPropagation()}>{c.reference}</Link>
        <ComplaintStatusChip status={c.status} />
        <PriorityTag priority={c.priority} />
        {c.aiConfidence != null && <span className="rq-ai"><Icon name="bolt" size={12} /> {t("review.aiMatch", { pct: f.number(Math.round(c.aiConfidence * 100)) })}</span>}
        <span className="rq-card__time">{f.dateTime(c.createdAt)}</span>
      </div>
      <p className="rq-card__text">{c.description}</p>
      <Fields item={item} edit={edit} change={change} assignable={assignable} />
      <div className="rq-card__foot">
        <span className="rq-channel"><Icon name="phone" size={12} /> {t(`channel.${c.channel}` as Key)}</span>
        <div className="rq-card__btns" onClick={(e) => e.stopPropagation()}>
          <button type="button" className="rq-btn rq-btn--bad" onClick={onReject}>{t("move.Rejected")}</button>
          <button type="button" className="rq-btn rq-btn--good" disabled={!edit.categoryId || busy} onClick={onAccept}>
            {assignable ? t("review.acceptAssign") : t("review.markReviewed")}
          </button>
        </div>
      </div>
    </article>
  );
}

function Inspector({ item, edit, change, assignable, busy, onAccept, onReject }: {
  item: Item; edit: Edit; change: (p: Partial<Edit>) => void; assignable: boolean; busy: boolean; onAccept: () => void; onReject: () => void;
}) {
  const t = useT();
  const f = useFormat();
  const lookups = useLookups();
  const c = item.complaint;
  const place = c.locationId ? lookups.locations.find((l) => l.id === c.locationId)?.name : undefined;
  const teamName = edit.teamId ? lookups.teamName(edit.teamId) : undefined;
  return (
    <>
      <header className="rq-ins__head">
        <div className="rq-ins__id">
          <Link to={`/complaints/${c.id}`}>{c.reference}</Link>
          <PriorityTag priority={edit.priority} />
        </div>
        <span className="rq-ins__time">{t("review.recorded", { time: f.dateTime(c.createdAt) })}</span>
      </header>
      <div className="rq-ins__body">
        <div className="rq-aibox">
          <span className="rq-aibox__icon"><Icon name="bolt" size={16} /></span>
          <div>
            <h4>{t("review.aiEngine")}</h4>
            <p>
              {item.suggestedCategoryId
                ? <>{t("review.suggested")}: <strong>{lookups.categoryName(item.suggestedCategoryId)}</strong></>
                : t("review.noSuggestion")}
            </p>
          </div>
          {c.aiConfidence != null && <span className="rq-aibox__pct">{t("review.match", { pct: f.number(Math.round(c.aiConfidence * 100)) })}</span>}
        </div>

        <div className="rq-box">
          <span className="rq-box__label">{t("review.statement")}</span>
          <p className="rq-quote">{c.description}</p>
          {c.aiSummary && <p className="rq-summary"><strong>{t("ai.title")}:</strong> {c.aiSummary}</p>}
          <div className="rq-box__meta">
            <span><Icon name="pin" size={13} /> <strong>{place ?? c.locationText ?? "—"}</strong></span>
            <span><Icon name="phone" size={13} /> {t("review.intakeVia")} <strong>{t(`channel.${c.channel}` as Key)}</strong></span>
          </div>
        </div>

        <div className="rq-box rq-box--edit">
          <div className="rq-box__title">
            <h4><Icon name="config" size={14} /> {t("review.modify")}</h4>
            <span>{t("review.liveEdit")}</span>
          </div>
          <Fields item={item} edit={edit} change={change} assignable={assignable} wide />
        </div>

        <div className="rq-box">
          <span className="rq-box__label">{t("review.status")}</span>
          <div className="rq-ins__chips"><ComplaintStatusChip status={c.status} /></div>
        </div>
      </div>
      <footer className="rq-ins__foot">
        <button type="button" className="rq-btn rq-btn--bad rq-btn--lg" onClick={onReject}><Icon name="close" size={14} /> {t("review.rejectReport")}</button>
        <Button onClick={onAccept} loading={busy} disabled={!edit.categoryId}>
          {assignable ? t("review.dispatchTo", { team: teamName ?? t("review.routing") }) : t("review.markReviewed")}
        </Button>
      </footer>
    </>
  );
}
