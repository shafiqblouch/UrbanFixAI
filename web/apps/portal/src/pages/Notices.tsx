import { Alert, Button, Card, EmptyState, SelectField, Sheet, Table, TextAreaField, TextField } from "@urbanfix/ui";
import { useEffect, useState, type FormEvent } from "react";
import { api, ok, type S } from "../api";
import { Async, ErrorNote, PageHeader } from "../components";
import { useAction, useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { useLookups } from "../lookups";
import { canSendNotices } from "../roles";
import { useSession } from "../session";

/** Notices (WP-19, RA-09): managers and the committee write and track them; everyone else reads the ones sent to them. */
export function NoticesPage() {
  const { role } = useSession();
  return canSendNotices(role) ? <ManageNotices /> : <MyNotices />;
}

// Sender ------------------------------------------------------------------------------------------------------

function ManageNotices() {
  const t = useT();
  const notices = useLoad(() => ok(api.GET("/notices")), []);
  const [composing, setComposing] = useState(false);
  const [viewing, setViewing] = useState<S["NoticeDto"]>();
  return (
    <>
      <PageHeader title={t("menu.notices")} actions={<Button onClick={() => setComposing(true)}>{t("notices.new")}</Button>} />
      <Async state={notices}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("notices.empty")} /> : <NoticeTable rows={rows} onOpen={setViewing} onChanged={notices.reload} />}
      </Async>
      <Composer open={composing} onClose={() => setComposing(false)} onSent={() => { setComposing(false); notices.reload(); }} />
      <Readers notice={viewing} onClose={() => setViewing(undefined)} />
    </>
  );
}

function NoticeTable({ rows, onOpen, onChanged }: { rows: S["NoticeDto"][]; onOpen: (n: S["NoticeDto"]) => void; onChanged: () => void }) {
  const t = useT();
  const f = useFormat();
  const action = useAction();
  const cancel = (id: string) => action.run(async () => {
    await ok(api.POST("/notices/{id}/cancel", { params: { path: { id } } }));
    onChanged();
  });
  return (
    <>
      <ErrorNote error={action.error} />
      <Table
        caption={t("menu.notices")}
        rows={rows}
        rowKey={(n) => n.id}
        columns={[
          { key: "title", header: t("notices.titleField"), render: (n) => <strong>{n.title}</strong> },
          { key: "audience", header: t("notices.audience"), render: (n) => t(`notices.audience.${n.audienceKind}` as Key) },
          {
            key: "status", header: t("status"),
            render: (n) => (
              <>
                {t(`notices.status.${n.status}` as Key)}
                <br />
                <small className="muted">
                  {n.status === "Sent" ? t("notices.sentOn", { when: f.dateTime(n.sentAt) }) : n.scheduledAt ? t("notices.scheduledFor", { when: f.dateTime(n.scheduledAt) }) : f.dateTime(n.createdAt)}
                </small>
              </>
            ),
          },
          { key: "recipients", header: t("notices.recipients"), numeric: true, render: (n) => f.number(n.recipients) },
          { key: "delivered", header: t("notices.delivered"), numeric: true, render: (n) => f.number(n.delivered) },
          { key: "read", header: t("notices.read"), numeric: true, render: (n) => f.number(n.read) },
          { key: "failed", header: t("notices.failed"), numeric: true, render: (n) => f.number(n.failed) },
          {
            key: "actions", header: t("action"),
            render: (n) => n.status === "Scheduled"
              ? <Button variant="danger" loading={action.busy} onClick={() => void cancel(n.id)}>{t("notices.cancel")}</Button>
              : n.status === "Sent" ? <Button variant="secondary" onClick={() => onOpen(n)}>{t("notices.details")}</Button> : null,
          },
        ]}
      />
    </>
  );
}

const AUDIENCES = ["all", "owners", "tenants"] as const;

function Composer({ open, onClose, onSent }: { open: boolean; onClose: () => void; onSent: () => void }) {
  const t = useT();
  const { locations } = useLookups();
  const action = useAction();
  const [form, setForm] = useState({ title: "", body: "", titleUr: "", bodyUr: "", audienceKind: "all", locationId: "", later: false, sendAt: "" });
  const [reach, setReach] = useState<number>();
  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm((f) => ({ ...f, [key]: value }));

  // Show how many people the choice reaches today, before anything is sent.
  useEffect(() => {
    if (!open) return;
    let current = true;
    ok(api.GET("/notices/audience", { params: { query: { kind: form.audienceKind, locationId: form.locationId || undefined } } }))
      .then((a) => { if (current) setReach(a.recipients); }, () => { if (current) setReach(undefined); });
    return () => { current = false; };
  }, [open, form.audienceKind, form.locationId]);

  const urduHalf = (form.titleUr.trim() === "") !== (form.bodyUr.trim() === "");
  const blank = form.title.trim() === "" || form.body.trim() === "" || urduHalf || (form.later && form.sendAt === "");

  function submit(e: FormEvent) {
    e.preventDefault();
    void action.run(async () => {
      await ok(api.POST("/notices", {
        body: {
          title: form.title, body: form.body, titleUr: form.titleUr.trim() || null, bodyUr: form.bodyUr.trim() || null,
          audienceKind: form.audienceKind, locationId: form.locationId || null,
          scheduledAt: form.later ? new Date(form.sendAt).toISOString() : null,
        },
      }));
      setForm((f) => ({ ...f, title: "", body: "", titleUr: "", bodyUr: "" }));
      onSent();
    });
  }

  return (
    <Sheet open={open} title={t("notices.new")} onClose={onClose}>
      <form className="stack" onSubmit={submit}>
        <TextField label={t("notices.titleField")} value={form.title} maxLength={200} required onChange={(e) => set("title", e.target.value)} />
        <TextAreaField label={t("notices.body")} value={form.body} maxLength={4000} required onChange={(e) => set("body", e.target.value)} />
        <TextField label={t("notices.titleUr")} dir="rtl" lang="ur" value={form.titleUr} maxLength={200} onChange={(e) => set("titleUr", e.target.value)} />
        <TextAreaField label={t("notices.bodyUr")} dir="rtl" lang="ur" hint={t("notices.urduHint")} value={form.bodyUr} maxLength={4000} onChange={(e) => set("bodyUr", e.target.value)} />
        <div className="queue__fields">
          <SelectField label={t("notices.audience")} value={form.audienceKind} onChange={(e) => set("audienceKind", e.target.value)}
            options={AUDIENCES.map((a) => ({ value: a, label: t(`notices.audience.${a}` as Key) }))} />
          <SelectField label={t("notices.where")} value={form.locationId} onChange={(e) => set("locationId", e.target.value)}
            options={[{ value: "", label: t("notices.wholeEstate") }, ...locations.map((l) => ({ value: l.id, label: "—".repeat(l.depth) + " " + l.name }))]} />
          <SelectField label={t("notices.when")} value={form.later ? "later" : "now"} onChange={(e) => set("later", e.target.value === "later")}
            options={[{ value: "now", label: t("notices.now") }, { value: "later", label: t("notices.later") }]} />
          {form.later && <TextField type="datetime-local" label={t("notices.sendAt")} value={form.sendAt} required onChange={(e) => set("sendAt", e.target.value)} />}
        </div>
        {reach !== undefined && <Alert tone={reach === 0 ? "warning" : "info"}>{reach === 0 ? t("notices.reachNone") : t("notices.reach", { count: reach })}</Alert>}
        <ErrorNote error={action.error} />
        <div className="row">
          <Button type="submit" loading={action.busy} disabled={blank}>{form.later ? t("notices.schedule") : t("notices.send")}</Button>
          <Button type="button" variant="ghost" onClick={onClose}>{t("cancel")}</Button>
        </div>
      </form>
    </Sheet>
  );
}

/** Who was sent a notice and who has read it (WP-19). */
function Readers({ notice, onClose }: { notice: S["NoticeDto"] | undefined; onClose: () => void }) {
  const t = useT();
  const f = useFormat();
  const readers = useLoad(() => (notice ? ok(api.GET("/notices/{id}/readers", { params: { path: { id: notice.id } } })) : Promise.resolve([])), [notice?.id]);
  const state = (r: S["ReaderDto"]): string => r.readAt ? t("notices.readers.read") : r.failed ? t("notices.readers.failed") : r.delivered ? t("notices.readers.delivered") : t("notices.readers.pending");
  return (
    <Sheet open={notice !== undefined} title={notice?.title ?? ""} onClose={onClose}>
      <Async state={readers}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("notices.readers.none")} /> : (
          <Table
            caption={t("notices.details")}
            rows={rows}
            rowKey={(r) => r.userId}
            columns={[
              { key: "name", header: t("name"), render: (r) => r.name },
              { key: "state", header: t("status"), render: state },
              { key: "readAt", header: t("notices.read"), render: (r) => f.dateTime(r.readAt) },
            ]}
          />
        )}
      </Async>
    </Sheet>
  );
}

// Recipient ---------------------------------------------------------------------------------------------------

function MyNotices() {
  const t = useT();
  const f = useFormat();
  const mine = useLoad(() => ok(api.GET("/me/notices")), []);
  const action = useAction();
  const open = (n: S["MyNoticeDto"]) => {
    if (!n.readAt) void action.run(async () => { await ok(api.POST("/notifications/{id}/read", { params: { path: { id: n.id } } })); mine.reload(); });
  };
  return (
    <>
      <PageHeader title={t("menu.notices")} />
      <Async state={mine}>
        {(rows) => rows.length === 0 ? <EmptyState title={t("notices.mineEmpty")} /> : (
          <ul className="queue">
            {rows.map((n) => (
              <li key={n.id} className={n.readAt ? "" : "notice--unread"}>
                <Card title={n.title} onClick={() => open(n)}>
                  <p className="notice__body">{n.body}</p>
                  <small className="muted">{f.dateTime(n.createdAt)}</small>
                </Card>
              </li>
            ))}
          </ul>
        )}
      </Async>
    </>
  );
}
