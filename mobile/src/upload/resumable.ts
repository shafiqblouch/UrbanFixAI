// Resumable chunked upload (RA-02, TA-04). The server keeps the bytes it has received (`/uploads`), so after a dropped
// connection, a killed app or a retried request the client asks how much arrived and carries on from there. A chunk sent
// twice is harmless: the server ignores one at an old offset.

/** The bytes of one file, read piece by piece so a 5 MB photo is never held in memory at once. */
export interface FileSource {
  uri: string;
  size: number;
  mimeType: string;
  read(offset: number, length: number): Promise<Uint8Array>;
}

/** The server side of an upload, as the API exposes it. */
export interface UploadTransport {
  start(mimeType: string, totalBytes: number): Promise<{ id: string; receivedBytes: number }>;
  /** `undefined` when the server no longer knows the upload (expired, or already attached). */
  status(id: string): Promise<{ receivedBytes: number; totalBytes: number } | undefined>;
  append(id: string, offset: number, chunk: Uint8Array): Promise<{ receivedBytes: number }>;
  /** Attaches the finished upload to its complaint or job. */
  finish(id: string): Promise<void>;
}

/** Remembers the open upload for a file across app restarts. */
export interface SessionStore {
  get(key: string): Promise<string | undefined>;
  set(key: string, value: string): Promise<void>;
  delete(key: string): Promise<void>;
}

/** A failure worth retrying: no signal, a timeout, a 5xx. */
export class RetryableError extends Error {}

/** The server says it already holds a different number of bytes; carry on from there. */
export class OffsetMismatch extends Error {
  constructor(public receivedBytes: number) {
    super("offset_mismatch");
  }
}

/** A failure that retrying cannot fix: file too large, unsupported type. */
export class FatalUploadError extends Error {
  constructor(public code: string) {
    super(code);
  }
}

/** Gave up for now after repeated failures. The open upload is kept, so the next attempt resumes where this one stopped. */
export class UploadInterrupted extends Error {
  constructor(public sentBytes: number, public totalBytes: number) {
    super("upload_interrupted");
  }
}

export interface UploadOptions {
  chunkSize?: number;
  /** Consecutive failures tolerated before giving up. */
  maxAttempts?: number;
  backoffMs?: (attempt: number) => number;
  sleep?: (ms: number) => Promise<void>;
  onProgress?: (sentBytes: number, totalBytes: number) => void;
}

export const DEFAULT_CHUNK = 256 * 1024;
const defaultBackoff = (attempt: number) => Math.min(1000 * 2 ** (attempt - 1), 15_000);
const realSleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * Uploads `file` and attaches it. `key` identifies the file across restarts (usually its URI).
 * Resolves when the server has the whole file and `finish` succeeded.
 */
export async function uploadFile(file: FileSource, transport: UploadTransport, store: SessionStore, key: string, options: UploadOptions = {}): Promise<void> {
  const chunkSize = options.chunkSize ?? DEFAULT_CHUNK;
  const maxAttempts = options.maxAttempts ?? 5;
  const backoff = options.backoffMs ?? defaultBackoff;
  const sleep = options.sleep ?? realSleep;

  let id = await store.get(key);
  let received = 0;
  if (id) {
    const known = await transport.status(id).catch(() => undefined);
    if (known && known.totalBytes === file.size) received = known.receivedBytes;
    else id = undefined; // expired, or a different file now sits at that path
  }
  if (!id) {
    const started = await retry(() => transport.start(file.mimeType, file.size), maxAttempts, backoff, sleep, file.size, 0);
    id = started.id;
    received = started.receivedBytes;
    await store.set(key, id);
  }
  options.onProgress?.(received, file.size);

  const uploadId = id;
  let failures = 0;
  while (received < file.size) {
    const length = Math.min(chunkSize, file.size - received);
    try {
      const chunk = await file.read(received, length);
      const reply = await transport.append(uploadId, received, chunk);
      received = reply.receivedBytes;
      failures = 0;
      options.onProgress?.(received, file.size);
    } catch (e) {
      if (e instanceof OffsetMismatch) { received = e.receivedBytes; continue; }
      if (!(e instanceof RetryableError)) throw e;
      if (++failures >= maxAttempts) throw new UploadInterrupted(received, file.size);
      await sleep(backoff(failures));
      // The chunk may have landed before the connection dropped; ask rather than guess.
      const known = await transport.status(uploadId).catch(() => undefined);
      if (known) received = known.receivedBytes;
    }
  }

  await retry(() => transport.finish(uploadId), maxAttempts, backoff, sleep, received, file.size);
  await store.delete(key);
}

async function retry<T>(fn: () => Promise<T>, maxAttempts: number, backoff: (n: number) => number, sleep: (ms: number) => Promise<void>, sent: number, total: number): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    try {
      return await fn();
    } catch (e) {
      if (!(e instanceof RetryableError)) throw e;
      if (attempt >= maxAttempts) throw new UploadInterrupted(sent, total);
      await sleep(backoff(attempt));
    }
  }
}
