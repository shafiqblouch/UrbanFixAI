/**
 * What a job still needs before the technician can send it for approval (TA-07). The same rules as the server
 * (JobEvidence.Missing): the app uses them to explain and to stop a pointless tap, the server remains the judge.
 */
export type EvidenceRule = "none" | "photo" | "checklist" | "note";

export interface CompletionInput {
  rule: string;
  /** Photos already on the server plus photos still waiting to upload. */
  before: number;
  after: number;
  checklist: { done: boolean }[];
  note: string;
  requiresCosts: boolean;
  hasCosts: boolean;
}

export type Missing = "before_photo" | "after_photo" | "checklist_incomplete" | "completion_note" | "actual_costs";

export function completionBlockers(i: CompletionInput): Missing[] {
  const missing: Missing[] = [];
  if (i.rule === "photo") {
    if (i.before === 0) missing.push("before_photo");
    if (i.after === 0) missing.push("after_photo");
  } else if (i.rule === "checklist") {
    if (i.checklist.some((c) => !c.done)) missing.push("checklist_incomplete");
  } else if (i.rule === "note") {
    if (!i.note.trim()) missing.push("completion_note");
  }
  if (i.requiresCosts && !i.hasCosts) missing.push("actual_costs");
  return missing;
}

export const checklistProgress = (checklist: { done: boolean }[]) => ({ done: checklist.filter((c) => c.done).length, total: checklist.length });
