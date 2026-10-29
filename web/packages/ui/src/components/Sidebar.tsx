import { Fragment, type ReactNode } from "react";
import { useLocale } from "../locale";
import { formatNumber } from "../core";

export interface NavItem {
  id: string;
  label: string;
  href?: string;
  icon?: ReactNode;
  /** Count shown as a badge; hidden when 0 or undefined. */
  count?: number;
  /** A small heading shown above this item when it differs from the previous item's section. */
  section?: string;
  /** How the count badge looks: a quiet chip, the accent (default) or a warning. */
  tone?: "quiet" | "accent" | "alert";
  /** Replaces the number inside the badge, e.g. "1 Alert". */
  countText?: string;
}

export interface NavProps {
  items: NavItem[];
  currentId: string;
  onNavigate?: (id: string) => void;
}

function NavLink({ item, current, onNavigate, className }: { item: NavItem; current: boolean; onNavigate?: NavProps["onNavigate"]; className: string }) {
  const { t, locale } = useLocale();
  const count = item.count ?? 0;
  return (
    <a
      className={className}
      href={item.href ?? `#${item.id}`}
      aria-current={current ? "page" : undefined}
      aria-label={count > 0 ? `${item.label}, ${t("nav.badge", { count: formatNumber(locale, count) })}` : undefined}
      onClick={(e) => {
        if (onNavigate) {
          e.preventDefault();
          onNavigate(item.id);
        }
      }}
    >
      {item.icon && <span className="uf-nav__icon" aria-hidden="true">{item.icon}</span>}
      <span className="uf-nav__label">{item.label}</span>
      {count > 0 && <span className={`uf-badge${item.tone && item.tone !== "accent" ? ` uf-badge--${item.tone}` : ""}`} aria-hidden="true">{item.countText ?? (count > 99 ? "99+" : formatNumber(locale, count))}</span>}
    </a>
  );
}

export function Sidebar({ items, currentId, onNavigate }: NavProps) {
  const { t } = useLocale();
  return (
    <nav className="uf-sidebar" aria-label={t("nav.primary")}>
      {items.map((item, i) => (
        <Fragment key={item.id}>
          {item.section && item.section !== items[i - 1]?.section && <div className="uf-nav__section">{item.section}</div>}
          <NavLink item={item} current={item.id === currentId} onNavigate={onNavigate} className="uf-nav__item" />
        </Fragment>
      ))}
    </nav>
  );
}

export function BottomTabs({ items, currentId, onNavigate }: NavProps) {
  const { t } = useLocale();
  return (
    <nav className="uf-tabs" aria-label={t("nav.primary")}>
      {items.map((item) => (
        <NavLink key={item.id} item={item} current={item.id === currentId} onNavigate={onNavigate} className="uf-tabs__item" />
      ))}
    </nav>
  );
}
