import { createTranslator, directionOf, formatNumber, type Locale, type Params } from "@urbanfix/ui/core";
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { storage } from "../platform/device";
import { en, ur, type Key } from "./strings";

export type { Key };
export type T = (key: Key, params?: Params) => string;

const LOCALE_KEY = "uf.locale";

interface I18n {
  locale: Locale;
  /** True when text and rows run right to left. Layout is mirrored in JavaScript, so changing language needs no restart. */
  rtl: boolean;
  t: T;
  setLocale: (locale: Locale) => void;
  number: (n: number) => string;
}

const Ctx = createContext<I18n | null>(null);

export function I18nProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<Locale>("en");

  useEffect(() => {
    storage.getItem(LOCALE_KEY).then((saved) => { if (saved === "en" || saved === "ur") setLocaleState(saved); }, () => undefined);
  }, []);

  const setLocale = useCallback((next: Locale) => {
    setLocaleState(next);
    void storage.setItem(LOCALE_KEY, next).catch(() => undefined);
  }, []);

  const value = useMemo<I18n>(() => {
    const translate = createTranslator(locale, { en, ur });
    return { locale, rtl: directionOf(locale) === "rtl", t: translate as T, setLocale, number: (n) => formatNumber(locale, n) };
  }, [locale, setLocale]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useI18n(): I18n {
  const v = useContext(Ctx);
  if (!v) throw new Error("useI18n must be used inside <I18nProvider>");
  return v;
}

export const useT = (): T => useI18n().t;
