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
