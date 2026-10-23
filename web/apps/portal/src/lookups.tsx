import { createContext, useContext, useMemo, type ReactNode } from "react";
import { api, ok, type S } from "./api";
import { useLoad } from "./hooks";
import { useLocale } from "@urbanfix/ui";

interface Lookups {
  categories: S["CategoryDto"][];
  departments: S["DepartmentDto"][];
  teams: S["TeamDto"][];
  locations: S["LocationDto"][];
  categoryName: (id: string | null | undefined) => string;
  teamName: (id: string | null | undefined) => string;
  reload: () => void;
}

const Ctx = createContext<Lookups | null>(null);

const safe = <T,>(p: Promise<T>): Promise<T | []> => p.catch(() => []);

/** Reference data used by filters and forms (categories, departments, teams, locations). */
export function LookupProvider({ children }: { children: ReactNode }) {
  const { locale } = useLocale();
  const state = useLoad(async () => {
    const [categories, departments, teams, locations] = await Promise.all([
      safe(ok(api.GET("/categories"))), safe(ok(api.GET("/departments"))), safe(ok(api.GET("/teams"))), safe(ok(api.GET("/locations/tree"))),
    ]);
    return { categories, departments, teams, locations } as Pick<Lookups, "categories" | "departments" | "teams" | "locations">;
  }, []);

  const value = useMemo<Lookups>(() => {
    const d = state.data ?? { categories: [], departments: [], teams: [], locations: [] };
    const cat = new Map(d.categories.map((c) => [c.id, (locale === "ur" && c.nameUr) || c.nameEn]));
    const team = new Map(d.teams.map((t) => [t.id, t.name]));
    return { ...d, categoryName: (id) => (id ? cat.get(id) ?? "—" : "—"), teamName: (id) => (id ? team.get(id) ?? "—" : "—"), reload: state.reload };
  }, [state.data, state.reload, locale]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useLookups(): Lookups {
  const v = useContext(Ctx);
  if (!v) throw new Error("useLookups must be used inside <LookupProvider>");
  return v;
}
