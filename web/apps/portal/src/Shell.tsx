import { Button, EmptyState, Sheet, Sidebar, useLocale, type NavItem } from "@urbanfix/ui";
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
  const f = useFormat();

  if (loading) return <p className="muted center" role="status">{t("loading")}</p>;
  if (!me?.organizationId) return <Navigate to="/login" replace />;

  const menus = menusFor(role);
  const current = menus.find((m) => location.pathname.startsWith(paths[m])) ?? menus[0] ?? "";
  const countFor = (m: MenuId): number | undefined =>
    m === "review" ? counts.review : m === "jobs" ? (role === "Technician" ? counts.jobs : counts.signoff) : undefined;
  const items: NavItem[] = menus.map((m) => ({ id: m, label: m === "jobs" && role === "Technician" ? t("menu.myJobs") : t(`menu.${m}` as Key), href: paths[m], count: countFor(m) }));
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
        <strong className="shell__brand"><span className="logo-mark" aria-hidden="true">UF</span>{t("app.name")}</strong>
        {organization && <span className="shell__org">{organization}</span>}
        {menus.includes("complaints") && (
          <form className="shell__search" role="search" onSubmit={submitSearch}>
            <input type="search" className="uf-field__input" aria-label={t("search")} placeholder={t("search")} value={search} onChange={(e) => setSearch(e.target.value)} />
          </form>
        )}
        <div className="shell__tools">
          <Button variant="ghost" onClick={switchLocale} aria-label={t("language")}>{locale === "en" ? "اردو" : "English"}</Button>
          <Button variant="ghost" onClick={cycleTheme} aria-label={t("theme")}>{t(`theme.${theme}` as Key)}</Button>
          <Button variant="ghost" onClick={() => setBell(true)} aria-label={t("notifications.title")}>
            {t("notifications.title")}{unread > 0 && <span className="uf-badge" data-testid="unread">{unread}</span>}
          </Button>
          <span className="muted">{me.displayName}</span>
          <Button variant="secondary" onClick={() => void signOut().then(() => navigate("/login"))}>{t("signOut")}</Button>
        </div>
      </header>
      <div className="shell__body">
        <Sidebar items={items} currentId={current} onNavigate={(id) => navigate(paths[id as MenuId])} />
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

