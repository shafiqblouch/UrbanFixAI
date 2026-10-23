import createClient from "openapi-fetch";
import type { paths } from "./schema";

export type { paths, components } from "./schema";

/** Typed client for the UrbanFix core API. Attach the session token with `setToken`. */
export function createApiClient(baseUrl: string, getToken: () => string | undefined = () => undefined) {
  const client = createClient<paths>({ baseUrl });
  client.use({
    onRequest({ request }) {
      const token = getToken();
      if (token) request.headers.set("Authorization", `Bearer ${token}`);
      return request;
    },
  });
  return client;
}

/** An error response from the API, with the machine-readable code the server sent (`error`, or `http_<status>`). */
export class ApiError extends Error {
  constructor(public status: number, public code: string, public body?: unknown) {
    super(code);
  }
}

type Result<T> = { data?: T; error?: unknown; response: Response };

async function check<T>(p: Promise<Result<T>>): Promise<{ data: T; response: Response }> {
  const r = await p;
  if (r.error !== undefined || !r.response.ok) {
    const body = r.error as { error?: string } | undefined;
    throw new ApiError(r.response.status, body?.error ?? `http_${r.response.status}`, r.error);
  }
  return { data: r.data as T, response: r.response };
}

/** Unwraps a typed call, throwing `ApiError` on any failure. Shared by the portal and the mobile app. */
export async function ok<T>(p: Promise<Result<T>>): Promise<T> {
  return (await check(p)).data;
}

/** Like `ok`, for paged lists: also returns the X-Total-Count header. */
export async function okPaged<T>(p: Promise<Result<T>>): Promise<{ items: T; total: number }> {
  const { data, response } = await check(p);
  return { items: data, total: Number(response.headers.get("X-Total-Count") ?? 0) };
}
