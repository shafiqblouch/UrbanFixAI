import { Alert, Button } from "@urbanfix/ui";
import { useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api, ok, okPaged, upload, type S } from "../api";
import { ComplaintStatusChip, ErrorNote, usePeople } from "../components";
import { useAction, useDebounced, useLoad } from "../hooks";
import { Icon, type IconName } from "../icons";
import { useFormat, useT, type Key } from "../i18n";
import { useLookups } from "../lookups";
import { useSession } from "../session";

const MAX = 4000;
const OPEN = ["New", "OnHold", "UnderReview", "Assigned", "InProgress"];

/** The longest word of a description (5+ letters): a cheap, honest key for finding open complaints that talk about the same thing. */
const keyword = (text: string) => text.split(/[\s,.;:!?()"'،۔]+/).filter((w) => w.length >= 5).sort((a, b) => b.length - a.length)[0] ?? "";

/** Entry form for walk-in and phone complaints, and for residents using the web (WP-05), with live checks beside it. */
export function NewComplaintPage() {
  const t = useT();
  const f = useFormat();
  const navigate = useNavigate();
  const { role } = useSession();
  const { nameOf } = usePeople();
  const lookups = useLookups();
  const staff = role !== "Resident";
  const action = useAction();

  const [channel, setChannel] = useState("phone");
  const [houseFilter, setHouseFilter] = useState("");
  const [houseId, setHouseId] = useState("");
  const [locationId, setLocationId] = useState("");
  const [reporterId, setReporterId] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [description, setDescription] = useState("");
  const [locationText, setLocationText] = useState("");
  const [callerPhone, setCallerPhone] = useState("");
  const [assetRef, setAssetRef] = useState("");
  const [files, setFiles] = useState<File[]>([]);
  const [created, setCreated] = useState<S["CreatedDto"]>();
  const [skipped, setSkipped] = useState(0);

  const houses = useLoad(() => (staff ? ok(api.GET("/houses", {})) : Promise.resolve([])), [staff]);
  const residents = useLoad(
    () => (staff && houseId ? ok(api.GET("/houses/{houseId}/residents", { params: { path: { houseId } } })) : Promise.resolve([])),
    [staff, houseId]);

  const names = useMemo(() => new Map(lookups.locations.map((l) => [l.id, l.name])), [lookups.locations]);
  const houseLabel = (h: S["HouseDto"]) => h.locationPath.split("/").filter(Boolean).map((id) => names.get(id) ?? "").filter(Boolean).join(" › ");
  const houseOptions = (houses.data ?? [])
    .map((h) => ({ value: h.id, label: houseLabel(h) }))
    .filter((o) => o.label.toLowerCase().includes(houseFilter.trim().toLowerCase()))
    .slice(0, 50);
  const leafLocations = lookups.locations.filter((l) => l.kind !== "sector");

  // What the live checks beside the form look at.
  const house = (houses.data ?? []).find((h) => h.id === houseId);
  const placeId = house?.locationId ?? locationId;
  const placeName = house ? houseLabel(house) : names.get(locationId) ?? "";
  const reporter = (residents.data ?? []).find((r) => r.userId === reporterId);
  const word = keyword(description);
  const debouncedWord = useDebounced(word, 500);

  const similar = useLoad(async () => {
    if (!staff || debouncedWord === "") return [];
    try { return (await okPaged(api.GET("/complaints", { params: { query: { q: debouncedWord, locationId: placeId || undefined, limit: 20 } } }))).items.filter((c) => OPEN.includes(c.status)).slice(0, 3); } catch { return []; }
  }, [staff, debouncedWord, placeId]);
  const history = useLoad(async () => {
    if (!staff || !placeId) return { items: [] as S["ComplaintDto"][], total: 0 };
    try { return await okPaged(api.GET("/complaints", { params: { query: { locationId: placeId, limit: 3 } } })); } catch { return { items: [], total: 0 }; }
  }, [staff, placeId]);

  const hasMedia = files.some((x) => x.type.startsWith("image/") || x.type.startsWith("audio/"));

  async function submit() {
    const result = await action.run(async () => {
      const made = await ok(api.POST("/complaints", { body: {
        description, channel: staff ? channel : null, categoryId: categoryId || null, houseId: houseId || null,
        locationId: houseId ? null : locationId || null, locationText: locationText || null, reportedByUserId: staff ? reporterId || null : null,
        // A caller who is not picked from the house is matched by number; unknown numbers follow the society's policy (CP-08).
        reporterPhone: staff && !reporterId ? callerPhone.trim() || null : null, assetRef: assetRef.trim() || null,
        // With only a photo or voice note, the AI reads or listens and writes the description and category.
        mediaOnly: description.trim() === "" && hasMedia,
      } }));
      // The complaint exists now; a photo that fails to upload must not lose it.
      let failed = 0;
      for (const file of files) { try { await upload(`/complaints/${made.id}/media`, file); } catch { failed++; } }
      setSkipped(failed);
      return made;
    });
    if (result) setCreated(result);
  }

  function reset() {
    setCreated(undefined); setDescription(""); setLocationText(""); setFiles([]); setHouseId(""); setLocationId(""); setReporterId(""); setCategoryId(""); setSkipped(0); setCallerPhone(""); setAssetRef("");
  }

  if (created) {
    return (
      <div className="newc newc--done">
        <section className="newc-card">
          <div className="stack">
            <h1 className="newc-title"><Icon name="approvals" size={18} />{t("menu.new")}</h1>
            <Alert tone="success">{t("new.created", { reference: created.reference })}</Alert>
            {created.needsLocation && <Alert tone="warning">{t("new.needsLocation")}</Alert>}
            {created.status === "OnHold" && !created.needsLocation && <Alert tone="warning">{t("new.heldUnverified")}</Alert>}
            {skipped > 0 && <Alert tone="warning">{t("new.photosFailed", { count: skipped })}</Alert>}
            <div className="row">
              <Link className="newc-submit" to={`/complaints/${created.id}`}>{t("new.open")}</Link>
              <Button variant="secondary" onClick={reset}>{t("new.another")}</Button>
            </div>
          </div>
        </section>
      </div>
    );
  }

  const channels: { id: string; icon: IconName }[] = [{ id: "phone", icon: "phone" }, { id: "walk_in", icon: "walk" }];
  const descOk = description.trim().length >= 10 || (hasMedia && description.trim() === "");
  const placeOk = placeId !== "" || locationText.trim() !== "";
  const who = reporterId ? "ok" : callerPhone.trim() ? "phone" : "no";
  const similarCount = similar.data?.length ?? 0;

  return (
    <form className="newc" onSubmit={(e) => { e.preventDefault(); void submit(); }}>
      <div className="newc-main">
        <nav className="newc-crumb" aria-label={t("newc.back")}>
          <button type="button" className="icon-btn icon-btn--sm icon-btn--box" onClick={() => navigate("/complaints")} aria-label={t("newc.back")} title={t("newc.back")}><Icon name="arrow" size={14} /></button>
          <span className="cmp-muted">{t("menu.complaints")}</span><Icon name="chevron" size={11} /><strong>{t("newc.crumb")}</strong>
        </nav>

        <section className="newc-card newc-banner">
          <div>
            <h1 className="newc-title"><Icon name="phone" size={18} />{t("newc.intake")}</h1>
            <p className="newc-sub">{t("newc.intakeSub")}</p>
          </div>
        </section>

        {staff && (
          <section className="newc-card">
            <span className="newc-label">{t("newc.channel")} <b aria-hidden="true">*</b></span>
            <div className="newc-channels" role="radiogroup" aria-label={t("newc.channel")}>
              {channels.map((c) => (
                <button key={c.id} type="button" role="radio" aria-checked={channel === c.id} className={`newc-channel${channel === c.id ? " newc-channel--on" : ""}`} onClick={() => setChannel(c.id)}>
                  <Icon name={c.icon} size={14} />{t(`channel.${c.id}` as Key)}
                </button>
              ))}
            </div>
          </section>
        )}

        <section className="newc-card newc-stack">
          <div>
            <div className="newc-row">
              <label className="newc-label" htmlFor="newc-desc">{t("description")} {!hasMedia && <b aria-hidden="true">*</b>}</label>
              <span className="newc-count">{t("newc.chars", { count: f.number(description.length), max: f.number(MAX) })}</span>
            </div>
            <textarea id="newc-desc" rows={4} value={description} onChange={(e) => setDescription(e.target.value)} required={!hasMedia} maxLength={MAX} />
            {hasMedia && description.trim() === "" && <p className="newc-hint">{t("newc.mediaOnly")}</p>}
          </div>
          <div>
            <label className="newc-label" htmlFor="newc-cat">{t("category")}</label>
            <select id="newc-cat" value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
              <option value="">✨ {t("new.categoryLater")}</option>
              {lookups.categories.filter((c) => c.isActive).map((c) => <option key={c.id} value={c.id}>{lookups.categoryName(c.id)}</option>)}
            </select>
          </div>
        </section>

        <section className="newc-card newc-stack">
          <h2 className="newc-h"><Icon name="directory" size={14} />{t("newc.callerCard")}</h2>
          {staff && (
            <div className="newc-grid">
              <div>
                <label className="newc-label" htmlFor="newc-phone">{t("new.callerPhone")}</label>
                <div className="newc-input">
                  <span aria-hidden="true"><Icon name="phone" size={14} /></span>
                  <input id="newc-phone" inputMode="tel" dir="ltr" value={callerPhone} onChange={(e) => setCallerPhone(e.target.value)} placeholder="0300 1234567" disabled={reporterId !== ""} />
                </div>
                <p className="newc-hint">{t("new.callerPhoneHint")}</p>
              </div>
              <div>
                <label className="newc-label" htmlFor="newc-house">{t("house")}</label>
                <div className="newc-input">
                  <span aria-hidden="true"><Icon name="search" size={14} /></span>
                  <input aria-label={t("new.findHouse")} placeholder={t("new.findHouse")} value={houseFilter} onChange={(e) => setHouseFilter(e.target.value)} />
                </div>
                <select id="newc-house" className="newc-gap" value={houseId} onChange={(e) => { setHouseId(e.target.value); setReporterId(""); }}>
                  <option value="">{t("new.noHouse")}</option>
                  {houseOptions.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
              </div>
            </div>
          )}
          {staff && houseId && (
            <div>
              <label className="newc-label" htmlFor="newc-reporter">{t("reporter")}</label>
              <select id="newc-reporter" value={reporterId} onChange={(e) => setReporterId(e.target.value)}>
                <option value="">{t("new.anonymous")}</option>
                {(residents.data ?? []).filter((r) => !r.toDate).map((r) => <option key={r.userId} value={r.userId}>{`${nameOf(r.userId)} (${r.relation})`}</option>)}
              </select>
            </div>
          )}
          <div>
            <label className="newc-label" htmlFor="newc-where">{t("new.locationText")}</label>
            <input id="newc-where" value={locationText} onChange={(e) => setLocationText(e.target.value)} />
          </div>
          <div className="newc-grid">
            {staff && !houseId && (
              <div>
                <label className="newc-label" htmlFor="newc-loc">{t("location")}</label>
                <select id="newc-loc" value={locationId} onChange={(e) => setLocationId(e.target.value)}>
                  <option value="">{t("choose")}</option>
                  {leafLocations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
                </select>
              </div>
            )}
            <div>
              <label className="newc-label" htmlFor="newc-asset">{t("new.asset")}</label>
              <input id="newc-asset" dir="ltr" className="newc-mono" value={assetRef} onChange={(e) => setAssetRef(e.target.value)} maxLength={100} placeholder="ELEC-PL-092" />
              <p className="newc-hint">{t("new.assetHint")}</p>
            </div>
          </div>
        </section>

        <Evidence files={files} onFiles={setFiles} />

        <ErrorNote error={action.error} />
        <div className="newc-actions">
          <Button type="button" variant="secondary" onClick={() => navigate("/complaints")}>{t("newc.cancel")}</Button>
          <button type="submit" className="newc-submit" disabled={action.busy}><Icon name="approvals" size={15} />{t("new.submit")}</button>
        </div>
      </div>

      {staff && (
        <aside className="newc-side" aria-label={t("newc.assistant")}>
          <section className="newc-card newc-assist">
            <header className="newc-assist__head">
              <span className="newc-assist__title"><span className="newc-assist__icon" aria-hidden="true">✨</span>{t("newc.assistant")}</span>
              <span className="newc-chip">{t("newc.live")}</span>
            </header>
            <ul className="newc-checks">
              <Check ok={descOk} title={t("newc.checkDesc")} text={descOk ? t("newc.checkDescOk") : t("newc.checkDescNo")} />
              <Check ok={placeOk} title={t("newc.checkPlace")} text={placeOk ? t("newc.checkPlaceOk") : t("newc.checkPlaceNo")} />
              <Check ok={who !== "no"} title={t("newc.checkWho")} text={who === "ok" ? t("newc.checkWhoOk") : who === "phone" ? t("newc.checkWhoPhone") : t("newc.checkWhoNo")} />
              <Check ok={categoryId !== ""} title={t("newc.category")} text={categoryId ? lookups.categoryName(categoryId) : t("newc.categoryAi")} soft />
            </ul>
            <div className="newc-box">
              <span className="newc-label">{t("newc.linked")}</span>
              {reporter ? (
                <div className="newc-person">
                  <span className="newc-person__avatar" aria-hidden="true">{nameOf(reporter.userId).split(/\s+/).map((p) => p[0]).slice(0, 2).join("").toUpperCase()}</span>
                  <div><strong>{nameOf(reporter.userId)}</strong><span className="cmp-muted">{placeName}{callerPhone ? ` • ${callerPhone}` : ""}</span></div>
                </div>
              ) : <p className="newc-hint">{t("newc.linkedNone")}</p>}
            </div>
            <div className={`newc-box${similarCount > 0 ? " newc-box--warn" : ""}`}>
              <div className="newc-row">
                <span className="newc-label newc-label--flat"><Icon name="alert" size={13} />{t("newc.dupTitle")}</span>
                {similarCount > 0 && <span className="newc-chip newc-chip--warn">{t("newc.dupFound", { count: f.number(similarCount) })}</span>}
              </div>
              {similarCount === 0 ? <p className="newc-hint">{t("newc.dupNone")}</p> : (
                <>
                  <p className="newc-hint">{t("newc.dupHint", { word })}</p>
                  <ul className="newc-list">
                    {(similar.data ?? []).map((c) => (
                      <li key={c.id}>
                        <Link to={`/complaints/${c.id}`} className="newc-ref">{c.reference}</Link>
                        <span className="newc-clip">{c.description}</span>
                        <ComplaintStatusChip status={c.status} />
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </div>
          </section>

          <section className="newc-card">
            <h2 className="newc-h"><Icon name="pin" size={14} />{t("newc.pinTitle")}</h2>
            <div className="newc-map" aria-label={placeName || t("newc.pinNone")}>
              <div className="newc-map__pin">
                <span className="newc-map__tag">{placeName || locationText || t("newc.pinNone")}</span>
                <Icon name="pin" size={28} />
              </div>
            </div>
          </section>

          <section className="newc-card">
            <h2 className="newc-h newc-row"><span className="newc-h__in"><Icon name="history" size={14} />{t("newc.history")}</span>
              {placeId && <span className="newc-count">{t("newc.historyTotal", { count: f.number(history.data?.total ?? 0) })}</span>}</h2>
            {(history.data?.items.length ?? 0) === 0 ? <p className="newc-hint">{t("newc.historyNone")}</p> : (
              <ul className="newc-list">
                {history.data?.items.map((c) => (
                  <li key={c.id} className="newc-box">
                    <div><Link to={`/complaints/${c.id}`} className="newc-ref">{c.reference}</Link><span className="newc-clip">{c.description}</span></div>
                    <ComplaintStatusChip status={c.status} />
                  </li>
                ))}
              </ul>
            )}
          </section>
        </aside>
      )}
    </form>
  );
}

/** Re-encodes a recording as 16 kHz mono 16-bit WAV, or returns null when the browser cannot decode it. */
async function toWav(blob: Blob): Promise<Blob | null> {
  try {
    const decoder = new AudioContext();
    const decoded = await decoder.decodeAudioData(await blob.arrayBuffer());
    void decoder.close();
    const rate = 16000;
    const offline = new OfflineAudioContext(1, Math.max(1, Math.ceil(decoded.duration * rate)), rate);
    const source = offline.createBufferSource();
    source.buffer = decoded;
    source.connect(offline.destination);
    source.start();
    const pcm = (await offline.startRendering()).getChannelData(0);

    const view = new DataView(new ArrayBuffer(44 + pcm.length * 2));
    const text = (at: number, s: string) => { for (let i = 0; i < s.length; i++) view.setUint8(at + i, s.charCodeAt(i)); };
    text(0, "RIFF"); view.setUint32(4, 36 + pcm.length * 2, true); text(8, "WAVE"); text(12, "fmt ");
    view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
    view.setUint32(24, rate, true); view.setUint32(28, rate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
    text(36, "data"); view.setUint32(40, pcm.length * 2, true);
    pcm.forEach((v, i) => view.setInt16(44 + i * 2, Math.round(Math.max(-1, Math.min(1, v)) * 32767), true));
    return new Blob([view], { type: "audio/wav" });
  } catch {
    return null;
  }
}

function Check({ ok: good, title, text, soft }: { ok: boolean; title: string; text: string; soft?: boolean }) {
  return (
    <li className={`newc-check${good ? " newc-check--ok" : soft ? "" : " newc-check--no"}`}>
      <span className="newc-check__dot" aria-hidden="true"><Icon name={good ? "check" : soft ? "bolt" : "alert"} size={12} /></span>
      <div><strong>{title}</strong><span>{text}</span></div>
    </li>
  );
}

/** Photos (picked or dropped) and a voice note recorded in the browser; both go up after the complaint is saved. */
function Evidence({ files, onFiles }: { files: File[]; onFiles: (f: File[]) => void }) {
  const t = useT();
  const input = useRef<HTMLInputElement>(null);
  const recorder = useRef<MediaRecorder>();
  const stream = useRef<MediaStream>();
  const chunks = useRef<Blob[]>([]);
  const filesRef = useRef(files);
  filesRef.current = files;
  const [over, setOver] = useState(false);
  const [recording, setRecording] = useState(false);
  const [seconds, setSeconds] = useState(0);
  const [micError, setMicError] = useState(false);
  const [noMic, setNoMic] = useState(false);
  const audioInput = useRef<HTMLInputElement>(null);
  const previews = useMemo(() => files.map((file) => ({ file, url: file.type.startsWith("image/") ? URL.createObjectURL(file) : undefined })), [files]);
  useEffect(() => () => previews.forEach((p) => p.url && URL.revokeObjectURL(p.url)), [previews]);
  useEffect(() => () => { stream.current?.getTracks().forEach((tr) => tr.stop()); }, []);
  // Desktops often have no microphone (or no recording support); say so up front and offer an audio file instead.
  useEffect(() => {
    const md = navigator.mediaDevices;
    if (!md?.getUserMedia || typeof MediaRecorder === "undefined") { setNoMic(true); return; }
    void md.enumerateDevices?.().then((d) => setNoMic(!d.some((x) => x.kind === "audioinput"))).catch(() => {});
  }, []);
  useEffect(() => {
    if (!recording) return;
    const id = setInterval(() => setSeconds((s) => s + 1), 1000);
    return () => clearInterval(id);
  }, [recording]);

  const add = (list: FileList | File[]) => onFiles([...filesRef.current, ...Array.from(list).filter((x) => x.type.startsWith("image/") || x.type.startsWith("audio/"))]);

  /** Attached audio goes up as-is when the server reads it (WAV, MP3, OGG, M4A); anything else (WebM, FLAC...) is converted to WAV first. */
  async function addAudio(list: FileList) {
    const out: File[] = [];
    for (const f of Array.from(list)) {
      if (!f.type.startsWith("audio/") && !/\.(wav|mp3|ogg|m4a|aac|webm|opus|flac)$/i.test(f.name)) continue;
      if (/\.(wav|mp3|ogg|m4a)$/i.test(f.name) && /wav|mpeg|mp3|ogg|mp4|m4a/.test(f.type)) { out.push(f); continue; }
      const wav = await toWav(f);
      out.push(wav ? new File([wav], f.name.replace(/\.[^.]+$/, "") + ".wav", { type: "audio/wav" }) : f);
    }
    if (out.length) onFiles([...filesRef.current, ...out]);
  }

  async function toggleRecord() {
    if (recording) { recorder.current?.stop(); return; }
    try {
      const media = await navigator.mediaDevices.getUserMedia({ audio: true });
      stream.current = media;
      const rec = new MediaRecorder(media);
      chunks.current = [];
      rec.ondataavailable = (e) => { if (e.data.size > 0) chunks.current.push(e.data); };
      rec.onstop = () => {
        media.getTracks().forEach((tr) => tr.stop());
        const type = rec.mimeType || "audio/webm";
        const recorded = new Blob(chunks.current, { type });
        // The server and the speech AI read WAV, MP3, OGG and M4A, not the WebM that browsers record, so convert first.
        void toWav(recorded).then((wav) => {
          const ext = type.includes("ogg") ? "ogg" : type.includes("mp4") ? "m4a" : "webm";
          const file = wav ? new File([wav], "voice-note.wav", { type: "audio/wav" }) : new File([recorded], `voice-note.${ext}`, { type });
          onFiles([...filesRef.current, file]);
          setRecording(false);
        });
      };
      recorder.current = rec;
      setMicError(false); setSeconds(0); setRecording(true);
      rec.start();
    } catch { setMicError(true); setNoMic(true); setRecording(false); }
  }

  const clock = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
  return (
    <section className="newc-card newc-stack">
      <h2 className="newc-h newc-row"><span className="newc-h__in"><Icon name="camera" size={14} />{t("newc.evidence")}</span><span className="newc-count">{t("newc.optional")}</span></h2>
      <div className="newc-grid">
        <div className={`newc-drop${over ? " newc-drop--over" : ""}`} role="button" tabIndex={0} onClick={() => input.current?.click()}
          onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); input.current?.click(); } }}
          onDragOver={(e) => { e.preventDefault(); setOver(true); }} onDragLeave={() => setOver(false)}
          onDrop={(e) => { e.preventDefault(); setOver(false); add(e.dataTransfer.files); }}>
          <Icon name="upload" size={22} />
          <strong>{t("newc.drop")}</strong>
          <span className="newc-hint">{t("newc.dropHint")}</span>
          <input ref={input} type="file" hidden accept="image/jpeg,image/png,image/webp,audio/*" multiple onChange={(e) => { if (e.target.files) add(e.target.files); e.target.value = ""; }} />
        </div>
        <div className="newc-voice">
          <div className="newc-row"><span className="newc-label newc-label--flat"><Icon name="mic" size={13} />{t("newc.voice")}</span>
            <span className={`newc-count${recording ? " newc-count--rec" : ""}`} role="status">{recording ? t("newc.recording", { time: clock }) : t("newc.ready")}</span></div>
          <button type="button" className={`newc-rec${recording ? " newc-rec--on" : ""}`} onClick={() => void toggleRecord()} disabled={noMic && !recording} aria-label={recording ? t("newc.stop") : t("newc.record")} aria-pressed={recording}>
            <Icon name={recording ? "stop" : "mic"} size={18} />
          </button>
          <p className="newc-hint newc-center" role={micError ? "alert" : undefined}>{micError ? t("newc.micDenied") : noMic ? t("newc.noMic") : t("newc.recordHint")}</p>
          <button type="button" className="newc-audio-btn" onClick={() => audioInput.current?.click()} disabled={recording}><Icon name="upload" size={14} />{t("newc.uploadAudio")}</button>
          <input ref={audioInput} type="file" hidden accept="audio/*,.wav,.mp3,.ogg,.m4a,.aac,.webm,.opus,.flac" multiple onChange={(e) => { if (e.target.files) void addAudio(e.target.files); e.target.value = ""; }} />
        </div>
      </div>
      {previews.length > 0 && (
        <ul className="newc-previews">
          {previews.map((p, i) => (
            <li key={`${p.file.name}-${i}`}>
              {p.url ? <img src={p.url} alt={p.file.name} /> : <span className="newc-previews__audio"><Icon name="mic" size={18} /><small>{p.file.name}</small></span>}
              <button type="button" aria-label={`${t("newc.remove")}: ${p.file.name}`} onClick={() => onFiles(files.filter((_, j) => j !== i))}><Icon name="close" size={12} /></button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

