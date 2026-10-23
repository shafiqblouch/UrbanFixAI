import { ApiError, createApiClient, ok, okPaged, type components } from "@urbanfix/api-client";
import { secrets } from "./platform/device";
import { FatalUploadError, OffsetMismatch, RetryableError, type UploadTransport } from "./upload/resumable";
import type { QueueItem } from "./upload/queue";

export { ApiError, ok, okPaged };
export type S = components["schemas"];

// 10.0.2.2 reaches the host machine from the Android emulator; set EXPO_PUBLIC_API_URL for a real device or a server.
export const API_URL = process.env.EXPO_PUBLIC_API_URL ?? "http://10.0.2.2:5181";

const TOKEN_KEY = "uf.token";
let token: string | undefined;

export const getToken = () => token;

export async function loadToken(): Promise<string | undefined> {
  token = (await secrets.get(TOKEN_KEY)) ?? undefined;
  return token;
}

export async function setToken(value: string | undefined): Promise<void> {
  token = value;
  if (value) await secrets.set(TOKEN_KEY, value);
  else await secrets.delete(TOKEN_KEY);
}

export const api = createApiClient(API_URL, () => token);

const auth = (): Record<string, string> => (token ? { Authorization: `Bearer ${token}` } : {});

/** Turns a failed HTTP call into the kind of error the uploader understands. */
async function failure(res: Response): Promise<Error> {
  const body = (await res.json().catch(() => ({}))) as { error?: string; receivedBytes?: number };
  if (res.status === 409 && body.error === "offset_mismatch" && typeof body.receivedBytes === "number") return new OffsetMismatch(body.receivedBytes);
  // Server trouble, a rate limit or a slow link is worth another try; a refusal (too large, wrong type, signed out) is not.
  if (res.status >= 500 || res.status === 408 || res.status === 429) return new RetryableError(`http_${res.status}`);
  return new FatalUploadError(body.error ?? `http_${res.status}`);
}

async function send(input: string, init: RequestInit): Promise<Response> {
  try {
    return await fetch(`${API_URL}${input}`, { ...init, headers: { ...auth(), ...init.headers } });
  } catch {
    throw new RetryableError("network"); // no signal, DNS, timeout
  }
}

/** The chunked-upload endpoints (`/uploads`) plus the call that attaches the finished file to a complaint or a job. */
export function uploadTransportFor(item: QueueItem): UploadTransport {
  return {
    async start(mimeType, totalBytes) {
      const res = await send("/uploads", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ mimeType, totalBytes }) });
      if (!res.ok) throw await failure(res);
      const body = (await res.json()) as S["UploadDto"];
      return { id: body.id, receivedBytes: body.receivedBytes };
    },
    async status(id) {
      const res = await send(`/uploads/${id}`, { method: "GET" });
      if (res.status === 404) return undefined;
      if (!res.ok) throw await failure(res);
      const body = (await res.json()) as S["UploadDto"];
      return { receivedBytes: body.receivedBytes, totalBytes: body.totalBytes };
    },
    async append(id, offset, chunk) {
      const res = await send(`/uploads/${id}?offset=${offset}`, {
        method: "PUT", headers: { "content-type": "application/octet-stream" }, body: chunk as unknown as BodyInit,
      });
      if (!res.ok) throw await failure(res);
      return { receivedBytes: ((await res.json()) as S["UploadDto"]).receivedBytes };
    },
    async finish(id) {
      const t = item.target;
      const path = t.kind === "job" ? `/jobs/${t.id}/media/from-upload/${id}?stage=${t.stage}` : `/complaints/${t.id}/media/from-upload/${id}`;
      const res = await send(path, { method: "POST" });
      if (!res.ok) throw await failure(res);
    },
  };
}

/** Turns a short recording into text to correct before saving (TA-08). Nothing is stored on the server. */
export async function transcribeVoice(uri: string, language?: "en" | "ur"): Promise<S["TranscriptDto"]> {
  const form = new FormData();
  form.append("file", { uri, name: "note.m4a", type: "audio/mp4" } as unknown as Blob);
  let res: Response;
  try {
    res = await fetch(`${API_URL}/voice/transcribe${language ? `?language=${language}` : ""}`, { method: "POST", headers: auth(), body: form });
  } catch {
    throw new ApiError(0, "network");
  }
  const body = (await res.json().catch(() => ({}))) as { error?: string };
  if (!res.ok) throw new ApiError(res.status, body.error ?? `http_${res.status}`, body);
  return body as S["TranscriptDto"];
}
