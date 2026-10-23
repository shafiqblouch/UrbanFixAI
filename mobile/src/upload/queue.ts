// A small persistent queue of photos waiting to upload. A complaint or job update is sent first (it is small); its photos
// follow in the background and survive the app being closed or the signal being lost. FD-17 replaces this with the shared
// sync engine in Phase 2a.

import { FatalUploadError, UploadInterrupted, type FileSource, type SessionStore, type UploadOptions, type UploadTransport, uploadFile } from "./resumable";

export interface QueueItem {
  id: string;
  /** Where the photo belongs: a complaint, or a job (with the stage: before / after / progress). */
  target: { kind: "complaint"; id: string } | { kind: "job"; id: string; stage: "before" | "after" | "progress" };
  uri: string;
  mimeType: string;
  size: number;
  status: "waiting" | "failed";
  attempts: number;
  /** Machine-readable reason when `failed`. */
  error?: string;
  createdAt: string;
}

export interface QueueStorage {
  load(): Promise<QueueItem[]>;
  save(items: QueueItem[]): Promise<void>;
}

export interface QueueDeps {
  storage: QueueStorage;
  sessions: SessionStore;
  /** Builds the transport that attaches an upload to the item's target. */
  transportFor(item: QueueItem): UploadTransport;
  open(item: QueueItem): Promise<FileSource>;
  options?: UploadOptions;
  newId?: () => string;
  now?: () => Date;
}

export type QueueEvent = { type: "uploading"; item: QueueItem; sent: number; total: number } | { type: "done"; item: QueueItem } | { type: "failed"; item: QueueItem } | { type: "paused"; item: QueueItem };

export class UploadQueue {
  private running = false;
  constructor(private deps: QueueDeps) {}

  async list(): Promise<QueueItem[]> {
    return this.deps.storage.load();
  }

  async pending(): Promise<number> {
    return (await this.list()).filter((i) => i.status === "waiting").length;
  }

  async add(input: Pick<QueueItem, "target" | "uri" | "mimeType" | "size">): Promise<QueueItem> {
    const item: QueueItem = {
      ...input, id: (this.deps.newId ?? defaultId)(), status: "waiting", attempts: 0, createdAt: (this.deps.now ?? (() => new Date()))().toISOString(),
    };
    await this.deps.storage.save([...(await this.list()), item]);
    return item;
  }

  /** Puts failed items back in line, e.g. after the user fixed the problem. */
  async retryFailed(): Promise<void> {
    const items = await this.list();
    await this.deps.storage.save(items.map((i) => (i.status === "failed" ? { ...i, status: "waiting", error: undefined } : i)));
  }

  async remove(id: string): Promise<void> {
    await this.deps.storage.save((await this.list()).filter((i) => i.id !== id));
  }

  /**
   * Uploads waiting items one at a time, oldest first. Stops at the first interruption (no signal) and leaves the rest queued;
   * an item the server refuses for good is marked failed and the queue moves on. Safe to call again at any time.
   */
  async drain(on: (e: QueueEvent) => void = () => undefined): Promise<void> {
    if (this.running) return;
    this.running = true;
    try {
      for (;;) {
        const next = (await this.list()).find((i) => i.status === "waiting");
        if (!next) return;
        try {
          const file = await this.deps.open(next);
          await uploadFile(file, this.deps.transportFor(next), this.deps.sessions, next.uri, {
            ...this.deps.options, onProgress: (sent, total) => on({ type: "uploading", item: next, sent, total }),
          });
          await this.remove(next.id);
          on({ type: "done", item: next });
        } catch (e) {
          if (e instanceof UploadInterrupted) {
            await this.update(next.id, { attempts: next.attempts + 1 });
            on({ type: "paused", item: next });
            return; // no signal: the later items would fail the same way
          }
          const error = e instanceof FatalUploadError ? e.code : e instanceof Error ? e.message : "unknown";
          await this.update(next.id, { status: "failed", attempts: next.attempts + 1, error });
          on({ type: "failed", item: { ...next, status: "failed", error } });
        }
      }
    } finally {
      this.running = false;
    }
  }

  private async update(id: string, patch: Partial<QueueItem>) {
    await this.deps.storage.save((await this.list()).map((i) => (i.id === id ? { ...i, ...patch } : i)));
  }
}

const defaultId = () => `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
