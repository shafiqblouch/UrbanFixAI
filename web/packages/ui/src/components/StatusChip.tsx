import { useLocale } from "../locale";
import type { MessageKey } from "../core";
import { Icons } from "./icons";

export type Status = "open" | "assigned" | "inProgress" | "resolved" | "rejected" | "overdue";

const tone: Record<Status, "info" | "neutral" | "warning" | "success" | "danger"> = {
  open: "info",
  assigned: "neutral",
  inProgress: "warning",
  resolved: "success",
  rejected: "danger",
  overdue: "danger",
};

const icon: Record<Status, () => JSX.Element> = {
  open: Icons.open,
  assigned: Icons.assigned,
  inProgress: Icons.inProgress,
  resolved: Icons.resolved,
  rejected: Icons.rejected,
  overdue: Icons.overdue,
};

/** Always icon plus text, so status is readable without colour (DS-02). `label` overrides the default wording for statuses that share an icon. */
export function StatusChip({ status, label }: { status: Status; label?: string }) {
  const { t } = useLocale();
  const Icon = icon[status];
  return (
    <span className={`uf-chip uf-chip--${tone[status]}`}>
      <Icon />
      <span>{label ?? t(`status.${status}` as MessageKey)}</span>
    </span>
  );
}
