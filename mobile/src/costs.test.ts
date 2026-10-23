import { describe, expect, it } from "vitest";
import { amountOf, emptyLine, fromServer, problemWith, toInput, totalOf, type Line } from "./costs";

const items = [{ id: "pipe", name: "PVC pipe", nameUr: null, unit: "metre", isActive: true, currentRate: { id: "r1", rate: 320.5, effectiveFrom: "", createdAt: "" } }];
const line = (patch: Partial<Line>): Line => ({ ...emptyLine("labour"), description: "Plumber", unitRate: "400", ...patch });

describe("cost lines (TA-06)", () => {
  it("prices materials from the price list and everything else from the typed rate", () => {
    expect(amountOf(line({ kind: "material", priceItemId: "pipe", quantity: "2.5", unitRate: "" }), items)).toBe(801.25);
    expect(amountOf(line({ quantity: "3" }), items)).toBe(1200);
  });

  it("accepts a decimal comma and half-typed numbers without crashing", () => {
    expect(amountOf(line({ quantity: "1,5" }), items)).toBe(600);
    expect(amountOf(line({ quantity: "2." }), items)).toBe(800);
    expect(amountOf(line({ quantity: "" }), items)).toBe(0);
    expect(amountOf(line({ quantity: "abc" }), items)).toBe(0);
  });

  it("totals to two decimals", () => {
    expect(totalOf([line({ quantity: "1.5", unitRate: "6.675" }), line({ quantity: "2", unitRate: "0.1" })], items)).toBe(10.21);
  });

  it("says what blocks saving a line", () => {
    expect(problemWith(line({ quantity: "0" }))).toBe("quantity");
    expect(problemWith(line({ kind: "material", priceItemId: "" }))).toBe("item"); // materials come from the list
    expect(problemWith(line({ kind: "material", priceItemId: "pipe" }))).toBeUndefined();
    expect(problemWith(line({ description: " " }))).toBe("description");
    expect(problemWith(line({ unitRate: "" }))).toBe("rate");
    expect(problemWith(line({ unitRate: "-5" }))).toBe("rate");
    expect(problemWith(line({}))).toBeUndefined();
  });

  it("sends the list item by id and never a typed rate for it", () => {
    expect(toInput(line({ kind: "material", priceItemId: "pipe", unitRate: "1", description: "" }))).toEqual({ kind: "material", priceItemId: "pipe", description: null, quantity: 1, unit: null, unitRate: null });
    expect(toInput(line({}))).toMatchObject({ priceItemId: null, description: "Plumber", unitRate: 400, quantity: 1 });
  });

  it("reads saved lines back for editing", () => {
    expect(fromServer({ id: "x", kind: "labour", description: "Plumber", priceItemId: null, quantity: 3, unit: "hour", unitRate: 400, amount: 1200 }))
      .toEqual({ kind: "labour", priceItemId: "", description: "Plumber", quantity: "3", unitRate: "400" });
  });
});
