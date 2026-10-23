import { Card, EmptyState, SelectField, Table } from "@urbanfix/ui";
import { Link, useSearchParams } from "react-router-dom";
import { api, ok } from "../api";
import { Async, JobStatusChip, PageHeader, PriorityTag, usePeople } from "../components";
import { useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { useSession } from "../session";

const STATUSES = ["Open", "Accepted", "InProgress", "PendingVerification", "Completed", "Returned", "Cancelled"] as const;

/** Job list: everything for supervisors, only their own for technicians (JB-02, WP-06). */
export function JobsPage() {
  const t = useT();
  const f = useFormat();
  const { role } = useSession();
  const { nameOf } = usePeople();
  const [params, setParams] = useSearchParams();
  const status = params.get("status") ?? "";
  const jobs = useLoad(() => ok(api.GET("/jobs", { params: { query: { status: status || undefined, limit: 200 } } })), [status]);

  return (
    <>
      <PageHeader title={role === "Technician" ? t("menu.myJobs") : t("menu.jobs")} />
      <Card>
        <div className="filters">
          <SelectField label={t("status")} placeholder={t("any")} value={status}
            onChange={(e) => setParams(e.target.value ? { status: e.target.value } : {}, { replace: true })}
            options={STATUSES.map((s) => ({ value: s, label: t(`jobStatus.${s}` as Key) }))} />
        </div>
      </Card>
      <Async state={jobs}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("jobs.none")} /> : (
          <Table caption={t("menu.jobs")} rows={rows} rowKey={(r) => r.job.id}
            columns={[
              { key: "n", header: t("job"), render: (r) => <Link to={`/jobs/${r.job.id}`}>{r.job.number}</Link> },
              { key: "c", header: t("reference"), render: (r) => <Link to={`/complaints/${r.job.complaintId}`}>{r.reference}</Link> },
              { key: "d", header: t("description"), render: (r) => <span className="clip">{r.description}</span> },
              { key: "s", header: t("status"), render: (r) => <JobStatusChip status={r.job.status} /> },
              { key: "p", header: t("priority"), render: (r) => <PriorityTag priority={r.priority} /> },
              { key: "a", header: t("assignee"), render: (r) => nameOf(r.job.assignedUserId) },
              { key: "t", header: t("created"), render: (r) => f.date(r.job.createdAt) },
            ]} />
        )}
      </Async>
    </>
  );
}
