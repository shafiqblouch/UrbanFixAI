import { useLocale } from "@urbanfix/ui";
import { useEffect, useRef, useState } from "react";

/**
 * Turn a GPS fix into a readable street address with OpenStreetMap's Nominatim reverse geocoder
 * (same provider as the map tiles, no API key). Results are cached in memory and in localStorage so
 * a place is only ever looked up once, and lookups are serialised with a gap between them to stay
 * inside Nominatim's "one request a second" usage policy.
 */

export type GeocodeStatus = "idle" | "loading" | "done" | "error";

/** Round to ~11 m so near-identical fixes share one cache entry and one lookup. */
const roundKey = (lat: number, lon: number, lang: string) => `${lat.toFixed(4)},${lon.toFixed(4)}@${lang}`;

const CACHE_PREFIX = "uf.geocode.";
const memory = new Map<string, string>();

function readCache(key: string): string | undefined {
  const hit = memory.get(key);
  if (hit !== undefined) return hit;
  try {
    const stored = localStorage.getItem(CACHE_PREFIX + key);
    if (stored != null) { memory.set(key, stored); return stored; }
  } catch { /* storage unavailable */ }
  return undefined;
}

function writeCache(key: string, address: string) {
  memory.set(key, address);
  try { localStorage.setItem(CACHE_PREFIX + key, address); } catch { /* storage unavailable, memory still helps */ }
}

// Serialise lookups: Nominatim asks for at most one request per second from a client.
const MIN_GAP_MS = 1100;
let chain: Promise<void> = Promise.resolve();
let lastAt = 0;

function schedule<T>(job: () => Promise<T>): Promise<T> {
  const run = chain.then(async () => {
    const wait = lastAt + MIN_GAP_MS - Date.now();
    if (wait > 0) await new Promise((r) => setTimeout(r, wait));
    lastAt = Date.now();
    return job();
  });
  // Keep the chain alive regardless of whether this job resolved or rejected.
  chain = run.then(() => undefined, () => undefined);
  return run;
}

async function reverse(lat: number, lon: number, lang: string, signal: AbortSignal): Promise<string> {
  const url = `https://nominatim.openstreetmap.org/reverse?format=jsonv2&zoom=18&addressdetails=1`
    + `&lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}&accept-language=${encodeURIComponent(lang)}`;
  const res = await fetch(url, { signal, headers: { Accept: "application/json" } });
  if (!res.ok) throw new Error(`geocode ${res.status}`);
  const data = (await res.json()) as { display_name?: string; error?: string };
  if (!data.display_name) throw new Error(data.error ?? "no address");
  return data.display_name;
}

export type Geocode = { address: string | null; status: GeocodeStatus };

/**
 * Reverse-geocode a position, or `{ status: "idle" }` when no position is given. The address comes
 * back as soon as it is known; the component shows the coordinates until then and if the lookup fails.
 */
export function useReverseGeocode(lat: number | null | undefined, lon: number | null | undefined): Geocode {
  const { locale } = useLocale();
  const [state, setState] = useState<Geocode>({ address: null, status: "idle" });

  // Track the current request so a stale lookup can never overwrite a newer one.
  const reqId = useRef(0);

  useEffect(() => {
    if (lat == null || lon == null) { setState({ address: null, status: "idle" }); return; }
    const key = roundKey(lat, lon, locale);
    const cached = readCache(key);
    if (cached !== undefined) { setState({ address: cached, status: "done" }); return; }

    const id = ++reqId.current;
    const controller = new AbortController();
    setState({ address: null, status: "loading" });

    schedule(() => reverse(lat, lon, locale, controller.signal))
      .then((address) => {
        writeCache(key, address);
        if (reqId.current === id) setState({ address, status: "done" });
      })
      .catch(() => {
        if (reqId.current === id && !controller.signal.aborted) setState({ address: null, status: "error" });
      });

    return () => { controller.abort(); };
  }, [lat, lon, locale]);

  return state;
}
