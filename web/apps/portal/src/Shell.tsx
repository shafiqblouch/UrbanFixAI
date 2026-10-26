import { Button, EmptyState, Sheet, Sidebar, useLocale, type NavItem } from "@urbanfix/ui";
import { Icon, initials } from "./icons";
import { useEffect, useState, type FormEvent } from "react";
import { Navigate, Outlet, useLocation, useNavigate } from "react-router-dom";
import { api, ok } from "./api";
import { useT, saveLocale, useFormat, type Key } from "./i18n";
import { useLive } from "./live";
import { PushSettings } from "./PushSettings";
import { menusFor, type MenuId } from "./roles";
import { useSession } from "./session";

type Theme = "system" | "light" | "dark";
const THEME_KEY = "uf.theme";

function useTheme(): [Theme, () => void] {
  const [theme, setTheme] = useState<Theme>(() => {
    try { return (localStorage.getItem(THEME_KEY) as Theme | null) ?? "system"; } catch { return "system"; }
  });
  useEffect(() => {
    const root = document.documentElement;
    if (theme === "system") root.removeAttribute("data-theme"); else root.setAttribute("data-theme", theme);
    try { localStorage.setItem(THEME_KEY, theme); } catch { /* storage unavailable */ }
  }, [theme]);
  return [theme, () => setTheme((c) => (c === "system" ? "dark" : c === "dark" ? "light" : "system"))];
}

const paths: Record<MenuId, string> = {
  platform: "/platform",
  directory: "/directory",
  charges: "/charges",
  reports: "/reports",
  dashboard: "/dashboard", complaints: "/complaints", review: "/review", new: "/new", jobs: "/jobs", stock: "/stock", approvals: "/approvals", notices: "/notices", config: "/config", audit: "/audit",
};

/** Header, role-based sidebar with count badges, and the signed-in guard (WP-01). */
export function Shell({ onLocale }: { onLocale: (l: "en" | "ur") => void }) {
  const t = useT();
  const { me, role, loading, signOut, leaveSupport } = useSession();
  const { counts, unread } = useLive();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const location = useLocation();
  const [theme, cycleTheme] = useTheme();
  const [search, setSearch] = useState("");
  const [bell, setBell] = useState(false);
  const [navOpen, setNavOpen] = useState(false);
  const f = useFormat();

  if (loading) return <p className="muted center" role="status">{t("loading")}</p>;
  if (!me?.organizationId) return <Navigate to="/login" replace />;

  const menus = menusFor(role);
  const current = menus.find((m) => location.pathname.startsWith(paths[m])) ?? menus[0] ?? "";
  const countFor = (m: MenuId): number | undefined =>
    m === "review" ? counts.review : m === "jobs" ? (role === "Technician" ? counts.jobs : counts.signoff) : undefined;
  const items: NavItem[] = menus.map((m) => ({ id: m, label: m === "jobs" && role === "Technician" ? t("menu.myJobs") : t(`menu.${m}` as Key), href: paths[m], count: countFor(m), icon: <Icon name={m} /> }));
  const organization = me.memberships.find((m) => m.organizationId === me.organizationId)?.organizationName;

  function submitSearch(e: FormEvent) {
    e.preventDefault();
    navigate(`/complaints?q=${encodeURIComponent(search.trim())}`);
  }

  function switchLocale() {
    const next = locale === "en" ? "ur" : "en";
    saveLocale(next);
    onLocale(next);
    // Remember the choice on the server too, so notifications arrive in the same language (keeps the user's other channels).
    void ok(api.GET("/me/preferences"))
      .then((p) => ok(api.PUT("/me/preferences", { body: { locale: next, channels: p.channels } })))
      .catch(() => undefined);
  }

  return (
    <div className="shell">
      {me.support && (
        <div className="support-banner" role="status">
          <span>{t("support.banner", { name: organization ?? "", until: f.dateTime(me.support.expiresAt) })}</span>
          <Button variant="secondary" onClick={() => void leaveSupport().then(() => navigate("/"))}>{t("support.leave")}</Button>
        </div>
      )}
      <header className="shell__header">
        <button type="button" className="icon-btn shell__burger" aria-label={navOpen ? t("nav.close") : t("nav.open")} aria-expanded={navOpen} onClick={() => setNavOpen((v) => !v)}>
          <Icon name={navOpen ? "close" : "menu"} />
        </button>
        <strong className="shell__brand"><span className="logo-mark" aria-hidden="true">UF</span>{t("app.name")}</strong>
        {organization && <span className="shell__org">{organization}</span>}
        {menus.includes("complaints") && (
          <form className="shell__search" role="search" onSubmit={submitSearch}>
            <span className="shell__search-icon" aria-hidden="true"><Icon name="search" size={18} /></span>
            <input type="search" className="uf-field__input" aria-label={t("search")} placeholder={t("search")} value={search} onChange={(e) => setSearch(e.target.value)} />
          </form>
        )}
        <div className="shell__tools">
          <button type="button" className="pill-btn" onClick={switchLocale} aria-label={t("language")}>{locale === "en" ? "اردو" : "English"}</button>
          <button type="button" className="icon-btn" onClick={cycleTheme} aria-label={t("theme")} title={t(`theme.${theme}` as Key)}>
            <Icon name={theme === "dark" ? "moon" : theme === "light" ? "sun" : "auto"} />
          </button>
          <button type="button" className="icon-btn" onClick={() => setBell(true)} aria-label={t("notifications.title")} title={t("notifications.title")}>
            <Icon name="bell" />
            {unread > 0 && <span className="uf-badge icon-btn__badge" data-testid="unread">{unread}</span>}
          </button>
        </div>
      </header>
      <div className="shell__body">
        {navOpen && <div className="shell__scrim" onClick={() => setNavOpen(false)} aria-hidden="true" />}
        <div className="shell__nav" data-open={navOpen}>
          <Sidebar items={items} currentId={current} onNavigate={(id) => { setNavOpen(false); navigate(paths[id as MenuId]); }} />
          <div className="shell__user">
            <span className="avatar" aria-hidden="true">{initials(me.displayName)}</span>
            <span className="shell__user-text">
              <strong>{me.displayName}</strong>
              <small>{role ? t(`role.${role}` as Key) : ""}</small>
            </span>
            <button type="button" className="icon-btn icon-btn--on-dark" onClick={() => void signOut().then(() => navigate("/login"))} aria-label={t("signOut")} title={t("signOut")}>
              <Icon name="logout" />
            </button>
          </div>
        </div>
        <main className="shell__main" id="main">
          <Outlet />
        </main>
      </div>
      <NotificationSheet open={bell} onClose={() => setBell(false)} />
    </div>
  );
}

function NotificationSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const t = useT();
  const { notifications, markRead, markAllRead, unread } = useLive();
  const f = useFormat();
  const navigate = useNavigate();
  return (
    <Sheet open={open} title={t("notifications.title")} onClose={onClose}>
      {notifications.length === 0 ? <EmptyState title={t("notifications.empty")} /> : (
        <ul className="notifications">
          {notifications.map((n) => (
            <li key={n.id} className={n.readAt ? "" : "notifications__unread"}>
              <button type="button" onClick={() => { void markRead(n.id); if (n.link) { onClose(); navigate(n.link); } }}>
                <strong>{n.title}</strong>
                <span>{n.body}</span>
                <small className="muted">{f.dateTime(n.createdAt)}</small>
              </button>
            </li>
          ))}
        </ul>
      )}
      {unread > 0 && <Button variant="secondary" onClick={() => void markAllRead()}>{t("notifications.markAll")}</Button>}
      <PushSettings />
    </Sheet>
  );
}

