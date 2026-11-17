import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  DraftStore,
  JobDraftStore,
  MemoryKeyValue,
  ReceiptStore,
  clearUserData,
  queueStorage,
  reconcileUserData,
  sessionStore,
  type KeyValue,
  type Receipt,
} from "./store";

// The reported bug: one phone, used by more than one resident. Receipts, the unsent draft and the upload
// queue live under device-global keys, so without this isolation a new resident would see — and tap into —
// the previous resident's receipts, whose complaints are not theirs on the server ("something went wrong").

const OWNER_KEY = "uf.dataOwner";
const USER_KEYS = ["uf.receipts", "uf.draft", "uf.jobDrafts", "uf.uploadQueue", "uf.uploadSessions"];
const UNRELATED = { "uf.token": "jwt-123", "uf.lastRole": "Resident" }; // survives sign-out / user switch

const receipt = (n: number): Receipt => ({
  id: `c${n}`,
  reference: `UF-2026-${String(n).padStart(5, "0")}`,
  description: "Leak",
  createdAt: "2026-10-22T08:00:00Z",
  needsLocation: false,
  photos: 1,
});

const draft = (description: string) => ({ description, locationText: "", photos: [], savedAt: "2026-10-22T08:00:00Z" });

/** Fills every per-user store with something, plus two unrelated keys, so wipes can be checked precisely. */
async function seedEverything(kv: KeyValue) {
  await new ReceiptStore(kv).add(receipt(1));
  await new DraftStore(kv).save(draft("water is dirty"));
  await new JobDraftStore(kv).save("job-1", "replaced the washer");
  await queueStorage(kv).save([{ id: "q1" } as never]); // a pending photo upload
  await sessionStore(kv).set("q1", "upload-url"); // its resumable-upload session
  for (const [k, v] of Object.entries(UNRELATED)) await kv.setItem(k, v);
}

async function snapshot(kv: KeyValue) {
  return {
    receipts: await new ReceiptStore(kv).list(),
    draft: await new DraftStore(kv).get(),
    jobDraft: await new JobDraftStore(kv).get("job-1"),
    queue: await queueStorage(kv).load(),
    session: await sessionStore(kv).get("q1"),
    owner: await kv.getItem(OWNER_KEY),
  };
}

async function expectAllUserDataGone(kv: KeyValue) {
  expect(await new ReceiptStore(kv).list()).toEqual([]);
  expect(await new DraftStore(kv).get()).toBeUndefined();
  expect(await new JobDraftStore(kv).get("job-1")).toBeUndefined();
  expect(await queueStorage(kv).load()).toEqual([]);
  expect(await sessionStore(kv).get("q1")).toBeUndefined();
}

async function expectUnrelatedKept(kv: KeyValue) {
  for (const [k, v] of Object.entries(UNRELATED)) expect(await kv.getItem(k)).toBe(v);
}

describe("reconcileUserData — who the on-device data belongs to", () => {
  let kv: MemoryKeyValue;
  beforeEach(() => { kv = new MemoryKeyValue(); });

  it("records the owner and leaves a clean device clean (first sign-in ever)", async () => {
    await reconcileUserData(kv, "user-A");
    expect(await kv.getItem(OWNER_KEY)).toBe("user-A");
    await expectAllUserDataGone(kv);
  });

  it("keeps a person's own receipts, draft and uploads when they sign back in (same owner)", async () => {
    await reconcileUserData(kv, "user-A");
    await seedEverything(kv);
    const before = await snapshot(kv);

    await reconcileUserData(kv, "user-A"); // e.g. re-authenticating after a token expiry

    expect(await snapshot(kv)).toEqual(before); // nothing lost
  });

  it("does not rewrite the owner marker on a same-owner reconcile", async () => {
    await reconcileUserData(kv, "user-A");
    const spy = vi.spyOn(kv, "setItem");
    await reconcileUserData(kv, "user-A");
    expect(spy).not.toHaveBeenCalled(); // early return — no writes, no churn
  });

  it("wipes everything and takes ownership when a DIFFERENT person signs in", async () => {
    await reconcileUserData(kv, "user-A");
    await seedEverything(kv);

    await reconcileUserData(kv, "user-B");

    await expectAllUserDataGone(kv);
    expect(await kv.getItem(OWNER_KEY)).toBe("user-B");
    await expectUnrelatedKept(kv); // the token / last role are not user content
  });

  it("wipes each one of the user-data keys, not just receipts", async () => {
    await reconcileUserData(kv, "user-A");
    for (const key of USER_KEYS) await kv.setItem(key, JSON.stringify(["something"]));

    await reconcileUserData(kv, "user-B");

    for (const key of USER_KEYS) expect(await kv.getItem(key)).toBeNull();
  });

  it("wipes legacy data left by an install from before ownership was tracked (no owner recorded)", async () => {
    await seedEverything(kv); // data exists, but OWNER_KEY was never set
    expect(await kv.getItem(OWNER_KEY)).toBeNull();

    await reconcileUserData(kv, "user-A");

    await expectAllUserDataGone(kv);
    expect(await kv.getItem(OWNER_KEY)).toBe("user-A");
  });

  it("isolates data across a chain of hand-offs A → B → A", async () => {
    await reconcileUserData(kv, "user-A");
    await new ReceiptStore(kv).add(receipt(1));

    await reconcileUserData(kv, "user-B");
    await new ReceiptStore(kv).add(receipt(2));
    expect((await new ReceiptStore(kv).list()).map((r) => r.id)).toEqual(["c2"]); // no trace of A

    await reconcileUserData(kv, "user-A"); // A returns to a phone B last used
    expect(await new ReceiptStore(kv).list()).toEqual([]); // and does not inherit B's receipt
    expect(await kv.getItem(OWNER_KEY)).toBe("user-A");
  });

  it("empties the real upload queue and upload sessions, read back through their own accessors", async () => {
    await reconcileUserData(kv, "user-A");
    await queueStorage(kv).save([{ id: "q1" } as never, { id: "q2" } as never]);
    await sessionStore(kv).set("q2", "resume-here");
    expect(await queueStorage(kv).load()).toHaveLength(2);

    await reconcileUserData(kv, "user-B");

    expect(await queueStorage(kv).load()).toEqual([]);
    expect(await sessionStore(kv).get("q2")).toBeUndefined();
  });

  it("treats an unreadable owner as 'not this person' and still wipes (errs on the safe side)", async () => {
    await seedEverything(kv);
    const kvThrowsOnRead: KeyValue = {
      getItem: () => Promise.reject(new Error("storage unavailable")),
      setItem: (k, v) => kv.setItem(k, v),
      removeItem: (k) => kv.removeItem(k),
    };

    await expect(reconcileUserData(kvThrowsOnRead, "user-A")).resolves.toBeUndefined(); // never throws

    await expectAllUserDataGone(kv);
    expect(await kv.getItem(OWNER_KEY)).toBe("user-A");
  });

  it("does not record the new owner when a wipe is incomplete, so the next sign-in retries (fail-safe)", async () => {
    await reconcileUserData(kv, "user-A");
    await seedEverything(kv);
    let failRemoval = true;
    const kvRemoveFails: KeyValue = {
      getItem: (k) => kv.getItem(k),
      setItem: (k, v) => kv.setItem(k, v),
      // The upload queue cannot be removed on this first attempt (e.g. a transient storage lock).
      removeItem: (k) => (failRemoval && k === "uf.uploadQueue" ? Promise.reject(new Error("locked")) : kv.removeItem(k)),
    };

    await expect(reconcileUserData(kvRemoveFails, "user-B")).resolves.toBeUndefined(); // never throws

    // Ownership is NOT claimed for B while A's data may remain — otherwise a same-owner early return
    // would leave that data readable forever.
    expect(await kv.getItem(OWNER_KEY)).toBe("user-A");

    // The next sign-in (storage now healthy) completes the wipe and only then takes ownership.
    failRemoval = false;
    await reconcileUserData(kv, "user-B");
    await expectAllUserDataGone(kv);
    expect(await kv.getItem(OWNER_KEY)).toBe("user-B");
  });

  it("does not reject when writing the new owner fails", async () => {
    const kvSetFails: KeyValue = {
      getItem: (k) => kv.getItem(k),
      setItem: (k) => (k === OWNER_KEY ? Promise.reject(new Error("disk full")) : Promise.resolve()),
      removeItem: (k) => kv.removeItem(k),
    };
    await expect(reconcileUserData(kvSetFails, "user-A")).resolves.toBeUndefined();
  });
});

describe("clearUserData — sign-out leaves nothing of the person behind", () => {
  let kv: MemoryKeyValue;
  beforeEach(() => { kv = new MemoryKeyValue(); });

  it("removes every user-data key and forgets the owner", async () => {
    await reconcileUserData(kv, "user-A");
    await seedEverything(kv);

    await clearUserData(kv);

    await expectAllUserDataGone(kv);
    expect(await kv.getItem(OWNER_KEY)).toBeNull(); // owner forgotten too
  });

  it("leaves unrelated keys (auth token, last role) untouched", async () => {
    await reconcileUserData(kv, "user-A");
    await seedEverything(kv);

    await clearUserData(kv);

    await expectUnrelatedKept(kv);
  });

  it("is safe to call on an already-empty device", async () => {
    await expect(clearUserData(kv)).resolves.toBeUndefined();
    await expectAllUserDataGone(kv);
  });

  it("does not reject when a removeItem fails", async () => {
    await reconcileUserData(kv, "user-A");
    await seedEverything(kv);
    const kvRemoveFails: KeyValue = {
      getItem: (k) => kv.getItem(k),
      setItem: (k, v) => kv.setItem(k, v),
      removeItem: (k) => (k === "uf.draft" ? Promise.reject(new Error("locked")) : kv.removeItem(k)),
    };
    await expect(clearUserData(kvRemoveFails)).resolves.toBeUndefined();
  });

  it("after sign-out, the next person signing in starts from a clean slate", async () => {
    // Full regression for the reported bug, end to end through the public stores.
    await reconcileUserData(kv, "user-A");
    await new ReceiptStore(kv).add(receipt(1)); // A files a complaint; a receipt is kept on the phone
    await new DraftStore(kv).save(draft("half-written report"));

    await clearUserData(kv); // A signs out

    // Something is even added before B's /me resolves (e.g. a stray cached write) — it must not leak either.
    await new ReceiptStore(kv).add(receipt(9));
    await reconcileUserData(kv, "user-B"); // B signs in on the same phone

    await expectAllUserDataGone(kv);
    expect(await kv.getItem(OWNER_KEY)).toBe("user-B");
  });
});
