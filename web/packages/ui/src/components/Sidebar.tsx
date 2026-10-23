import type { ReactNode } from "react";
import { useLocale } from "../locale";
import { formatNumber } from "../core";

export interface NavItem {
  id: string;
  label: string;
  href?: string;
  icon?: ReactNode;
  /** Count shown as a badge; hidden when 0 or undefined. */
  count?: number;
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
      {count > 0 && <span className="uf-badge" aria-hidden="true">{count > 99 ? "99+" : formatNumber(locale, count)}</span>}
    </a>
  );
}

export function Sidebar({ items, currentId, onNavigate }: NavProps) {
  const { t } = useLocale();
  return (
    <nav className="uf-sidebar" aria-label={t("nav.primary")}>
      {items.map((item) => (
        <NavLink key={item.id} item={item} current={item.id === currentId} onNavigate={onNavigate} className="uf-nav__item" />
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
