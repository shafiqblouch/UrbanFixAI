import { describe, expect, it } from "vitest";
import { DraftStore, GateQueue, JobDraftStore, MemoryKeyValue, ReceiptStore, type PendingGateEvent, type Receipt } from "./store";

const receipt = (n: number): Receipt => ({ id: `c${n}`, reference: `UF-2026-${String(n).padStart(5, "0")}`, description: "Leak", createdAt: "2026-10-22T08:00:00Z", needsLocation: false, photos: 1 });

describe("receipts kept offline (RA-03)", () => {
  it("lists the newest first and survives a restart", async () => {
    const kv = new MemoryKeyValue();
    await new ReceiptStore(kv).add(receipt(1));
    await new ReceiptStore(kv).add(receipt(2));
    const list = await new ReceiptStore(kv).list(); // a fresh instance over the same storage, like after an app restart
    expect(list.map((r) => r.reference)).toEqual(["UF-2026-00002", "UF-2026-00001"]);
    expect((await new ReceiptStore(kv).get("c1"))?.reference).toBe("UF-2026-00001");
  });

  it("replaces a receipt with the same id instead of duplicating it, and keeps only the latest 50", async () => {
    const store = new ReceiptStore(new MemoryKeyValue());
    await store.add(receipt(1));
    await store.add({ ...receipt(1), photos: 3 });
    expect(await store.list()).toHaveLength(1);
    for (let i = 2; i <= 60; i++) await store.add(receipt(i));
    const list = await store.list();
    expect(list).toHaveLength(50);
    expect(list[0].id).toBe("c60");
  });

  it("opens with an empty list when the stored value is corrupt", async () => {
    const kv = new MemoryKeyValue();
    await kv.setItem("uf.receipts", "{not json");
    expect(await new ReceiptStore(kv).list()).toEqual([]);
  });
});

describe("unsent complaint draft", () => {
  it("keeps the words until the complaint is sent", async () => {
    const store = new DraftStore(new MemoryKeyValue());
    expect(await store.get()).toBeUndefined();
    await store.save({ description: "Water is dirty", locationText: "Street 4", photos: [{ uri: "file:///a.jpg", size: 1024 }], savedAt: "2026-10-22T08:00:00Z" });
    expect((await store.get())?.description).toBe("Water is dirty");
    await store.clear();
    expect(await store.get()).toBeUndefined();
  });
});

describe("unsaved job note draft", () => {
  it("keeps a note per job and survives a restart", async () => {
    const kv = new MemoryKeyValue();
    await new JobDraftStore(kv).save("job-1", "Replaced the washer");
    await new JobDraftStore(kv).save("job-2", "Waiting for a part");
    expect(await new JobDraftStore(kv).get("job-1")).toBe("Replaced the washer"); // a fresh instance, like after a restart
    expect(await new JobDraftStore(kv).get("job-2")).toBe("Waiting for a part");
  });

  it("clears one job's note without touching another", async () => {
    const store = new JobDraftStore(new MemoryKeyValue());
    await store.save("job-1", "a");
    await store.save("job-2", "b");
    await store.clear("job-1");
    expect(await store.get("job-1")).toBeUndefined();
    expect(await store.get("job-2")).toBe("b");
  });
});

const crossing = (n: number, direction: "in" | "out" = "in"): PendingGateEvent => ({
  id: `g${n}`,
  input: { direction, subjectType: "visitor", subjectName: `Visitor ${n}` },
  label: `Visitor ${n} — ${direction}`,
  capturedAt: `2026-11-17T08:0${n}:00Z`,
});

describe("gate crossings queued offline (GT-01)", () => {
  it("keeps crossings in the order they were recorded and survives a restart", async () => {
    const kv = new MemoryKeyValue();
    await new GateQueue(kv).add(crossing(1));
    await new GateQueue(kv).add(crossing(2, "out"));
    const list = await new GateQueue(kv).list(); // fresh instance, like after the app reopens at the gate
    expect(list.map((e) => e.id)).toEqual(["g1", "g2"]);
    expect(list[1].input.direction).toBe("out");
  });

  it("replaces a crossing with the same id rather than duplicating it", async () => {
    const store = new GateQueue(new MemoryKeyValue());
    await store.add(crossing(1));
    await store.add({ ...crossing(1), label: "edited" });
    const list = await store.list();
    expect(list).toHaveLength(1);
    expect(list[0].label).toBe("edited");
  });

  it("removes a crossing once it has synced, leaving the rest", async () => {
    const store = new GateQueue(new MemoryKeyValue());
    await store.add(crossing(1));
    await store.add(crossing(2));
    await store.remove("g1");
    expect((await store.list()).map((e) => e.id)).toEqual(["g2"]);
  });

  it("opens with an empty queue when the stored value is corrupt", async () => {
    const kv = new MemoryKeyValue();
    await kv.setItem("uf.gateQueue", "{not json");
    expect(await new GateQueue(kv).list()).toEqual([]);
  });
});
