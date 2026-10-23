import { formatNumber, useLocale, type Catalog, type Locale, type Params } from "@urbanfix/ui";
import { en, ur, type Key } from "./strings";

export type { Key };

/** Portal strings layered over the shared UI strings. */
export const catalog: Catalog = { en, ur };

export type T = (key: Key, params?: Params) => string;

/** Translator for portal keys; a missing key is a compile error, not a blank label. */
export function useT(): T {
  return useLocale().t as T;
}

const LOCALE_KEY = "uf.locale";

export function initialLocale(): Locale {
  try {
    const saved = localStorage.getItem(LOCALE_KEY);
    if (saved === "en" || saved === "ur") return saved;
  } catch { /* storage unavailable */ }
  return typeof navigator !== "undefined" && navigator.language.startsWith("ur") ? "ur" : "en";
}

export function saveLocale(locale: Locale) {
  try { localStorage.setItem(LOCALE_KEY, locale); } catch { /* storage unavailable */ }
}

const intl = (locale: Locale) => (locale === "ur" ? "ur-PK-u-nu-latn" : "en-GB");

/** Date and time in the current language, always with Latin digits so numbers read the same in both. */
export function useFormat() {
  const { locale } = useLocale();
  return {
    locale,
    date: (iso: string | null | undefined) => (iso ? new Intl.DateTimeFormat(intl(locale), { dateStyle: "medium" }).format(new Date(iso)) : "—"),
    dateTime: (iso: string | null | undefined) => (iso ? new Intl.DateTimeFormat(intl(locale), { dateStyle: "medium", timeStyle: "short" }).format(new Date(iso)) : "—"),
    number: (n: number, options?: Intl.NumberFormatOptions) => formatNumber(locale, n, options),
    money: (n: number) => `Rs ${formatNumber(locale, n, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`,
    hours: (h: number) => formatNumber(locale, Math.round(h * 10) / 10),
  };
}
