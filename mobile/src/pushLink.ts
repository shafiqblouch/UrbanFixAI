// What to open when someone taps a notification (MA-04). The server sends the link as a path such as "/complaints/{id}" or "/jobs/{id}".
import type { MobileRole } from "./roles";

export type Target =
  | { name: "ComplaintDetail"; params: { id: string } }
  | { name: "JobDetail"; params: { id: string } }
  | { name: "Tabs"; params: { screen: "notices" | "today" } };

const UUID = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

/** The screen a link opens for this role, or undefined when the app has none (the person stays where they are). */
export function targetFor(link: string | null | undefined, role: MobileRole): Target | undefined {
  const path = (link ?? "").split(/[?#]/)[0];
  const complaint = new RegExp(`^/complaints/(${UUID})$`).exec(path);
  if (complaint && role === "Resident") return { name: "ComplaintDetail", params: { id: complaint[1] } };
  const job = new RegExp(`^/jobs/(${UUID})$`).exec(path);
  if (job && role === "Technician") return { name: "JobDetail", params: { id: job[1] } };
  // A notice, or any other message with no screen of its own, lands on the list that holds it.
  return role === "Resident" ? { name: "Tabs", params: { screen: "notices" } } : undefined;
}
