import { Button, EmptyState } from "@urbanfix/ui";
import { useEffect, useState, type ReactNode } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, download, ok, okPaged, type S } from "../api";
import { Async, ComplaintStatusChip, ErrorNote, PriorityTag, usePeople } from "../components";
import { useAction, useDebounced, useLoad } from "../hooks";
import { Icon } from "../icons";
import { useFormat, useT, type Key } from "../i18n";
import { useLookups } from "../lookups";

const PAGE = 25;
const STATUSES = ["New", "OnHold", "UnderReview", "Assigned", "InProgress", "Resolved", "Closed", "Rejected", "Merged"] as const;
const PRIORITIES = ["Low", "Normal", "High", "Urgent"] as const;
const TONE: Record<(typeof STATUSES)[number], string> = { New: "info", OnHold: "neutral", UnderReview: "info", Assigned: "warn", InProgress: "warn", Resolved: "good", Closed: "good", Rejected: "bad", Merged: "neutral" };

/**
 * The complaints workspace: status pills with live counts, advanced filters, a table that scrolls inside its card and a side inspector (WP-02).
 * The URL holds the filters, so a view can be shared, and the header search box filters this list as you type.
 */
export function ComplaintsPage() {
  const t = useT();
  const f = useFormat();
  const lookups = useLookups();
  const [params, setParams] = useSearchParams();
  const [advanced, setAdvanced] = useState(false);
  const [inspect, setInspect] = useState<string>();
  const get = (k: string) => params.get(k) ?? "";
  const q = get("q");
  const debouncedQ = useDebounced(q);

  const page = Math.max(1, Number(get("page")) || 1);
  const filters = { status: get("status"), categoryId: get("categoryId"), departmentId: get("departmentId"), locationId: get("locationId"), priority: get("priority"), from: get("from"), to: get("to") };

  function set(key: string, value: string) {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value); else next.delete(key);
    if (key !== "page") next.delete("page");
    setParams(next, { replace: true });
  }

  const list = useLoad(() => okPaged(api.GET("/complaints", {
    params: { query: {
      q: debouncedQ || undefined, status: (filters.status || undefined) as never, categoryId: filters.categoryId || undefined, departmentId: filters.departmentId || undefined,
      locationId: filters.locationId || undefined, priority: filters.priority || undefined,
      from: filters.from ? new Date(filters.from).toISOString() : undefined,
      to: filters.to ? new Date(`${filters.to}T23:59:59`).toISOString() : undefined,
      limit: PAGE, offset: (page - 1) * PAGE,
    } },
  })), [debouncedQ, page, ...Object.values(filters)]);

  // Counts for the status pills. Not every role may read the summary, so without it the pills simply show no numbers.
  const summary = useLoad(async () => { try { return await ok(api.GET("/dashboard/summary")); } catch { return undefined; } }, []);
  const high = useLoad(async () => { try { return (await okPaged(api.GET("/complaints", { params: { query: { priority: "High", limit: 1 } } }))).total; } catch { return undefined; } }, []);
  const counts = new Map((summary.data?.byStatus ?? []).map((r) => [r.key, r.count]));
  const grandTotal = summary.data?.total;

  // The same filters the list uses, so the file holds what is on screen (all pages, up to the server's limit).
  const exporting = useAction();
  const exportQuery = {
    q: debouncedQ || undefined, status: filters.status || undefined, categoryId: filters.categoryId || undefined, departmentId: filters.departmentId || undefined,
    locationId: filters.locationId || undefined, priority: filters.priority || undefined,
    from: filters.from ? new Date(filters.from).toISOString() : undefined, to: filters.to ? new Date(`${filters.to}T23:59:59`).toISOString() : undefined,
  };

  const sectors = lookups.locations.filter((l) => l.depth === 0 || l.kind === "sector");
  const placeOf = (c: S["ComplaintDto"]) => lookups.locations.find((l) => l.id === c.locationId)?.name ?? c.locationText ?? "—";
  const anyFilter = debouncedQ !== "" || Object.values(filters).some(Boolean);
  const advancedActive = ["categoryId", "departmentId", "locationId", "from", "to"].some((k) => get(k) !== "");
  // Show a pill for each status that has complaints (and the chosen one); without counts, show them all.
  const pills = STATUSES.filter((s) => !summary.data || (counts.get(s) ?? 0) > 0 || filters.status === s);

  return (
    <div className="cmp">
      <div className="cmp-head">
        <div>
          <h1 className="cmp-title">
            <span>{t("cmp.title")}</span>
            <span className="cmp-badge">{t("cmp.total", { count: f.number(list.data?.total ?? grandTotal ?? 0) })}</span>
          </h1>
          <p className="cmp-sub">{t("cmp.sub")}</p>
        </div>
        <div className="row">
          <Button variant="secondary" loading={exporting.busy} onClick={() => void exporting.run(() => download("/exports/complaints", { ...exportQuery, format: "csv" }))}><Icon name="file" size={15} />{t("complaints.exportCsv")}</Button>
          <Button variant="secondary" loading={exporting.busy} onClick={() => void exporting.run(() => download("/exports/complaints", { ...exportQuery, format: "pdf" }))}><Icon name="file" size={15} />{t("complaints.exportPdf")}</Button>
        </div>
      </div>
      <ErrorNote error={exporting.error} />

      <section className="cmp-filters" role="search" aria-label={t("filters")}>
        <div className="cmp-pills">
          <span className="cmp-pills__label">{t("cmp.statusLabel")}</span>
          <button type="button" className={`cmp-pill cmp-pill--all${filters.status === "" ? " cmp-pill--on" : ""}`} aria-pressed={filters.status === ""} onClick={() => set("status", "")}>
            {t("cmp.all")}{grandTotal !== undefined && ` (${f.number(grandTotal)})`}
          </button>
          {pills.map((s) => (
            <button key={s} type="button" className={`cmp-pill cmp-pill--${TONE[s]}${filters.status === s ? " cmp-pill--on" : ""}`} aria-pressed={filters.status === s} onClick={() => set("status", filters.status === s ? "" : s)}>
              <span className="cmp-pill__dot" />{t(`complaintStatus.${s}` as Key)}{summary.data && ` (${f.number(counts.get(s) ?? 0)})`}
            </button>
          ))}
          <button type="button" className={`cmp-pill cmp-pill--bad cmp-pill--prio${filters.priority === "High" ? " cmp-pill--on" : ""}`} aria-pressed={filters.priority === "High"} onClick={() => set("priority", filters.priority === "High" ? "" : "High")}>
            <Icon name="fire" size={12} />{t("cmp.high")}{high.data !== undefined && ` (${f.number(high.data)})`}
          </button>
        </div>
        <div className="row">
          {anyFilter && <button type="button" className="cmp-link" onClick={() => setParams({}, { replace: true })}>{t("clearFilters")}</button>}
          <button type="button" className={`cmp-advanced${advancedActive ? " cmp-advanced--active" : ""}`} aria-expanded={advanced} onClick={() => setAdvanced((v) => !v)}>
            <Icon name="config" size={14} />{t("cmp.advanced")}<span className={`cmp-advanced__chev${advanced ? " cmp-advanced__chev--open" : ""}`}><Icon name="chevron" size={12} /></span>
          </button>
        </div>
        {advanced && (
          <div className="cmp-panel">
            <Field label={t("category")}>
              <select value={filters.categoryId} onChange={(e) => set("categoryId", e.target.value)}>
                <option value="">{t("any")}</option>
                {lookups.categories.map((c) => <option key={c.id} value={c.id}>{lookups.categoryName(c.id)}</option>)}
              </select>
            </Field>
            <Field label={t("department")}>
              <select value={filters.departmentId} onChange={(e) => set("departmentId", e.target.value)}>
                <option value="">{t("any")}</option>
                {lookups.departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
              </select>
            </Field>
            <Field label={t("sector")}>
              <select value={filters.locationId} onChange={(e) => set("locationId", e.target.value)}>
                <option value="">{t("any")}</option>
                {sectors.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </select>
            </Field>
            <Field label={t("priority")}>
              <select value={filters.priority} onChange={(e) => set("priority", e.target.value)}>
                <option value="">{t("any")}</option>
                {PRIORITIES.map((p) => <option key={p} value={p}>{t(`priority.${p}` as Key)}</option>)}
              </select>
            </Field>
            <Field label={t("cmp.createdRange")}>
              <div className="cmp-range">
                <input type="date" aria-label={t("from")} value={filters.from} onChange={(e) => set("from", e.target.value)} />
                <span>–</span>
                <input type="date" aria-label={t("to")} value={filters.to} onChange={(e) => set("to", e.target.value)} />
              </div>
            </Field>
          </div>
        )}
      </section>

      <Async state={list}>
        {({ items, total }) => {
          const pages = Math.max(1, Math.ceil(total / PAGE));
          const from = total === 0 ? 0 : (page - 1) * PAGE + 1;
          const to = Math.min(total, page * PAGE);
          return (
            <section className="cmp-table">
              {items.length === 0 ? <EmptyState title={t("complaints.none")} /> : (
                <div className="cmp-scroll">
                  <table>
                    <thead>
                      <tr>
                        <th>{t("reference")}</th><th>{t("cmp.descSector")}</th><th>{t("category")}</th><th>{t("status")}</th><th>{t("priority")}</th><th>{t("created")}</th>
                        <th className="cmp-end">{t("cmp.quick")}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {items.map((c) => (
                        <tr key={c.id} className={inspect === c.id ? "cmp-row--open" : undefined} onClick={() => setInspect(c.id)}>
                          <td><button type="button" className="cmp-ref" onClick={(e) => { e.stopPropagation(); setInspect(c.id); }}>{c.reference}</button></td>
                          <td>
                            <span className="cmp-desc">{c.description}</span>
                            <span className="cmp-place"><Icon name="pin" size={11} />{placeOf(c)}</span>
                          </td>
                          <td>{lookups.categoryName(c.categoryId)}</td>
                          <td><ComplaintStatusChip status={c.status} /></td>
                          <td><PriorityTag priority={c.priority} /></td>
                          <td className="cmp-muted">{f.date(c.createdAt)}</td>
                          <td className="cmp-end" onClick={(e) => e.stopPropagation()}>
                            <button type="button" className="cmp-act" title={t("cmp.inspect")} aria-label={`${t("cmp.inspect")}: ${c.reference}`} onClick={() => setInspect(c.id)}><Icon name="eye" size={15} /></button>
                            <Link className="cmp-act cmp-act--brand" to={`/complaints/${c.id}`} title={t("cmp.assign")} aria-label={`${t("cmp.assign")}: ${c.reference}`}><Icon name="directory" size={15} /></Link>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              <footer className="cmp-foot" role="navigation" aria-label={t("pager")}>
                <span role="status" className="cmp-muted">{t("cmp.showing", { from: f.number(from), to: f.number(to), total: f.number(total) })}</span>
                <div className="row">
                  <Button variant="secondary" disabled={page <= 1} onClick={() => set("page", String(page - 1))}>{t("previous")}</Button>
                  <span className="cmp-page">{t("cmp.pageOf", { page: f.number(page), pages: f.number(pages) })}</span>
                  <Button variant="secondary" disabled={page >= pages} onClick={() => set("page", String(page + 1))}>{t("next")}</Button>
                </div>
              </footer>
            </section>
          );
        }}
      </Async>

      {inspect && <Inspector id={inspect} onClose={() => setInspect(undefined)} />}
    </div>
  );
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return <label className="cmp-field"><span>{label}</span>{children}</label>;
}

/** The slide-over that shows one complaint without leaving the list; "Open" goes to the full page for assigning work or changing status. */
function Inspector({ id, onClose }: { id: string; onClose: () => void }) {
  const t = useT();
  const f = useFormat();
  const lookups = useLookups();
  const { nameOf } = usePeople();
  const detail = useLoad(() => ok(api.GET("/complaints/{id}", { params: { path: { id } } })), [id]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <>
      <div className="cmp-overlay" onClick={onClose} aria-hidden="true" />
      <aside className="cmp-drawer" role="dialog" aria-modal="true" aria-label={t("cmp.inspector")}>
        <Async state={detail}>
          {(d) => {
            const c = d.complaint;
            const place = lookups.locations.find((l) => l.id === c.locationId)?.name ?? c.locationText ?? "—";
            const reporter = nameOf(c.reportedByUserId);
            const photos = d.media.filter((m) => m.kind !== "audio");
            return (
              <>
                <header className="cmp-drawer__head">
                  <div>
                    <span className="cmp-drawer__ref">{c.reference}</span>
                    <h2 className="cmp-drawer__title">{c.description}</h2>
                  </div>
                  <button type="button" className="icon-btn icon-btn--sm icon-btn--box" onClick={onClose} aria-label={t("cmp.close")} autoFocus><Icon name="close" size={15} /></button>
                </header>
                <div className="cmp-drawer__body">
                  <div className="cmp-box cmp-box--split">
                    <div><span className="cmp-label">{t("cmp.currentStatus")}</span><ComplaintStatusChip status={c.status} /></div>
                    <div className="cmp-end"><span className="cmp-label">{t("priority")}</span><PriorityTag priority={c.priority} /></div>
                  </div>
                  <dl className="cmp-box cmp-facts">
                    <div><dt>{t("category")}</dt><dd>{lookups.categoryName(c.categoryId)}</dd></div>
                    <div><dt>{t("sector")}</dt><dd>{place}</dd></div>
                    <div><dt>{t("cmp.reportedOn")}</dt><dd>{f.dateTime(c.createdAt)}</dd></div>
                    <div><dt>{t("channel")}</dt><dd>{t(`channel.${c.channel}` as Key)}</dd></div>
                  </dl>
                  <div className="cmp-box">
                    <h3 className="cmp-box__title"><Icon name="directory" size={13} />{t("cmp.reporter")}</h3>
                    <div className="cmp-person">
                      <span className="cmp-person__avatar" aria-hidden="true">{reporter === "—" ? "?" : reporter.split(/\s+/).map((p) => p[0]).slice(0, 2).join("").toUpperCase()}</span>
                      <div><strong>{reporter}</strong><span className="cmp-muted">{c.locationText ?? place}</span></div>
                    </div>
                  </div>
                  {d.media.length > 0 && (
                    <div className="cmp-box">
                      <h3 className="cmp-box__title"><Icon name="camera" size={13} />{t("cmp.evidence")}</h3>
                      <ul className="cmp-media">
                        {photos.map((m) => (
                          <li key={m.id}><a href={m.url} target="_blank" rel="noreferrer"><img src={m.thumbUrl ?? m.url} alt={t("complaint.photo")} loading="lazy" /></a></li>
                        ))}
                        {d.media.filter((m) => m.kind === "audio").map((m) => <li key={m.id} className="cmp-media__audio"><audio controls src={m.url} aria-label={t("complaint.voice")} /></li>)}
                      </ul>
                    </div>
                  )}
                  <div className="cmp-box">
                    <h3 className="cmp-box__title"><Icon name="history" size={13} />{t("cmp.progress")}</h3>
                    <ol className="cmp-steps">
                      {d.history.map((h, i) => (
                        <li key={i} className={`cmp-steps__${TONE[h.to]}`}>
                          <strong>{t(`complaintStatus.${h.to}` as Key)}</strong>
                          <span className="cmp-muted">{f.dateTime(h.occurredAt)} · {nameOf(h.actorUserId)}</span>
                          {h.reason && <span>{h.reason}</span>}
                        </li>
                      ))}
                    </ol>
                  </div>
                </div>
                <footer className="cmp-drawer__foot">
                  <Link className="cmp-open" to={`/complaints/${c.id}`}><Icon name="directory" size={14} />{t("cmp.openFull")}</Link>
                  <Button variant="secondary" onClick={onClose}>{t("cmp.close")}</Button>
                </footer>
              </>
            );
          }}
        </Async>
      </aside>
    </>
  );
}
