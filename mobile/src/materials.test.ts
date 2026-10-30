import { describe, expect, it } from "vitest";
import { emptyRequestLine, requestBody, requestIsValid, type RequestLine } from "./materials";

const line = (patch: Partial<RequestLine>): RequestLine => ({ ...emptyRequestLine(), ...patch });

describe("material requests (TA-09)", () => {
  it("needs at least one item with a quantity", () => {
    expect(requestIsValid([emptyRequestLine()])).toBe(false);
    expect(requestIsValid([line({ itemId: "pipe", quantity: "2" })])).toBe(true);
  });

  it("refuses a half-filled line", () => {
    expect(requestIsValid([line({ itemId: "pipe", quantity: "" })])).toBe(false);
    expect(requestIsValid([line({ itemId: "", quantity: "3" })])).toBe(false);
    expect(requestIsValid([line({ itemId: "pipe", quantity: "0" })])).toBe(false);
  });

  it("accepts extra complete lines alongside untouched empty ones", () => {
    expect(requestIsValid([line({ itemId: "pipe", quantity: "2" }), emptyRequestLine()])).toBe(true);
  });

  it("sends only the complete lines, quantities as numbers and a decimal comma allowed", () => {
    const lines = [line({ itemId: "pipe", quantity: "2,5" }), emptyRequestLine(), line({ itemId: "clamp", quantity: "4" })];
    expect(requestBody(lines)).toEqual([
      { itemId: "pipe", quantity: 2.5 },
      { itemId: "clamp", quantity: 4 },
    ]);
  });
});
