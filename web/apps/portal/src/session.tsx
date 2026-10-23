import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, getToken, ok, setToken, stashToken, takeStash, type S } from "./api";
import type { Role } from "./roles";

export interface SessionValue {
  me: S["MeResponse"] | undefined;
  role: Role | undefined;
  /** True until the first /me answer, so the shell does not flash the login page. */
  loading: boolean;
  /** Stores a new session token and loads the user. */
  signIn: (token: string) => Promise<void>;
  refresh: () => Promise<void>;
  /** Switches to a support session token; the person's own token is kept aside (PA-05). */
  enterSupport: (token: string, supportSessionId: string) => Promise<void>;
  /** Ends the support session and goes back to the person's own session. */
  leaveSupport: () => Promise<void>;
  signOut: () => Promise<void>;
}

const Ctx = createContext<SessionValue | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<S["MeResponse"]>();
  const [loading, setLoading] = useState(!!getToken());

  const refresh = useCallback(async () => {
    if (!getToken()) { setMe(undefined); setLoading(false); return; }
    try {
      setMe(await ok(api.GET("/me")));
    } catch {
      setToken(undefined);
      setMe(undefined);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const value = useMemo<SessionValue>(() => {
    const membership = me?.memberships.find((m) => m.organizationId === me.organizationId);
    return {
      me,
      role: membership?.role as Role | undefined,
      loading,
      refresh,
      signIn: async (token) => { setToken(token); setLoading(true); await refresh(); },
      enterSupport: async (token, supportSessionId) => { stashToken(supportSessionId); setToken(token); setLoading(true); await refresh(); },
      leaveSupport: async () => {
        const own = takeStash();
        if (!own) { setToken(undefined); setMe(undefined); return; }
        setToken(own.token);
        try { await api.POST("/admin/support-sessions/{id}/end", { params: { path: { id: own.supportSessionId } } }); } catch { /* it ends by itself at its time */ }
        setLoading(true);
        await refresh();
      },
      signOut: async () => {
        try { await api.POST("/auth/logout"); } catch { /* the token is dropped either way */ }
        setToken(undefined);
        setMe(undefined);
      },
    };
  }, [me, loading, refresh]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useSession(): SessionValue {
  const v = useContext(Ctx);
  if (!v) throw new Error("useSession must be used inside <SessionProvider>");
  return v;
}
