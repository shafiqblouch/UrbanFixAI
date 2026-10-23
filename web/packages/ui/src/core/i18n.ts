import type { Locale } from "./locale";
import { messages, type MessageKey } from "./messages";

export type Params = Record<string, string | number>;
/** Shared UI keys are checked; an app may add its own through the catalog, so any string is accepted too. */
export type Translate = (key: MessageKey | (string & {}), params?: Params) => string;
export type Catalog = Partial<Record<Locale, Record<string, string>>>;

/**
 * Builds a translator for one locale. App-level strings can be layered on top of the shared UI
 * strings with `extra`; a missing Urdu string falls back to English rather than showing a key.
 */
export function createTranslator(locale: Locale, extra: Catalog = {}): Translate {
  const lookup = (l: Locale, key: string): string | undefined =>
    extra[l]?.[key] ?? (messages[l] as Record<string, string>)[key];
  return (key, params) => {
    const template = lookup(locale, key) ?? lookup("en", key) ?? key;
    return template.replace(/\{(\w+)\}/g, (whole, name: string) => String(params?.[name] ?? whole));
  };
}
