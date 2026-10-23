import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, getToken, ok, type S } from "./api";
import { isSupervising } from "./roles";
import { useSession } from "./session";

type Notification = S["NotificationDto"];

export interface LiveValue {
  notifications: Notification[];
  unread: number;
  markRead: (id: string) => Promise<void>;
  markAllRead: () => Promise<void>;
  /** Badge counts for the sidebar (WP-01). */
  counts: { review: number; signoff: number; jobs: number };
}

const Ctx = createContext<LiveValue | null>(null);

/** Notifications and sidebar counts. New notifications arrive over SignalR without a refresh (NT-05) and also refresh the counts. */
export function LiveProvider({ children }: { children: ReactNode }) {
  const { me, role } = useSession();
  const [notifications, setNotifications] = useState<Notification[]>([]);
  const [unread, setUnread] = useState(0);
  const [counts, setCounts] = useState({ review: 0, signoff: 0, jobs: 0 });
  const signedIn = !!me?.organizationId;

  const loadInbox = useCallback(async () => {
    const inbox = await ok(api.GET("/notifications", { params: { query: { limit: 30 } } }));
    setNotifications(inbox.items);
    setUnread(inbox.unread);
  }, []);

  const loadCounts = useCallback(async () => {
    const next = { review: 0, signoff: 0, jobs: 0 };
    try {
      if (role === "Manager" || role === "Supervisor" || role === "Office") next.review = (await ok(api.GET("/review-queue", { params: { query: { limit: 200 } } }))).length;
      if (isSupervising(role)) next.signoff = (await ok(api.GET("/jobs", { params: { query: { status: "PendingVerification", limit: 200 } } }))).length;
      if (role === "Technician") next.jobs = (await ok(api.GET("/jobs", { params: { query: { mine: true, limit: 200 } } }))).filter((j) => ["Open", "Accepted", "InProgress"].includes(j.job.status)).length;
    } catch { /* counts are a convenience; the screens report real errors */ }
    setCounts(next);
  }, [role]);

  useEffect(() => {
    if (!signedIn) return;
    void loadInbox().catch(() => undefined);
    void loadCounts();
    const poll = setInterval(() => void loadCounts(), 60_000);

    // Live updates are a bonus: if the socket cannot be built or opened, the 60-second poll still keeps the counts fresh.
    let hub: ReturnType<HubConnectionBuilder["build"]> | undefined;
    try {
      hub = new HubConnectionBuilder()
        .withUrl(new URL("/api/hubs/notifications", globalThis.location?.href ?? "http://localhost").href, { accessTokenFactory: () => getToken() ?? "" })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.None)
        .build();
      hub.on("notification", (n: Notification) => {
        setNotifications((list) => [n, ...list.filter((x) => x.id !== n.id)].slice(0, 30));
        setUnread((u) => u + 1);
        void loadCounts();
      });
      hub.start().catch(() => undefined);
    } catch { hub = undefined; }
    return () => {
      clearInterval(poll);
      if (hub && hub.state !== HubConnectionState.Disconnected) void hub.stop();
    };
  }, [signedIn, loadInbox, loadCounts]);

  const value = useMemo<LiveValue>(() => ({
    notifications, unread, counts,
    markRead: async (id) => {
      await ok(api.POST("/notifications/{id}/read", { params: { path: { id } } }));
      setNotifications((list) => list.map((n) => (n.id === id ? { ...n, readAt: new Date().toISOString() } : n)));
      setUnread((u) => Math.max(0, u - 1));
    },
    markAllRead: async () => {
      await ok(api.POST("/notifications/read-all"));
      setNotifications((list) => list.map((n) => ({ ...n, readAt: n.readAt ?? new Date().toISOString() })));
      setUnread(0);
    },
  }), [notifications, unread, counts]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useLive(): LiveValue {
  const v = useContext(Ctx);
  if (!v) throw new Error("useLive must be used inside <LiveProvider>");
  return v;
}
