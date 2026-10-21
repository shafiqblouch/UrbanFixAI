import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createApiClient } from "@urbanfix/api-client";

// Compiles only while the portal matches the API contract (FD-07).
export const api = createApiClient(import.meta.env.VITE_API_URL ?? "http://localhost:5000");

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <h1>UrbanFix</h1>
  </StrictMode>,
);
