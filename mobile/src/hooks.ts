import { useIsFocused } from "@react-navigation/native";
import { useCallback, useEffect, useRef, useState } from "react";
import { AppState } from "react-native";
import { ApiError } from "./api";
import type { T } from "./i18n";
import type { Key } from "./i18n/strings";

/** Runs `load` when `deps` change; keeps the previous data on screen while reloading. */
export function useLoad<V>(load: () => Promise<V>, deps: unknown[]) {
  const [data, setData] = useState<V>();
  const [error, setError] = useState<ApiError>();
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  const latest = useRef(0);

  useEffect(() => {
    const id = ++latest.current;
    setLoading(true);
    load().then(
      (value) => { if (id === latest.current) { setData(value); setError(undefined); setLoading(false); } },
      (e: unknown) => { if (id === latest.current) { setError(e instanceof ApiError ? e : new ApiError(0, "network")); setLoading(false); } },
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);

  return { data, error, loading, reload: useCallback(() => setTick((n) => n + 1), []) };
}

/** Runs an action with busy and error state. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError>();
  const run = useCallback(async <V,>(fn: () => Promise<V>): Promise<V | undefined> => {
    setBusy(true);
    setError(undefined);
    try {
      return await fn();
    } catch (e) {
      setError(e instanceof ApiError ? e : new ApiError(0, "network"));
      return undefined;
    } finally {
      setBusy(false);
    }
  }, []);
  return { busy, error, run, clear: () => setError(undefined) };
}

/** A plain-language message for an API error; network problems say the work is saved. */
export function errorText(t: T, error: ApiError): string {
  if (error.code === "network" || error.status === 0) return t("error.network");
  const key = `error.${error.code}` as Key;
  const text = t(key);
  return text === key ? t("error") : text;
}

/**
 * Calls `reload` every `everyMs` while the screen is showing and the app is in front, so a status change appears without
 * pulling to refresh (RA-05). Push messages (MA-04) will make this quicker once they exist; until then this is the live update.
 */
export function usePolling(reload: () => void, everyMs = 20_000) {
  const focused = useIsFocused();
  const [active, setActive] = useState(AppState.currentState === "active");
  useEffect(() => {
    const sub = AppState.addEventListener("change", (s) => setActive(s === "active"));
    return () => sub.remove();
  }, []);
  useEffect(() => {
    if (!focused || !active) return;
    const id = setInterval(reload, everyMs);
    return () => clearInterval(id);
  }, [focused, active, reload, everyMs]);
}
