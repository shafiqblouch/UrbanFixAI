import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, getToken, loadToken, ok, setToken, type S } from "./api";
import { storage } from "./platform/device";
import { pickView, type View } from "./roles";

interface Session {
  me: S["MeResponse"] | undefined;
  /** The role and organization the app is acting as; undefined when the person has no role the app supports. */
  view: View | undefined;
  /** True until the first /me answer, so the app does not flash the sign-in screen. */
  loading: boolean;
  signIn: (token: string) => Promise<void>;
  refresh: () => Promise<void>;
  signOut: () => Promise<void>;
  /** The role switcher (DS-05): act as another membership, changing organization on the server if needed. */
  switchView: (view: View) => Promise<void>;
}

const Ctx = createContext<Session | null>(null);
const LAST_ROLE = "uf.lastRole";

export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<S["MeResponse"]>();
  const [view, setView] = useState<View>();
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    if (!getToken()) { setMe(undefined); setView(undefined); setLoading(false); return; }
    try {
      const profile = await ok(api.GET("/me"));
      const last = (await storage.getItem(LAST_ROLE).catch(() => null)) ?? undefined;
      setMe(profile);
      setView(pickView(profile.memberships, profile.organizationId, last));
    } catch {
      await setToken(undefined);
      setMe(undefined);
      setView(undefined);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void loadToken().then(load); }, [load]);

  const value = useMemo<Session>(() => ({
    me, view, loading,
    refresh: load,
    signIn: async (token) => { await setToken(token); setLoading(true); await load(); },
    signOut: async () => {
      try { await api.POST("/auth/logout"); } catch { /* the token is dropped either way */ }
      await setToken(undefined);
      setMe(undefined);
      setView(undefined);
    },
    switchView: async (next) => {
      if (next.organizationId !== me?.organizationId) await ok(api.POST("/auth/switch-organization", { body: { organizationId: next.organizationId } }));
      await storage.setItem(LAST_ROLE, next.role).catch(() => undefined);
      await load();
    },
  }), [me, view, loading, load]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useSession(): Session {
  const v = useContext(Ctx);
  if (!v) throw new Error("useSession must be used inside <SessionProvider>");
  return v;
}
