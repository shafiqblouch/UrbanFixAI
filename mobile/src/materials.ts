// Building a material request for a job (TA-09): the technician picks items from the store and a quantity for each.
// Kept apart from the screen so the rules can be tested without rendering.

export type RequestLine = { itemId: string; quantity: string };

export const emptyRequestLine = (): RequestLine => ({ itemId: "", quantity: "" });

/** A line the technician has begun to fill: chosen an item or typed a quantity. */
const started = (l: RequestLine): boolean => l.itemId !== "" || l.quantity.trim() !== "";

/** A line ready to send: an item and a quantity above zero. A decimal comma is accepted, like the cost lines. */
const complete = (l: RequestLine): boolean => l.itemId !== "" && Number(l.quantity.replace(",", ".")) > 0;

/** Can the request be sent? At least one complete line, and no half-filled one. */
export function requestIsValid(lines: RequestLine[]): boolean {
  const begun = lines.filter(started);
  return begun.length > 0 && begun.every(complete);
}

/** The body for POST /stock/requests: only the complete lines, quantities as numbers. */
export function requestBody(lines: RequestLine[]): { itemId: string; quantity: number }[] {
  return lines.filter(complete).map((l) => ({ itemId: l.itemId, quantity: Number(l.quantity.replace(",", ".")) }));
}
