import { describe, expect, it } from "vitest";
import { DraftStore, MemoryKeyValue, ReceiptStore, type Receipt } from "./store";

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
    await store.save({ description: "Water is dirty", locationText: "Street 4", photoUris: ["file:///a.jpg"], savedAt: "2026-10-22T08:00:00Z" });
    expect((await store.get())?.description).toBe("Water is dirty");
    await store.clear();
    expect(await store.get()).toBeUndefined();
  });
});
