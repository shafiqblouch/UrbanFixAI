import type { ReactNode } from "react";
import { useLocale } from "../locale";
import { formatNumber } from "../core";
import { Button } from "./Button";
import { Icons } from "./icons";

function State({ icon, title, body, action, role }: { icon: ReactNode; title: string; body?: string; action?: ReactNode; role?: "alert" | "status" }) {
  return (
    <div className="uf-state" role={role}>
      <span className="uf-state__icon">{icon}</span>
      <h2 className="uf-state__title">{title}</h2>
      {body && <p className="uf-state__body">{body}</p>}
      {action}
    </div>
  );
}

export function EmptyState({ title, body, action }: { title?: string; body?: string; action?: ReactNode }) {
  const { t } = useLocale();
  return <State icon={<Icons.empty />} title={title ?? t("state.empty.title")} body={body} action={action} />;
}

export function ErrorState({ title, body, onRetry }: { title?: string; body?: string; onRetry?: () => void }) {
  const { t } = useLocale();
  return (
    <State
      role="alert"
      icon={<Icons.error />}
      title={title ?? t("state.error.title")}
      body={body ?? t("state.error.body")}
      action={onRetry && <Button variant="secondary" onClick={onRetry}>{t("common.retry")}</Button>}
    />
  );
}

/** Shown while the device has no connection; `queued` is the count of unsynced items (FD-17). */
export function OfflineState({ queued = 0 }: { queued?: number }) {
  const { t, locale } = useLocale();
  const body = t("state.offline.body");
  return (
    <State
      role="status"
      icon={<Icons.offline />}
      title={t("state.offline.title")}
      body={queued > 0 ? `${body} ${t("state.offline.queued", { count: formatNumber(locale, queued) })}` : body}
    />
  );
}
