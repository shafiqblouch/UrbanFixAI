import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { ApiError, api, ok, uploadTransportFor } from "./api";
import { DraftStore, GateQueue, JobDraftStore, ReceiptStore, queueStorage, sessionStore, type GateEventInput } from "./offline/store";
import { onReconnect, storage } from "./platform/device";
import { openFile } from "./platform/files";
import { UploadQueue } from "./upload/queue";

const receipts = new ReceiptStore(storage);
const drafts = new DraftStore(storage);
const jobDrafts = new JobDraftStore(storage);
const gateQueue = new GateQueue(storage);

/** A short unique id for a locally-queued item. */
const localId = () => `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;

/**
 * Whether a failed send is worth queueing for a retry (no signal, server trouble, a rate limit) rather than a
 * refusal the server will never accept (a 4xx). A network failure throws before reaching `ApiError`, so a plain
 * error means "no connection".
 */
const offlineish = (e: unknown): boolean =>
  !(e instanceof ApiError) || e.status === 0 || e.status >= 500 || e.status === 408 || e.status === 429;

/** The server's RecordEvent body wants every field present (null when absent); fill in what the guard left out. */
const gateBody = (i: GateEventInput) => ({
  direction: i.direction,
  subjectType: i.subjectType,
  gateId: i.gateId ?? null,
  houseId: i.houseId ?? null,
  vehicleId: i.vehicleId ?? null,
  helpId: i.helpId ?? null,
  passId: i.passId ?? null,
  residentUserId: i.residentUserId ?? null,
  subjectName: i.subjectName ?? null,
  phone: i.phone ?? null,
  vehiclePlate: i.vehiclePlate ?? null,
  decision: i.decision ?? null,
  note: i.note ?? null,
});
const queue = new UploadQueue({
  storage: queueStorage(storage),
  sessions: sessionStore(storage),
  open: async (item) => openFile(item.uri, item.mimeType),
  transportFor: uploadTransportFor,
});

interface Services {
  receipts: ReceiptStore;
  drafts: DraftStore;
  jobDrafts: JobDraftStore;
  queue: UploadQueue;
  /** Photos still waiting, and photos the server refused for good. */
  waiting: number;
  failed: number;
  uploading: boolean;
  /** Starts (or resumes) uploading whatever is queued. Safe to call any time. */
  drain: () => Promise<void>;
  gateQueue: GateQueue;
  /** Gate crossings recorded on this device that have not reached the server yet (GT-01). */
  gatePending: number;
  /**
   * Records a crossing: sent straight away when online, kept on the device and drained later when not.
   * Returns how it was handled so the guard sees "logged" or "saved, will sync".
   */
  recordGate: (input: GateEventInput, label: string) => Promise<"sent" | "queued">;
  /** Sends any queued crossings, oldest first; stops at the first that still cannot reach the server. */
  drainGate: () => Promise<void>;
}

const Ctx = createContext<Services | null>(null);

/** Owns the receipts, the unsent draft and the upload queue, and resumes uploads when the signal comes back. */
export function ServicesProvider({ children }: { children: ReactNode }) {
  const [counts, setCounts] = useState({ waiting: 0, failed: 0 });
  const [uploading, setUploading] = useState(false);
  const [gatePending, setGatePending] = useState(0);
  const busy = useRef(false);
  const gateBusy = useRef(false);

  const recount = useCallback(async () => {
    const items = await queue.list();
    setCounts({ waiting: items.filter((i) => i.status === "waiting").length, failed: items.filter((i) => i.status === "failed").length });
  }, []);

  const recountGate = useCallback(async () => {
    setGatePending((await gateQueue.list()).length);
  }, []);

  const drainGate = useCallback(async () => {
    if (gateBusy.current) return;
    gateBusy.current = true;
    try {
      for (const e of await gateQueue.list()) {
        try {
          await ok(api.POST("/gate/events", { body: gateBody(e.input) }));
          await gateQueue.remove(e.id);
        } catch (err) {
          if (offlineish(err)) break; // still no signal — leave the rest for next time
          await gateQueue.remove(e.id); // a refusal that will never succeed (e.g. a stale reference): drop it
        }
        await recountGate();
      }
    } finally {
      gateBusy.current = false;
      await recountGate();
    }
  }, [recountGate]);

  const recordGate = useCallback(async (input: GateEventInput, label: string): Promise<"sent" | "queued"> => {
    try {
      await ok(api.POST("/gate/events", { body: gateBody(input) }));
      return "sent";
    } catch (e) {
      if (!offlineish(e)) throw e;
      await gateQueue.add({ id: localId(), input, label, capturedAt: new Date().toISOString() });
      await recountGate();
      return "queued";
    }
  }, [recountGate]);

  const drain = useCallback(async () => {
    if (busy.current) return;
    busy.current = true;
    setUploading(true);
    try {
      await queue.drain(() => void recount());
    } finally {
      busy.current = false;
      setUploading(false);
      await recount();
    }
  }, [recount]);

  useEffect(() => {
    void recount();
    void recountGate();
    void drain();
    void drainGate();
    return onReconnect(() => { void drain(); void drainGate(); });
  }, [recount, recountGate, drain, drainGate]);

  const value = useMemo<Services>(
    () => ({ receipts, drafts, jobDrafts, queue, ...counts, uploading, drain, gateQueue, gatePending, recordGate, drainGate }),
    [counts, uploading, drain, gatePending, recordGate, drainGate],
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useServices(): Services {
  const v = useContext(Ctx);
  if (!v) throw new Error("useServices must be used inside <ServicesProvider>");
  return v;
}
