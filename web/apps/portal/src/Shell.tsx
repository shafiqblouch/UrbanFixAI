import { Button, EmptyState, Sheet, Sidebar, useLocale, type NavItem } from "@urbanfix/ui";
import { Icon, initials } from "./icons";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, Navigate, Outlet, useLocation, useNavigate, useSearchParams } from "react-router-dom";
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
    try { return (localStorage.getItem(THEME_KEY) as Theme | null) ?? "dark"; } catch { return "dark"; }
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
  dashboard: "/dashboard", complaints: "/complaints", map: "/map", review: "/review", new: "/new", jobs: "/jobs", stock: "/stock", approvals: "/approvals", notices: "/notices", config: "/config", audit: "/audit",
};

/** Screens that sit under the "System" heading; "New complaint" is the button above the menu instead of a menu entry. */
const SYSTEM: MenuId[] = ["approvals", "notices", "config", "audit"];

/** Brand, role-based sidebar with count badges, top bar, and the signed-in guard (WP-01). */
export function Shell({ onLocale }: { onLocale: (l: "en" | "ur") => void }) {
  const t = useT();
  const { me, role, loading, signOut, leaveSupport } = useSession();
  const { counts, unread } = useLive();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const location = useLocation();
  const [theme, cycleTheme] = useTheme();
  const [search, setSearch] = useState("");
  const [urlParams, setUrlParams] = useSearchParams();
  const [bell, setBell] = useState(false);
  const [navOpen, setNavOpen] = useState(false);
  const searchBox = useRef<HTMLInputElement>(null);
  const f = useFormat();

  // "/" or Ctrl/Cmd+K jumps to the search box, as the hint in it says.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      const typing = /^(input|textarea|select)$/i.test((e.target as HTMLElement)?.tagName ?? "");
      if ((e.key === "k" && (e.ctrlKey || e.metaKey)) || (e.key === "/" && !typing)) { e.preventDefault(); searchBox.current?.focus(); }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  if (loading) return <p className="muted center" role="status">{t("loading")}</p>;
  if (!me?.organizationId) return <Navigate to="/login" replace />;

  const menus = menusFor(role);
  const navMenus = menus.filter((m) => m !== "new");
  const current = navMenus.find((m) => location.pathname.startsWith(paths[m])) ?? "";
  const countFor = (m: MenuId): number | undefined =>
    m === "review" ? counts.review : m === "complaints" ? counts.complaints : m === "jobs" ? (role === "Technician" ? counts.jobs : counts.overdue > 0 ? counts.overdue : counts.signoff) : undefined;
  const items: NavItem[] = navMenus.map((m) => ({
    id: m, label: m === "jobs" && role === "Technician" ? t("menu.myJobs") : t(`menu.${m}` as Key), href: paths[m], count: countFor(m), icon: <Icon name={m} />,
    section: SYSTEM.includes(m) ? t("shell.system") : undefined,
    // Complaints: a quiet chip; Review: the accent; Jobs: a warning when something is overdue.
    tone: m === "complaints" ? "quiet" : m === "jobs" && role !== "Technician" && counts.overdue > 0 ? "alert" : "accent",
    countText: m === "jobs" && role !== "Technician" && counts.overdue > 0 ? `${f.number(counts.overdue)} ${t("nav.alert")}` : undefined,
  }));
  const organization = me.memberships.find((m) => m.organizationId === me.organizationId)?.organizationName;
  const today = new Intl.DateTimeFormat(f.locale === "ur" ? "ur-PK-u-nu-latn" : "en-GB", { weekday: "short", day: "numeric", month: "short", year: "numeric" }).format(new Date());

  // On the complaints list the box edits that list's own filter (so it filters as you type); elsewhere it jumps there on Enter.
  const onList = location.pathname === "/complaints";
  const searchValue = onList ? urlParams.get("q") ?? "" : search;
  function changeSearch(value: string) {
    if (!onList) { setSearch(value); return; }
    const next = new URLSearchParams(urlParams);
    if (value) next.set("q", value); else next.delete("q");
    next.delete("page");
    setUrlParams(next, { replace: true });
  }

  function submitSearch(e: FormEvent) {
    e.preventDefault();
    if (!onList) navigate(`/complaints?q=${encodeURIComponent(search.trim())}`);
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
      {navOpen && <div className="shell__scrim" onClick={() => setNavOpen(false)} aria-hidden="true" />}
      <aside className="shell__nav" data-open={navOpen}>
        <div className="shell__brandrow">
          <strong className="shell__brand">
            <span className="logo-mark" aria-hidden="true">UF</span>
            <span className="shell__brand-text"><span>{t("app.name")}</span><small>{t("shell.tagline")}</small></span>
          </strong>
          {organization && <span className="shell__org" title={organization}><span className="shell__org-dot" aria-hidden="true" /><span className="shell__org-name">{organization}</span><Icon name="chevron" size={11} /></span>}
        </div>
        {menus.includes("new") && (
          <Link to="/new" className="shell__new" onClick={() => setNavOpen(false)}><Icon name="new" size={16} />{t("menu.new")}</Link>
        )}
        <div className="shell__navscroll">
          <Sidebar items={items} currentId={current} onNavigate={(id) => { setNavOpen(false); navigate(paths[id as MenuId]); }} />
        </div>
        <div className="shell__user">
          <span className="avatar" aria-hidden="true">{initials(me.displayName)}</span>
          <span className="shell__user-text">
            <strong>{me.displayName}</strong>
            <small>{role ? t(`role.${role}` as Key) : ""}</small>
          </span>
          <button type="button" className="icon-btn icon-btn--sm" onClick={() => void signOut().then(() => navigate("/login"))} aria-label={t("signOut")} title={t("signOut")}>
            <Icon name="logout" size={16} />
          </button>
        </div>
      </aside>
      <div className="shell__col">
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
          {menus.includes("complaints") && (
            <form className="shell__search" role="search" onSubmit={submitSearch}>
              <span className="shell__search-icon" aria-hidden="true"><Icon name="search" size={15} /></span>
              <input ref={searchBox} type="search" className="uf-field__input" aria-label={t("search")} placeholder={t("shell.searchHint")} value={searchValue} onChange={(e) => changeSearch(e.target.value)} />
              <kbd className="shell__kbd" aria-hidden="true">Ctrl K</kbd>
            </form>
          )}
          <div className="shell__tools">
            <button type="button" className="pill-btn" onClick={switchLocale} aria-label={t("language")}><Icon name="globe" size={14} />{locale === "en" ? "اردو" : "English"}</button>
            <button type="button" className="icon-btn icon-btn--sm icon-btn--box" onClick={cycleTheme} aria-label={t("theme")} title={t(`theme.${theme}` as Key)}>
              <Icon name={theme === "dark" ? "moon" : theme === "light" ? "sun" : "auto"} size={16} />
            </button>
            <button type="button" className="icon-btn icon-btn--sm icon-btn--box" onClick={() => setBell(true)} aria-label={t("notifications.title")} title={t("notifications.title")}>
              <Icon name="bell" size={16} />
              {unread > 0 && <span className="uf-badge icon-btn__badge" data-testid="unread">{unread}</span>}
            </button>
            <span className="shell__date">{today}</span>
          </div>
        </header>
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
