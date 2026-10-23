// Framework-free locale helpers, shared by the web portal and the mobile app (FD-08).
export type Locale = "en" | "ur";
export type Direction = "ltr" | "rtl";

export const locales: readonly Locale[] = ["en", "ur"];

const rtlLocales: ReadonlySet<Locale> = new Set(["ur"]);

export const directionOf = (locale: Locale): Direction => (rtlLocales.has(locale) ? "rtl" : "ltr");

/** Urdu needs a larger size and taller lines than Latin to stay legible on small screens. */
export const scaleFor = (locale: Locale, scale: number): number => (locale === "ur" ? scale : 1);

export const formatNumber = (locale: Locale, value: number, options?: Intl.NumberFormatOptions): string =>
  new Intl.NumberFormat(locale === "ur" ? "ur-PK-u-nu-latn" : "en-PK", options).format(value);

export const isLocale = (value: unknown): value is Locale => value === "en" || value === "ur";
