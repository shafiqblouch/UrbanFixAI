// Danger words (RA-08). The list is the same file the server reads (contracts/safety-terms.json), so what the app warns about
// and what the server flags can never drift apart.
import shared from "../../contracts/safety-terms.json";

const TERMS: string[] = Object.values(shared.terms).flat().map((t) => t.toLowerCase());

/** The danger words found in a text; empty when there are none. */
export function safetyTermsIn(text: string): string[] {
  const t = text.toLowerCase();
  return TERMS.filter((term) => t.includes(term));
}

export const isSafetyText = (text: string): boolean => safetyTermsIn(text).length > 0;

/** Pakistan's rescue service. The prompt offers to dial it. */
export const EMERGENCY_NUMBER = "1122";
