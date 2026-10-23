import { describe, expect, it } from "vitest";
import { checklistProgress, completionBlockers } from "./jobRules";
import { normalizePhone, resendWait } from "./phone";
import { EMERGENCY_NUMBER, isSafetyText, safetyTermsIn } from "./safety";

describe("emergency prompt (RA-08)", () => {
  // The same phrases the server's test set flags: if one of these stops being flagged, the prompt would be missing exactly when it matters.
  it.each([
    "A live wire is hanging down near the school gate",
    "There is an exposed wire on the pole outside house 14",
    "Child got an electric shock from the street light",
    "The transformer is sparking and making noise",
    "Smoke is coming out of the electric panel",
    "I can smell gas in the lane, possible gas leak",
    "Sewage overflow is flooding the road and entering houses",
    "The manhole cover is missing, an open manhole on the main road",
    "A tree has fallen on the cars and someone is injured",
    "بجلی کی ننگی تار لٹک رہی ہے",
    "کھمبے سے چنگاری نکل رہی ہے اور آگ لگنے کا خطرہ ہے",
    "گلی میں کھلا مین ہول ہے",
    "دیوار گر گئی ہے اور ایک بچہ زخمی ہوا",
    "گیس کی بو آ رہی ہے",
    "Gali mein nangi taar latak rahi hai",
    "khamba gir gaya hai aur karant hai",
    "Diwar gir gayi, ek bacha zakhmi hai",
  ])("warns for: %s", (text) => expect(isSafetyText(text)).toBe(true));

  it.each([
    "The tap in the park is dripping",
    "Street light is not working since Monday",
    "Garbage has not been collected this week",
    "پارک کی بینچ ٹوٹی ہوئی ہے",
    "Pani nahi aa raha subah se",
    "",
  ])("stays quiet for: %s", (text) => expect(isSafetyText(text)).toBe(false));

  it("names what it found and offers the rescue number", () => {
    expect(safetyTermsIn("Live wire and a gas leak")).toEqual(expect.arrayContaining(["live wire", "gas leak"]));
    expect(EMERGENCY_NUMBER).toBe("1122");
  });
});

describe("phone numbers (RA-04)", () => {
  it.each([
    ["0300 1234567", "+923001234567"],
    ["+92 300 1234567", "+923001234567"],
    ["0092-300-1234567", "+923001234567"],
    ["923001234567", "+923001234567"],
    ["+44 7700 900123", "+447700900123"],
  ])("reads %s as %s", (input, expected) => expect(normalizePhone(input)).toBe(expected));

  it.each(["12345", "abc", "", "   ", "+0123456789"])("rejects %j", (input) => expect(normalizePhone(input)).toBeUndefined());

  it("counts down the wait before another code can be asked for", () => {
    expect(resendWait(undefined, 5000)).toBe(0);
    expect(resendWait(0, 10_000)).toBe(50);
    expect(resendWait(0, 59_100)).toBe(1);
    expect(resendWait(0, 61_000)).toBe(0);
  });
});

describe("what a job still needs (TA-07)", () => {
  const base = { before: 0, after: 0, checklist: [], note: "", requiresCosts: false, hasCosts: false };

  it.each([
    ["none", {}, []],
    ["photo", {}, ["before_photo", "after_photo"]],
    ["photo", { before: 1 }, ["after_photo"]],
    ["photo", { before: 1, after: 2 }, []],
    ["checklist", { checklist: [{ done: true }, { done: false }] }, ["checklist_incomplete"]],
    ["checklist", { checklist: [{ done: true }] }, []],
    ["checklist", {}, []],
    ["note", { note: "  " }, ["completion_note"]],
    ["note", { note: "replaced washer" }, []],
    ["none", { requiresCosts: true }, ["actual_costs"]],
    ["none", { requiresCosts: true, hasCosts: true }, []],
    ["photo", { requiresCosts: true }, ["before_photo", "after_photo", "actual_costs"]],
  ])("%s %j -> %j", (rule, over, expected) => expect(completionBlockers({ ...base, rule, ...over })).toEqual(expected));

  it("counts checklist progress", () => {
    expect(checklistProgress([{ done: true }, { done: false }, { done: true }])).toEqual({ done: 2, total: 3 });
    expect(checklistProgress([])).toEqual({ done: 0, total: 0 });
  });
});
