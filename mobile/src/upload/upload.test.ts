import { describe, expect, it } from "vitest";
import { MemoryKeyValue, queueStorage, sessionStore } from "../offline/store";
import { LADDER, MAX_PHOTO_BYTES, compressToTarget } from "./compress";
import { UploadQueue, type QueueItem } from "./queue";
import {
  FatalUploadError, OffsetMismatch, RetryableError, UploadInterrupted, uploadFile,
  type FileSource, type SessionStore, type UploadTransport,
} from "./resumable";

const noSleep = async () => undefined;

function file(size: number, uri = "file:///photo.jpg"): FileSource {
  const bytes = Uint8Array.from({ length: size }, (_, i) => i % 251);
  return { uri, size, mimeType: "image/jpeg", read: async (offset, length) => bytes.slice(offset, offset + length) };
}

/** Behaves like the real /uploads endpoints: keeps what arrived, ignores a chunk at an old offset, refuses a gap. */
class FakeServer implements UploadTransport {
  uploads = new Map<string, { total: number; data: number[] }>();
  finished: string[] = [];
  appendCalls = 0;
  /** Called before each append; throw to simulate a failure. `lands` simulates a chunk that arrived although the reply was lost. */
  beforeAppend: (n: number) => void | { lands: true } = () => undefined;
  finishError?: Error;
  startCalls = 0;

  async start(_mime: string, total: number) {
    this.startCalls++;
    const id = `up-${this.uploads.size + 1}`;
    this.uploads.set(id, { total, data: [] });
    return { id, receivedBytes: 0 };
  }
  async status(id: string) {
    const u = this.uploads.get(id);
    return u ? { receivedBytes: u.data.length, totalBytes: u.total } : undefined;
  }
  async append(id: string, offset: number, chunk: Uint8Array) {
    const u = this.uploads.get(id)!;
    const n = ++this.appendCalls;
    const effect = this.beforeAppend(n);
    if (offset < u.data.length) return { receivedBytes: u.data.length }; // duplicate retry
    if (offset > u.data.length) throw new OffsetMismatch(u.data.length);
    u.data.push(...chunk);
    if (effect && "lands" in effect) throw new RetryableError("connection dropped after the chunk arrived");
    return { receivedBytes: u.data.length };
  }
  async finish(id: string) {
    if (this.finishError) throw this.finishError;
    this.finished.push(id);
  }
}

const memorySessions = (): SessionStore & { map: Map<string, string> } => {
  const map = new Map<string, string>();
  return { map, get: async (k) => map.get(k), set: async (k, v) => void map.set(k, v), delete: async (k) => void map.delete(k) };
};

describe("resumable upload (RA-02, TA-04)", () => {
  it("sends the file in chunks, reports progress and attaches it", async () => {
    const server = new FakeServer();
    const store = memorySessions();
    const progress: number[] = [];
    await uploadFile(file(1000), server, store, "k", { chunkSize: 400, sleep: noSleep, onProgress: (s) => progress.push(s) });

    expect(server.appendCalls).toBe(3);
    expect(progress).toEqual([0, 400, 800, 1000]);
    expect(server.uploads.get("up-1")!.data).toHaveLength(1000);
    expect(server.finished).toEqual(["up-1"]);
    expect(store.map.size).toBe(0); // nothing left to resume
  });

  it("survives dropped connections and never duplicates or loses bytes", async () => {
    const server = new FakeServer();
    server.beforeAppend = (n) => {
      if (n === 2) throw new RetryableError("no signal"); // chunk never arrived
      if (n === 4) return { lands: true }; // chunk arrived, reply lost: the retry must not resend it
    };
    await uploadFile(file(2000), server, memorySessions(), "k", { chunkSize: 500, sleep: noSleep });

    const received = server.uploads.get("up-1")!.data;
    expect(received).toHaveLength(2000);
    expect(received).toEqual(Array.from({ length: 2000 }, (_, i) => i % 251));
    expect(server.finished).toHaveLength(1);
  });

  it("backs off between retries", async () => {
    const server = new FakeServer();
    server.beforeAppend = (n) => { if (n <= 3) throw new RetryableError("no signal"); };
    const waits: number[] = [];
    await uploadFile(file(100), server, memorySessions(), "k", { chunkSize: 100, sleep: async (ms) => void waits.push(ms), backoffMs: (n) => n * 100 });
    expect(waits).toEqual([100, 200, 300]);
  });

  it("gives up after repeated failures but keeps the upload open, and a later run resumes instead of starting over", async () => {
    const server = new FakeServer();
    const store = memorySessions();
    server.beforeAppend = (n) => { if (n > 2) throw new RetryableError("tunnel"); };

    await expect(uploadFile(file(1000), server, store, "photo", { chunkSize: 250, maxAttempts: 3, sleep: noSleep })).rejects.toBeInstanceOf(UploadInterrupted);
    expect(server.uploads.get("up-1")!.data).toHaveLength(500); // two chunks made it
    expect(store.map.get("photo")).toBe("up-1"); // remembered across the app restart

    server.beforeAppend = () => undefined; // signal is back
    const sentAgain: number[] = [];
    const before = server.appendCalls;
    await uploadFile(file(1000), server, store, "photo", { chunkSize: 250, sleep: noSleep, onProgress: (s) => sentAgain.push(s) });
    expect(server.startCalls).toBe(1); // same upload, not a new one
    expect(sentAgain[0]).toBe(500); // resumed at the halfway point
    expect(server.appendCalls - before).toBe(2); // only the missing half was sent
    expect(server.finished).toEqual(["up-1"]);
  });

  it("starts afresh when the server has forgotten the upload or the file changed", async () => {
    const server = new FakeServer();
    const store = memorySessions();
    store.map.set("k", "gone");
    await uploadFile(file(300), server, store, "k", { chunkSize: 300, sleep: noSleep });
    expect(server.startCalls).toBe(1);

    store.map.set("k2", "up-1"); // up-1 was for a 300 byte file
    await uploadFile(file(500), server, store, "k2", { chunkSize: 500, sleep: noSleep });
    expect(server.startCalls).toBe(2);
  });

  it("resyncs when the server holds more or fewer bytes than expected", async () => {
    const server = new FakeServer();
    let first = true;
    server.beforeAppend = () => {
      if (first) { first = false; server.uploads.get("up-1")!.data.push(...Array.from({ length: 300 }, (_, i) => i % 251)); } // another request landed 300 bytes
    };
    await uploadFile(file(900), server, memorySessions(), "k", { chunkSize: 300, sleep: noSleep });
    expect(server.uploads.get("up-1")!.data).toHaveLength(900);
  });

  it("does not retry a refusal that retrying cannot fix", async () => {
    const server = new FakeServer();
    server.beforeAppend = () => { throw new FatalUploadError("file_too_large"); };
    await expect(uploadFile(file(100), server, memorySessions(), "k", { sleep: noSleep })).rejects.toMatchObject({ code: "file_too_large" });
    expect(server.appendCalls).toBe(1);
  });

  it("retries the final attach if it times out", async () => {
    const server = new FakeServer();
    let tries = 0;
    const finish = server.finish.bind(server);
    server.finish = async (id) => { if (++tries < 3) throw new RetryableError("timeout"); return finish(id); };
    await uploadFile(file(10), server, memorySessions(), "k", { sleep: noSleep });
    expect(tries).toBe(3);
    expect(server.finished).toEqual(["up-1"]);
  });
});

describe("compression to 500 KB (RA-02)", () => {
  it("uses the best quality that fits", async () => {
    const sizes = [900_000, 700_000, 400_000, 200_000];
    const tried: number[] = [];
    const out = await compressToTarget(async (step) => { tried.push(step.maxSide); return { uri: `u${tried.length}`, size: sizes[tried.length - 1] }; });
    expect(out).toMatchObject({ uri: "u3", size: 400_000, fits: true, steps: 3 });
    expect(tried).toEqual([1600, 1280, 1024]);
    expect(out.size).toBeLessThanOrEqual(MAX_PHOTO_BYTES);
  });

  it("tells the caller when nothing fits instead of sending a huge file silently", async () => {
    const out = await compressToTarget(async () => ({ uri: "x", size: 5_000_000 }));
    expect(out.fits).toBe(false);
    expect(out.steps).toBe(LADDER.length);
  });

  it("keeps an already small photo as it is", async () => {
    const out = await compressToTarget(async () => ({ uri: "small", size: 100_000 }));
    expect(out.steps).toBe(1);
  });
});

describe("upload queue", () => {
  function setup(server: FakeServer, sizes: Record<string, number> = {}) {
    const kv = new MemoryKeyValue();
    const sessions = sessionStore(kv);
    let n = 0;
    const queue = new UploadQueue({
      storage: queueStorage(kv), sessions, newId: () => `q${++n}`, now: () => new Date("2026-10-22T08:00:00Z"),
      options: { chunkSize: 100, sleep: noSleep, maxAttempts: 2 },
      open: async (i) => file(sizes[i.uri] ?? i.size, i.uri),
      transportFor: () => server,
    });
    return { queue, kv };
  }
  const photo = (uri: string, size = 250): Pick<QueueItem, "target" | "uri" | "mimeType" | "size"> =>
    ({ target: { kind: "job", id: "j1", stage: "before" }, uri, mimeType: "image/jpeg", size });

  it("uploads waiting photos oldest first and empties itself", async () => {
    const server = new FakeServer();
    const { queue } = setup(server);
    await queue.add(photo("file:///a.jpg"));
    await queue.add(photo("file:///b.jpg"));
    const events: string[] = [];
    await queue.drain((e) => { if (e.type === "done") events.push(e.item.uri); });
    expect(events).toEqual(["file:///a.jpg", "file:///b.jpg"]);
    expect(await queue.list()).toEqual([]);
    expect(server.finished).toHaveLength(2);
  });

  it("survives a restart: a new queue over the same storage finds the waiting items", async () => {
    const server = new FakeServer();
    const first = setup(server);
    await first.queue.add(photo("file:///a.jpg"));
    const second = new UploadQueue({
      storage: queueStorage(first.kv), sessions: sessionStore(first.kv), options: { sleep: noSleep },
      open: async (i) => file(i.size, i.uri), transportFor: () => server,
    });
    expect(await second.pending()).toBe(1);
    await second.drain();
    expect(await second.pending()).toBe(0);
  });

  it("pauses on lost signal, keeps the rest queued, and carries on when drained again", async () => {
    const server = new FakeServer();
    server.beforeAppend = () => { throw new RetryableError("no signal"); };
    const { queue } = setup(server);
    await queue.add(photo("file:///a.jpg"));
    await queue.add(photo("file:///b.jpg"));
    const events: string[] = [];
    await queue.drain((e) => events.push(e.type));
    expect(events).toContain("paused");
    expect(await queue.pending()).toBe(2); // b was not even tried

    server.beforeAppend = () => undefined;
    await queue.drain();
    expect(await queue.pending()).toBe(0);
  });

  it("marks a refused photo failed with the reason and moves on to the next", async () => {
    const server = new FakeServer();
    let n = 0;
    server.beforeAppend = () => { if (++n === 1) throw new FatalUploadError("unsupported_file_type"); };
    const { queue } = setup(server);
    await queue.add(photo("file:///bad.jpg", 100));
    await queue.add(photo("file:///good.jpg", 100));
    await queue.drain();

    const left = await queue.list();
    expect(left).toHaveLength(1);
    expect(left[0]).toMatchObject({ uri: "file:///bad.jpg", status: "failed", error: "unsupported_file_type" });
    expect(server.finished).toHaveLength(1);

    await queue.retryFailed();
    expect((await queue.list())[0].status).toBe("waiting");
  });

  it("never runs two drains at once", async () => {
    const server = new FakeServer();
    const { queue } = setup(server);
    await queue.add(photo("file:///a.jpg"));
    await Promise.all([queue.drain(), queue.drain()]);
    expect(server.finished).toHaveLength(1);
  });
});
