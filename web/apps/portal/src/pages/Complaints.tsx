import { Button, Card, EmptyState, SelectField, Table, TextField } from "@urbanfix/ui";
import { Link, useSearchParams } from "react-router-dom";
import { api, download, okPaged } from "../api";
import { Async, ComplaintStatusChip, ErrorNote, PageHeader, Pager, PriorityTag } from "../components";
import { useAction, useDebounced, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { useLookups } from "../lookups";

const PAGE = 25;
const STATUSES = ["New", "OnHold", "UnderReview", "Assigned", "InProgress", "Resolved", "Closed", "Rejected", "Merged"] as const;
const PRIORITIES = ["Low", "Normal", "High", "Urgent"] as const;

/** Complaint list with combinable filters and server-side search and paging (WP-02). The URL holds the filters, so a view can be shared. */
export function ComplaintsPage() {
  const t = useT();
  const f = useFormat();
  const lookups = useLookups();
  const [params, setParams] = useSearchParams();
  const get = (k: string) => params.get(k) ?? "";
  const q = get("q"); // the URL is the single source of truth, so the header search box and this field stay in step
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

  // The same filters the list uses, so the file holds what is on screen (all pages, up to the server's limit).
  const exporting = useAction();
  const exportQuery = {
    q: debouncedQ || undefined, status: filters.status || undefined, categoryId: filters.categoryId || undefined, departmentId: filters.departmentId || undefined,
    locationId: filters.locationId || undefined, priority: filters.priority || undefined,
    from: filters.from ? new Date(filters.from).toISOString() : undefined, to: filters.to ? new Date(`${filters.to}T23:59:59`).toISOString() : undefined,
  };

  const sectors = lookups.locations.filter((l) => l.depth === 0 || l.kind === "sector");
  const anyFilter = debouncedQ !== "" || Object.values(filters).some(Boolean);

  return (
    <>
      <PageHeader title={t("menu.complaints")} actions={
        <>
          <Button variant="secondary" loading={exporting.busy} onClick={() => void exporting.run(() => download("/exports/complaints", { ...exportQuery, format: "csv" }))}>{t("complaints.exportCsv")}</Button>
          <Button variant="secondary" loading={exporting.busy} onClick={() => void exporting.run(() => download("/exports/complaints", { ...exportQuery, format: "pdf" }))}>{t("complaints.exportPdf")}</Button>
        </>
      } />
      <ErrorNote error={exporting.error} />
      <Card>
        <form className="filters" role="search" aria-label={t("filters")} onSubmit={(e) => e.preventDefault()}>
          <TextField label={t("search")} type="search" value={q} onChange={(e) => set("q", e.target.value)} />
          <SelectField label={t("status")} placeholder={t("any")} value={filters.status} onChange={(e) => set("status", e.target.value)}
            options={STATUSES.map((s) => ({ value: s, label: t(`complaintStatus.${s}` as Key) }))} />
          <SelectField label={t("category")} placeholder={t("any")} value={filters.categoryId} onChange={(e) => set("categoryId", e.target.value)}
            options={lookups.categories.map((c) => ({ value: c.id, label: lookups.categoryName(c.id) }))} />
          <SelectField label={t("department")} placeholder={t("any")} value={filters.departmentId} onChange={(e) => set("departmentId", e.target.value)}
            options={lookups.departments.map((d) => ({ value: d.id, label: d.name }))} />
          <SelectField label={t("sector")} placeholder={t("any")} value={filters.locationId} onChange={(e) => set("locationId", e.target.value)}
            options={sectors.map((l) => ({ value: l.id, label: l.name }))} />
          <SelectField label={t("priority")} placeholder={t("any")} value={filters.priority} onChange={(e) => set("priority", e.target.value)}
            options={PRIORITIES.map((p) => ({ value: p, label: t(`priority.${p}` as Key) }))} />
          <TextField label={t("from")} type="date" value={filters.from} onChange={(e) => set("from", e.target.value)} />
          <TextField label={t("to")} type="date" value={filters.to} onChange={(e) => set("to", e.target.value)} />
          {anyFilter && <Button type="button" variant="ghost" onClick={() => setParams({}, { replace: true })}>{t("clearFilters")}</Button>}
        </form>
      </Card>

      <Async state={list}>
        {({ items, total }) => items.length === 0 ? <EmptyState title={t("complaints.none")} /> : (
          <>
            <p className="muted" role="status">{t("results", { count: f.number(total) })}</p>
            <Table caption={t("menu.complaints")} rows={items} rowKey={(c) => c.id}
              columns={[
                { key: "ref", header: t("reference"), render: (c) => <Link to={`/complaints/${c.id}`}>{c.reference}</Link> },
                { key: "desc", header: t("description"), render: (c) => <span className="clip">{c.description}</span> },
                { key: "cat", header: t("category"), render: (c) => lookups.categoryName(c.categoryId) },
                { key: "status", header: t("status"), render: (c) => <ComplaintStatusChip status={c.status} /> },
                { key: "pri", header: t("priority"), render: (c) => <PriorityTag priority={c.priority} /> },
                { key: "created", header: t("created"), render: (c) => f.date(c.createdAt) },
              ]} />
            <Pager total={total} page={page} size={PAGE} onPage={(p) => set("page", String(p))} />
          </>
        )}
      </Async>
    </>
  );
}
