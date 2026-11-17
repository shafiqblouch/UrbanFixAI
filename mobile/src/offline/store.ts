// What stays on the phone: receipts (RA-03), the unsent complaint draft, and the upload queue.

/** A tiny key-value store. AsyncStorage on the phone, a Map in tests. */
export interface KeyValue {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
}

export class MemoryKeyValue implements KeyValue {
  data = new Map<string, string>();
  async getItem(key: string) { return this.data.get(key) ?? null; }
  async setItem(key: string, value: string) { this.data.set(key, value); }
  async removeItem(key: string) { this.data.delete(key); }
}

async function readJson<T>(kv: KeyValue, key: string, fallback: T): Promise<T> {
  try {
    const raw = await kv.getItem(key);
    return raw ? (JSON.parse(raw) as T) : fallback;
  } catch {
    return fallback; // a corrupt value must not stop the app opening
  }
}

export interface Receipt {
  id: string;
  reference: string;
  description: string;
  createdAt: string;
  /** The complaint was held because no location was given. */
  needsLocation: boolean;
  photos: number;
}

const RECEIPTS = "uf.receipts";
const MAX_RECEIPTS = 50;

/** Every key that holds data belonging to the signed-in person, so it can be wiped when they sign out. */
const USER_DATA_KEYS = [RECEIPTS, "uf.draft", "uf.jobDrafts", "uf.uploadQueue", "uf.uploadSessions", "uf.gateQueue"];

/** Remembers which person the on-device user data belongs to, so a different person's sign-in clears it. */
const DATA_OWNER = "uf.dataOwner";

/**
 * Removes all on-device data tied to the current person (receipts, unsent draft, upload queue and sessions),
 * and forgets who it belonged to. Called on sign-out so the next person to sign in on this device never
 * sees — or taps into — someone else's receipts, whose complaints are not theirs on the server.
 */
export async function clearUserData(kv: KeyValue): Promise<void> {
  await Promise.all([...USER_DATA_KEYS, DATA_OWNER].map((key) => kv.removeItem(key).catch(() => undefined)));
}

/**
 * Called once a person is known to be signed in. Unless the on-device data is already recorded as theirs,
 * it is wiped and ownership is recorded for this person — so a different person (or data left by an install
 * from before ownership was tracked) can never be read or tapped into. A person signing back into their own
 * account keeps their receipts, unsent draft and pending uploads.
 */
export async function reconcileUserData(kv: KeyValue, userId: string): Promise<void> {
  let owner: string | null = null;
  try { owner = await kv.getItem(DATA_OWNER); } catch { /* treat as unknown owner */ }
  if (owner === userId) return;
  // Record this person as owner only once every key is actually gone. If a removal fails, ownership is left
  // unchanged so the next sign-in wipes again — never claim the data is this person's while some of the
  // previous person's data might still be on the device (that would make it readable and never re-wiped).
  const wiped = await Promise.allSettled(USER_DATA_KEYS.map((key) => kv.removeItem(key)));
  if (wiped.every((r) => r.status === "fulfilled")) await kv.setItem(DATA_OWNER, userId).catch(() => undefined);
}

/** Receipts are shown the moment the server confirms and kept so they can be read with no signal. */
export class ReceiptStore {
  constructor(private kv: KeyValue) {}

  async list(): Promise<Receipt[]> {
    return readJson<Receipt[]>(this.kv, RECEIPTS, []);
  }

  async add(receipt: Receipt): Promise<void> {
    const rest = (await this.list()).filter((r) => r.id !== receipt.id);
    await this.kv.setItem(RECEIPTS, JSON.stringify([receipt, ...rest].slice(0, MAX_RECEIPTS)));
  }

  async get(id: string): Promise<Receipt | undefined> {
    return (await this.list()).find((r) => r.id === id);
  }
}

/** A photo kept in a draft: the local file plus what is needed to re-queue it for upload. */
export interface DraftPhoto {
  uri: string;
  size: number;
}

/** A voice note kept in a draft: the local file, its size and its length in seconds. */
export interface DraftVoice {
  uri: string;
  size: number;
  seconds: number;
}

export interface Draft {
  description: string;
  locationText: string;
  landmarkId?: string;
  latitude?: number;
  longitude?: number;
  photos: DraftPhoto[];
  voiceNotes?: DraftVoice[];
  savedAt: string;
}

const DRAFT = "uf.draft";

/** One unsent complaint. Saved when sending fails (no signal) so the words are never lost. */
export class DraftStore {
  constructor(private kv: KeyValue) {}

  get(): Promise<Draft | undefined> {
    return readJson<Draft | undefined>(this.kv, DRAFT, undefined);
  }

  async save(draft: Draft): Promise<void> {
    await this.kv.setItem(DRAFT, JSON.stringify(draft));
  }

  async clear(): Promise<void> {
    await this.kv.removeItem(DRAFT);
  }
}

const JOB_DRAFTS = "uf.jobDrafts";

/**
 * The technician's unsaved completion note, kept per job so it survives leaving the job screen or the
 * app being closed. Photos already persist in the upload queue; this keeps the words with them.
 */
export class JobDraftStore {
  constructor(private kv: KeyValue) {}

  private all(): Promise<Record<string, string>> {
    return readJson<Record<string, string>>(this.kv, JOB_DRAFTS, {});
  }

  async get(jobId: string): Promise<string | undefined> {
    return (await this.all())[jobId];
  }

  async save(jobId: string, note: string): Promise<void> {
    await this.kv.setItem(JOB_DRAFTS, JSON.stringify({ ...(await this.all()), [jobId]: note }));
  }

  async clear(jobId: string): Promise<void> {
    const { [jobId]: _gone, ...rest } = await this.all();
    await this.kv.setItem(JOB_DRAFTS, JSON.stringify(rest));
  }
}

/**
 * What the guard typed when recording a crossing (GT-01), matching the server's RecordEvent body. A correction
 * is a new event, so nothing here is ever edited after it is queued.
 */
export interface GateEventInput {
  direction: "in" | "out";
  subjectType: "resident" | "vehicle" | "help" | "visitor" | "delivery";
  gateId?: string | null;
  houseId?: string | null;
  vehicleId?: string | null;
  helpId?: string | null;
  passId?: string | null;
  residentUserId?: string | null;
  subjectName?: string | null;
  phone?: string | null;
  vehiclePlate?: string | null;
  decision?: "allowed" | "denied" | null;
  note?: string | null;
}

/** A crossing recorded while offline (or that failed to send), waiting to be posted. */
export interface PendingGateEvent {
  /** Local id, so it can be shown and removed before the server assigns a real one. */
  id: string;
  input: GateEventInput;
  /** A short human label ("Courier — in") for the pending list. */
  label: string;
  capturedAt: string;
}

const GATE_QUEUE = "uf.gateQueue";
const MAX_GATE_QUEUE = 500;

/**
 * The guard's crossings that have not reached the server yet. The gate is where the signal is worst, so an
 * entry or exit is always written here first and drained when the network returns — nothing is lost at the barrier.
 */
export class GateQueue {
  constructor(private kv: KeyValue) {}

  list(): Promise<PendingGateEvent[]> {
    return readJson<PendingGateEvent[]>(this.kv, GATE_QUEUE, []);
  }

  async add(event: PendingGateEvent): Promise<void> {
    const rest = (await this.list()).filter((e) => e.id !== event.id);
    await this.kv.setItem(GATE_QUEUE, JSON.stringify([...rest, event].slice(-MAX_GATE_QUEUE)));
  }

  async remove(id: string): Promise<void> {
    await this.kv.setItem(GATE_QUEUE, JSON.stringify((await this.list()).filter((e) => e.id !== id)));
  }
}

export const queueStorage = (kv: KeyValue) => ({
  load: () => readJson<import("../upload/queue").QueueItem[]>(kv, "uf.uploadQueue", []),
  save: (items: import("../upload/queue").QueueItem[]) => kv.setItem("uf.uploadQueue", JSON.stringify(items)),
});

export const sessionStore = (kv: KeyValue) => ({
  get: async (key: string) => (await readJson<Record<string, string>>(kv, "uf.uploadSessions", {}))[key],
  set: async (key: string, value: string) => {
    const all = await readJson<Record<string, string>>(kv, "uf.uploadSessions", {});
    await kv.setItem("uf.uploadSessions", JSON.stringify({ ...all, [key]: value }));
  },
  delete: async (key: string) => {
    const { [key]: _gone, ...rest } = await readJson<Record<string, string>>(kv, "uf.uploadSessions", {});
    await kv.setItem("uf.uploadSessions", JSON.stringify(rest));
  },
});
