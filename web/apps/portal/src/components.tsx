import { Alert, Button, ErrorState, Sheet, StatusChip, TextAreaField, type Status } from "@urbanfix/ui";
import { createContext, useContext, useMemo, useState, type ReactNode } from "react";
import { api, ok, type ApiError, type S } from "./api";
import { useLoad } from "./hooks";
import { useT, type Key } from "./i18n";
import { useSession } from "./session";

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: string; actions?: ReactNode }) {
  return (
    <div className="page-header">
      <div className="page-header__text">
        <h1>{title}</h1>
        {subtitle && <p className="page-header__sub">{subtitle}</p>}
      </div>
      {actions && <div className="page-header__actions">{actions}</div>}
    </div>
  );
}

/** Shows a spinner-free loading line, an error with retry, or the content. */
export function Async<T>({ state, children }: { state: { data: T | undefined; error: ApiError | undefined; loading: boolean; reload: () => void }; children: (data: T) => ReactNode }) {
  const t = useT();
  if (state.data !== undefined) return <>{children(state.data)}</>;
  if (state.error) return <ErrorState onRetry={state.reload} body={errorText(t, state.error.code)} />;
  return (
    <div className="skeleton" role="status">
      <span className="uf-sr-only">{t("loading")}</span>
      <span className="skeleton__bar skeleton__bar--wide" /><span className="skeleton__bar" /><span className="skeleton__bar skeleton__bar--short" />
    </div>
  );
}

export function errorText(t: ReturnType<typeof useT>, code: string): string {
  const key = `error.${code}` as Key;
  const translated = t(key);
  return translated === key ? t("error.generic") : translated;
}

export function ErrorNote({ error }: { error: ApiError | undefined }) {
  const t = useT();
  return error ? <Alert tone="danger">{errorText(t, error.code)}</Alert> : null;
}

const complaintIcon: Record<S["ComplaintStatus"], Status> = {
  New: "open", OnHold: "open", UnderReview: "open", Assigned: "assigned", InProgress: "inProgress",
  Resolved: "resolved", Closed: "resolved", Rejected: "rejected", Merged: "rejected",
};

export function ComplaintStatusChip({ status }: { status: S["ComplaintStatus"] }) {
  const t = useT();
  return <StatusChip status={complaintIcon[status]} label={t(`complaintStatus.${status}` as Key)} />;
}

const jobIcon: Record<string, Status> = {
  Open: "open", Accepted: "assigned", InProgress: "inProgress", PendingVerification: "inProgress", Completed: "resolved", Returned: "rejected", Cancelled: "rejected",
};

export function JobStatusChip({ status }: { status: string }) {
  const t = useT();
  return <StatusChip status={jobIcon[status] ?? "open"} label={t(`jobStatus.${status}` as Key)} />;
}

export function PriorityTag({ priority }: { priority: string }) {
  const t = useT();
  return <span className={`priority priority--${priority.toLowerCase()}`}>{t(`priority.${priority}` as Key)}</span>;
}

// People ------------------------------------------------------------------------------------------------------

interface People {
  list: S["PersonDto"][];
  nameOf: (id: string | null | undefined) => string;
}

const PeopleCtx = createContext<People>({ list: [], nameOf: () => "—" });

/** Loads the organization's people once, so any screen can show names instead of ids. */
export function PeopleProvider({ children }: { children: ReactNode }) {
  const { role } = useSession();
  const staff = role !== undefined && role !== "Resident" && role !== "Accounts" && role !== "Guard" && role !== "PlatformAdmin";
  const state = useLoad(() => (staff ? ok(api.GET("/directory")) : Promise.resolve([])), [staff]);
  const value = useMemo<People>(() => {
    const list = state.data ?? [];
    const byId = new Map(list.map((p) => [p.userId, p.displayName]));
    return { list, nameOf: (id) => (id ? byId.get(id) ?? "—" : "—") };
  }, [state.data]);
  return <PeopleCtx.Provider value={value}>{children}</PeopleCtx.Provider>;
}

export const usePeople = () => useContext(PeopleCtx);

// Reason dialog -----------------------------------------------------------------------------------------------

/** Asks for a reason before a state change (reject, send back, return work...). */
export function ReasonDialog({ open, title, required = true, error, busy, onCancel, onConfirm, children }: {
  open: boolean; title: string; required?: boolean; error?: ApiError; busy?: boolean;
  onCancel: () => void; onConfirm: (reason: string) => void; children?: ReactNode;
}) {
  const t = useT();
  const [reason, setReason] = useState("");
  const blank = required && reason.trim() === "";
  return (
    <Sheet open={open} title={title} onClose={onCancel}>
      <form className="stack" onSubmit={(e) => { e.preventDefault(); if (!blank) onConfirm(reason.trim()); }}>
        {children}
        <TextAreaField label={t("reason")} value={reason} onChange={(e) => setReason(e.target.value)} required={required} autoFocus />
        <ErrorNote error={error} />
        <div className="row">
          <Button type="submit" loading={busy} disabled={blank}>{t("confirm")}</Button>
          <Button type="button" variant="ghost" onClick={onCancel}>{t("cancel")}</Button>
        </div>
      </form>
    </Sheet>
  );
}

export function Pager({ total, page, size, onPage }: { total: number; page: number; size: number; onPage: (page: number) => void }) {
  const t = useT();
  const pages = Math.max(1, Math.ceil(total / size));
  return (
    <div className="row pager" role="navigation" aria-label={t("pager")}>
      <Button variant="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)}>{t("previous")}</Button>
      <span>{t("pageOf", { page, pages, total })}</span>
      <Button variant="secondary" disabled={page >= pages} onClick={() => onPage(page + 1)}>{t("next")}</Button>
    </div>
  );
}
