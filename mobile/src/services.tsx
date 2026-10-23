import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { uploadTransportFor } from "./api";
import { DraftStore, ReceiptStore, queueStorage, sessionStore } from "./offline/store";
import { onReconnect, storage } from "./platform/device";
import { openFile } from "./platform/files";
import { UploadQueue } from "./upload/queue";

const receipts = new ReceiptStore(storage);
const drafts = new DraftStore(storage);
const queue = new UploadQueue({
  storage: queueStorage(storage),
  sessions: sessionStore(storage),
  open: async (item) => openFile(item.uri, item.mimeType),
  transportFor: uploadTransportFor,
});

interface Services {
  receipts: ReceiptStore;
  drafts: DraftStore;
  queue: UploadQueue;
  /** Photos still waiting, and photos the server refused for good. */
  waiting: number;
  failed: number;
  uploading: boolean;
  /** Starts (or resumes) uploading whatever is queued. Safe to call any time. */
  drain: () => Promise<void>;
}

const Ctx = createContext<Services | null>(null);

/** Owns the receipts, the unsent draft and the upload queue, and resumes uploads when the signal comes back. */
export function ServicesProvider({ children }: { children: ReactNode }) {
  const [counts, setCounts] = useState({ waiting: 0, failed: 0 });
  const [uploading, setUploading] = useState(false);
  const busy = useRef(false);

  const recount = useCallback(async () => {
    const items = await queue.list();
    setCounts({ waiting: items.filter((i) => i.status === "waiting").length, failed: items.filter((i) => i.status === "failed").length });
  }, []);

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
    void drain();
    return onReconnect(() => void drain());
  }, [recount, drain]);

  const value = useMemo<Services>(() => ({ receipts, drafts, queue, ...counts, uploading, drain }), [counts, uploading, drain]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useServices(): Services {
  const v = useContext(Ctx);
  if (!v) throw new Error("useServices must be used inside <ServicesProvider>");
  return v;
}
