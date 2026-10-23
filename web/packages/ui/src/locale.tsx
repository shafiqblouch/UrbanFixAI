import { createContext, useContext, useEffect, useMemo, type ReactNode } from "react";
import { createTranslator, directionOf, type Catalog, type Direction, type Locale, type Translate } from "./core";

export interface LocaleValue {
  locale: Locale;
  dir: Direction;
  t: Translate;
}

const LocaleContext = createContext<LocaleValue | null>(null);

export function LocaleProvider({
  locale,
  catalog,
  syncDocument = false,
  children,
}: {
  locale: Locale;
  catalog?: Catalog;
  /** Set lang/dir on <html> so scrollbars, native controls and text selection follow the language. */
  syncDocument?: boolean;
  children: ReactNode;
}) {
  const value = useMemo<LocaleValue>(
    () => ({ locale, dir: directionOf(locale), t: createTranslator(locale, catalog) }),
    [locale, catalog],
  );

  useEffect(() => {
    if (!syncDocument) return;
    document.documentElement.lang = value.locale;
    document.documentElement.dir = value.dir;
  }, [syncDocument, value.locale, value.dir]);

  return (
    <LocaleContext.Provider value={value}>
      <div className="uf-root" lang={value.locale} dir={value.dir}>
        {children}
      </div>
    </LocaleContext.Provider>
  );
}

export function useLocale(): LocaleValue {
  const value = useContext(LocaleContext);
  if (!value) throw new Error("useLocale must be used inside <LocaleProvider>");
  return value;
}
