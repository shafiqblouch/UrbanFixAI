import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "./api";

export interface Loaded<T> {
  data: T | undefined;
  error: ApiError | undefined;
  loading: boolean;
  reload: () => void;
}

/** Runs `load` when `deps` change; keeps the previous data on screen while reloading. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): Loaded<T> {
  const [data, setData] = useState<T>();
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

/** Runs an action with busy / error state, for buttons that call the API. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError>();
  const run = useCallback(async <T,>(fn: () => Promise<T>): Promise<T | undefined> => {
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

export function useDebounced<T>(value: T, ms = 300): T {
  const [v, setV] = useState(value);
  useEffect(() => { const h = setTimeout(() => setV(value), ms); return () => clearTimeout(h); }, [value, ms]);
  return v;
}
