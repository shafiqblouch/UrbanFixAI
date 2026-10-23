/**
 * Phone numbers as the server stores them: international form, e.g. +923001234567 (RA-04). This mirrors the server's rules
 * so an obviously wrong number is caught before a code is requested; the server stays the authority.
 * A leading 0 is read as Pakistan (+92).
 */
export function normalizePhone(input: string): string | undefined {
  const trimmed = input.trim();
  if (!trimmed) return undefined;
  const digits = trimmed.replace(/\D/g, "");
  let candidate: string;
  if (trimmed.startsWith("+")) candidate = `+${digits}`;
  else if (digits.startsWith("00")) candidate = `+${digits.slice(2)}`;
  else if (digits.startsWith("0")) candidate = `+92${digits.slice(1)}`;
  else if (digits.startsWith("92") && digits.length >= 12) candidate = `+${digits}`;
  else return undefined;
  return /^\+[1-9][0-9]{7,14}$/.test(candidate) ? candidate : undefined;
}

/** Seconds until another code may be asked for, given when the last was sent. The server enforces 60 seconds. */
export function resendWait(sentAt: number | undefined, now: number, cooldownSeconds = 60): number {
  if (sentAt === undefined) return 0;
  return Math.max(0, Math.ceil(cooldownSeconds - (now - sentAt) / 1000));
}
