import { Button, Card, EmptyState, SelectField, Table, TextField } from "@urbanfix/ui";
import { useState } from "react";
import { api, ok, type S } from "../api";
import { Async, PageHeader, Pager, usePeople } from "../components";
import { useLoad } from "../hooks";
import { useFormat, useT } from "../i18n";

const PAGE = 50;

/** Who changed what and when, filterable by actor, entity, action and date (WP-16). Rows are read-only. */
export function AuditPage() {
  const t = useT();
  const f = useFormat();
  const { list, nameOf } = usePeople();
  const [actor, setActor] = useState("");
  const [entityType, setEntityType] = useState("");
  const [entityId, setEntityId] = useState("");
  const [action, setAction] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<string>();

  const rows = useLoad(() => ok(api.GET("/audit", { params: { query: {
    actorUserId: actor || undefined, entityType: entityType || undefined, entityId: entityId.trim() || undefined, action: action || undefined,
    from: from ? new Date(from).toISOString() : undefined, to: to ? new Date(`${to}T23:59:59`).toISOString() : undefined,
    limit: PAGE, offset: (page - 1) * PAGE,
  } } })), [actor, entityType, entityId, action, from, to, page]);

  const reset = (fn: () => void) => { fn(); setPage(1); };
  const hasNext = (rows.data?.length ?? 0) === PAGE;
  const detail = (a: S["AuditDto"]) => JSON.stringify({ before: a.before, after: a.after }, null, 2);

  return (
    <>
      <PageHeader title={t("menu.audit")} />
      <Card>
        <form className="filters" role="search" aria-label={t("filters")} onSubmit={(e) => e.preventDefault()}>
          <SelectField label={t("who")} placeholder={t("any")} value={actor} onChange={(e) => reset(() => setActor(e.target.value))}
            options={list.map((p) => ({ value: p.userId, label: p.displayName }))} />
          <TextField label={t("audit.entityType")} value={entityType} onChange={(e) => reset(() => setEntityType(e.target.value))} placeholder="Complaint" />
          <TextField label={t("audit.entityId")} value={entityId} onChange={(e) => reset(() => setEntityId(e.target.value))} />
          <SelectField label={t("action")} placeholder={t("any")} value={action} onChange={(e) => reset(() => setAction(e.target.value))}
            options={["created", "updated", "deleted"].map((a) => ({ value: a, label: a }))} />
          <TextField label={t("from")} type="date" value={from} onChange={(e) => reset(() => setFrom(e.target.value))} />
          <TextField label={t("to")} type="date" value={to} onChange={(e) => reset(() => setTo(e.target.value))} />
        </form>
      </Card>
      <Async state={rows}>
        {(data) => data.length === 0 ? <EmptyState title={t("audit.none")} /> : (
          <>
            <Table caption={t("menu.audit")} rows={data} rowKey={(a) => a.id}
              columns={[
                { key: "when", header: t("when"), render: (a) => f.dateTime(a.occurredAt) },
                { key: "who", header: t("who"), render: (a) => nameOf(a.actorUserId) },
                { key: "action", header: t("action"), render: (a) => a.action },
                { key: "entity", header: t("audit.entityType"), render: (a) => a.entityType },
                { key: "id", header: t("audit.entityId"), render: (a) => <code dir="ltr">{a.entityId.slice(0, 8)}</code> },
                { key: "d", header: "", render: (a) => <Button variant="ghost" aria-expanded={open === a.id} onClick={() => setOpen(open === a.id ? undefined : a.id)}>{t("audit.details")}</Button> },
              ]} />
            {open && <pre className="audit-detail" dir="ltr" tabIndex={0}>{detail(data.find((a) => a.id === open)!)}</pre>}
            <Pager total={hasNext ? page * PAGE + 1 : (page - 1) * PAGE + data.length} page={page} size={PAGE} onPage={setPage} />
          </>
        )}
      </Async>
    </>
  );
}
