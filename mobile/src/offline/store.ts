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

export interface Draft {
  description: string;
  locationText: string;
  landmarkId?: string;
  latitude?: number;
  longitude?: number;
  photoUris: string[];
  voiceUri?: string;
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
