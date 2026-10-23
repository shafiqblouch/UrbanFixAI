import { ApiError, createApiClient, ok, okPaged, type components } from "@urbanfix/api-client";

// The error type and response unwrapping are shared with the mobile app.
export { ApiError, ok, okPaged };

export type S = components["schemas"];

const KEY = "uf.token";
const read = (): string | undefined => {
  try { return sessionStorage.getItem(KEY) ?? undefined; } catch { return undefined; }
};
let token = read();

export const getToken = () => token;
export function setToken(value: string | undefined) {
  token = value;
  try { value ? sessionStorage.setItem(KEY, value) : sessionStorage.removeItem(KEY); } catch { /* private mode: keep it in memory only */ }
}

// Same-origin, but absolute: Request objects (and so the test environment) need a full URL.
export const api = createApiClient(new URL("/api", globalThis.location?.href ?? "http://localhost").href, () => token);

/** Multipart upload (photos). openapi-fetch cannot type multipart bodies, so this uses fetch directly. */
export async function upload(path: string, file: File): Promise<void> {
  const form = new FormData();
  form.append("file", file);
  const res = await fetch(`/api${path}`, { method: "POST", body: form, headers: token ? { Authorization: `Bearer ${token}` } : {} });
  if (!res.ok) throw new ApiError(res.status, ((await res.json().catch(() => ({}))) as { error?: string }).error ?? `http_${res.status}`);
}

/**
 * Downloads a file the API builds (exports, summaries). A plain link cannot carry the sign-in token, so the file is fetched
 * with it and handed to the browser as a download.
 */
export async function download(path: string, query: Record<string, string | undefined> = {}): Promise<void> {
  const qs = new URLSearchParams(Object.entries(query).filter((e): e is [string, string] => e[1] !== undefined && e[1] !== "")).toString();
  const res = await fetch(`/api${path}${qs ? `?${qs}` : ""}`, { headers: token ? { Authorization: `Bearer ${token}` } : {} });
  if (!res.ok) throw new ApiError(res.status, ((await res.json().catch(() => ({}))) as { error?: string }).error ?? "download_failed");
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(res.headers.get("content-disposition") ?? "")?.[1] ?? "download";
  const url = URL.createObjectURL(await res.blob());
  const link = document.createElement("a");
  link.href = url;
  link.download = decodeURIComponent(name);
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

// A platform person opening a support session swaps their token for the support one and keeps their own aside, so they can go back (PA-05).
const STASH = "uf.token.platform";
const STASH_SESSION = "uf.support.session";
export function stashToken(supportSessionId: string) {
  try { sessionStorage.setItem(STASH, token ?? ""); sessionStorage.setItem(STASH_SESSION, supportSessionId); } catch { /* private mode: the person signs in again */ }
}
export function takeStash(): { token: string; supportSessionId: string } | undefined {
  try {
    const saved = sessionStorage.getItem(STASH);
    const id = sessionStorage.getItem(STASH_SESSION);
    sessionStorage.removeItem(STASH); sessionStorage.removeItem(STASH_SESSION);
    return saved && id ? { token: saved, supportSessionId: id } : undefined;
  } catch { return undefined; }
}
