import type { NativeStackScreenProps } from "@react-navigation/native-stack";
import { useNavigation } from "@react-navigation/native";
import { useCallback, useEffect, useState } from "react";
import { Pressable, View } from "react-native";
import { api, ok, type S } from "../api";
import { errorText, useAction, useLoad, usePolling } from "../hooks";
import { useI18n, useT } from "../i18n";
import type { Key } from "../i18n/strings";
import type { Draft, Receipt } from "../offline/store";
import { isOnline, useOnline } from "../platform/device";
import { takePhoto, type Photo } from "../platform/photo";
import { useVoiceRecorder } from "../platform/voice";
import { isSafetyText } from "../safety";
import { currentPosition } from "../platform/device";
import { useServices } from "../services";
import { useSession } from "../session";
import { space, TOUCH, usePalette } from "../theme";
import { Banner, Button, Card, Chip, Empty, Field, Loading, Row, Screen, Txt } from "../ui";
import type { RootStackParams } from "../navigation";
import { EmergencyPrompt, FeedbackCard, FindByReference, MissingInfoCard } from "./residentExtras";

type Nav = NativeStackScreenProps<RootStackParams>["navigation"];

const statusTone: Record<S["ComplaintStatus"], "success" | "warning" | "danger" | "info" | "neutral"> = {
  New: "info", OnHold: "warning", UnderReview: "info", Assigned: "neutral", InProgress: "warning", Resolved: "success", Closed: "success", Rejected: "danger", Merged: "neutral",
};
const statusGlyph: Record<S["ComplaintStatus"], string> = {
  New: "○", OnHold: "⏸", UnderReview: "◔", Assigned: "◐", InProgress: "◑", Resolved: "✓", Closed: "✓", Rejected: "✕", Merged: "⇄",
};

export function StatusChip({ status }: { status: S["ComplaintStatus"] }) {
  const t = useT();
  return <Chip label={t(`status.${status}` as Key)} tone={statusTone[status]} glyph={statusGlyph[status]} />;
}

/** Resident home: quick actions and recent complaints (RA-01). */
export function HomeScreen() {
  const t = useT();
  const nav = useNavigation<Nav>();
  const { me } = useSession();
  const { waiting } = useServices();
  const online = useOnline();
  const recent = useLoad(() => ok(api.GET("/complaints", { params: { query: { limit: 3 } } })), []);
  const p = usePalette();

  const actions: { label: string; glyph: string; go: () => void }[] = [
    { label: t("home.report"), glyph: "＋", go: () => nav.navigate("Tabs", { screen: "new" } as never) },
    { label: t("home.mine"), glyph: "☰", go: () => nav.navigate("Tabs", { screen: "complaints" } as never) },
    { label: t("home.receipts"), glyph: "🧾", go: () => nav.navigate("Receipts") },
  ];

  return (
    <Screen>
      {!online && <Banner text={t("offline")} />}
      {waiting > 0 && <Banner tone="info" text={t("offline.queued", { count: waiting })} />}
      <Txt size="xl" bold>{t("home.greeting", { name: me?.displayName ?? "" })}</Txt>
      <Row wrap gap={space[3]}>
        {actions.map((a) => (
          <Pressable key={a.label} accessibilityRole="button" accessibilityLabel={a.label} onPress={a.go}
            style={{ flexBasis: "47%", flexGrow: 1, minHeight: 112, borderRadius: 16, backgroundColor: p.primarySoft, alignItems: "center", justifyContent: "center", gap: space[2], padding: space[3] }}>
            <Txt size="xxl" color={p.onPrimarySoft}>{a.glyph}</Txt>
            <Txt bold color={p.onPrimarySoft} style={{ textAlign: "center" }}>{a.label}</Txt>
          </Pressable>
        ))}
      </Row>
      <Txt size="lg" bold>{t("home.recent")}</Txt>
      {recent.loading && !recent.data ? <Loading /> : recent.data && recent.data.length > 0
        ? recent.data.map((c) => <ComplaintCard key={c.id} c={c} onPress={() => nav.navigate("ComplaintDetail", { id: c.id })} />)
        : <Empty text={t("home.noComplaints")} />}
    </Screen>
  );
}

function ComplaintCard({ c, onPress }: { c: S["ComplaintDto"]; onPress: () => void }) {
  const f = useDate();
  return (
    <Card onPress={onPress}>
      <Row><Txt bold style={{ flex: 1 }}>{c.reference}</Txt><StatusChip status={c.status} /></Row>
      <Txt numberOfLines={2}>{c.description}</Txt>
      <Txt size="sm" muted>{f(c.createdAt)}</Txt>
    </Card>
  );
}

export function useDate() {
  const { locale } = useI18n();
  return (iso: string) => new Intl.DateTimeFormat(locale === "ur" ? "ur-PK-u-nu-latn" : "en-GB", { dateStyle: "medium", timeStyle: "short" }).format(new Date(iso));
}

/** The resident's complaints; falls back to the saved receipts when there is no signal. */
export function ComplaintsScreen() {
  const t = useT();
  const nav = useNavigation<Nav>();
  const { receipts } = useServices();
  const list = useLoad(() => ok(api.GET("/complaints", { params: { query: { limit: 50 } } })), []);
  const [saved, setSaved] = useState<Receipt[]>([]);
  useEffect(() => { void receipts.list().then(setSaved); }, [receipts]);

  if (list.loading && !list.data) return <Screen><Loading /></Screen>;
  if (!list.data) {
    return (
      <Screen>
        <Banner text={t("complaint.cached")} />
        {saved.length === 0 ? <Empty text={t("receipt.none")} /> : saved.map((r) => (
          <Card key={r.id} onPress={() => nav.navigate("Receipt", { id: r.id })}><Txt bold>{r.reference}</Txt><Txt numberOfLines={2}>{r.description}</Txt></Card>
        ))}
        <Button title={t("retry")} variant="secondary" onPress={list.reload} />
      </Screen>
    );
  }
  return (
    <Screen>
      <FindByReference onFound={(id) => nav.navigate("ComplaintDetail", { id })} />
      {list.data.length === 0 ? <Empty text={t("home.noComplaints")} /> : list.data.map((c) => <ComplaintCard key={c.id} c={c} onPress={() => nav.navigate("ComplaintDetail", { id: c.id })} />)}
    </Screen>
  );
}

export function ComplaintDetailScreen({ route }: NativeStackScreenProps<RootStackParams, "ComplaintDetail">) {
  const t = useT();
  const f = useDate();
  const { id } = route.params;
  const detail = useLoad(() => ok(api.GET("/complaints/{id}", { params: { path: { id } } })), [id]);
  const action = useAction();
  const [reopening, setReopening] = useState(false);
  const [reason, setReason] = useState("");
  usePolling(detail.reload); // a status change shows up without pulling to refresh (RA-05)
  const [updatedAt, setUpdatedAt] = useState(() => new Date().toISOString());
  useEffect(() => { if (detail.data) setUpdatedAt(new Date().toISOString()); }, [detail.data]);

  const move = (to: S["ComplaintStatus"], why: string | null) => action.run(async () => {
    await ok(api.POST("/complaints/{id}/transition", { params: { path: { id } }, body: { to, reason: why, mergedIntoId: null } }));
    setReopening(false);
    detail.reload();
  });

  if (!detail.data) return <Screen>{detail.error ? <Banner text={errorText(t, detail.error)} /> : <Loading />}</Screen>;
  const d = detail.data;
  return (
    <Screen>
      <Row><Txt size="xl" bold style={{ flex: 1 }}>{d.complaint.reference}</Txt><StatusChip status={d.complaint.status} /></Row>
      <Card><Txt>{d.complaint.description}</Txt>{d.complaint.statusReason && <Txt muted>{d.complaint.statusReason}</Txt>}</Card>
      <Txt size="sm" muted>{t("complaint.updated", { time: f(updatedAt) })}</Txt>

      <MissingInfoCard complaint={d.complaint} onAnswered={detail.reload} />

      {d.allowedNext.includes("Closed") && (
        <Card>
          <Button title={t("complaint.confirm")} onPress={() => void move("Closed", null)} loading={action.busy} />
          {d.allowedNext.includes("UnderReview") && !reopening && <Button title={t("complaint.reopen")} variant="secondary" onPress={() => setReopening(true)} />}
          {reopening && (
            <>
              <Field label={t("complaint.reason")} value={reason} onChangeText={setReason} multiline numberOfLines={3} />
              <Button title={t("send")} variant="danger" disabled={!reason.trim()} onPress={() => void move("UnderReview", reason.trim())} loading={action.busy} />
            </>
          )}
          {action.error && <Banner tone="danger" text={errorText(t, action.error)} />}
        </Card>
      )}

      {["Resolved", "Closed"].includes(d.complaint.status) && (
        <FeedbackCard complaintId={id} existing={d.feedback} defaultFixed={d.complaint.status === "Closed"} onSent={detail.reload} />
      )}

      <Txt size="lg" bold>{t("complaint.timeline")}</Txt>
      {d.history.map((h, i) => (
        <Card key={i}><StatusChip status={h.to} /><Txt size="sm" muted>{f(h.occurredAt)}</Txt>{h.reason && <Txt>{h.reason}</Txt>}</Card>
      ))}
    </Screen>
  );
}

/** Complaint form: words first, then optional photos, a voice note and a place. The words are sent at once; photos follow in the background (RA-02). */
export function NewComplaintScreen() {
  const p = usePalette();
  const t = useT();
  const nav = useNavigation<Nav>();
  const { receipts, drafts, queue, drain } = useServices();
  const online = useOnline();
  const voice = useVoiceRecorder();
  const send = useAction();

  const [description, setDescription] = useState("");
  const [where, setWhere] = useState("");
  const [landmarkQuery, setLandmarkQuery] = useState("");
  const [landmark, setLandmark] = useState<S["LandmarkDto"]>();
  const [position, setPosition] = useState<{ latitude: number; longitude: number }>();
  const [photos, setPhotos] = useState<Photo[]>([]);
  const [notice, setNotice] = useState<string>();
  const [restored, setRestored] = useState<Draft>();
  const [asset, setAsset] = useState("");
  const [asked, setAsked] = useState(false);
  const danger = isSafetyText(description) || isSafetyText(where);
  useEffect(() => { setAsked(false); }, [description, where]); // new words, new check

  const landmarks = useLoad(() => (landmarkQuery.trim().length >= 2 ? ok(api.GET("/landmarks", { params: { query: { q: landmarkQuery.trim() } } })).catch(() => []) : Promise.resolve([])), [landmarkQuery]);

  useEffect(() => { void drafts.get().then(setRestored); }, [drafts]);

  const restore = () => {
    if (!restored) return;
    setDescription(restored.description);
    setWhere(restored.locationText);
    if (restored.latitude != null && restored.longitude != null) setPosition({ latitude: restored.latitude, longitude: restored.longitude });
    setRestored(undefined);
  };

  async function addPhoto() {
    setNotice(undefined);
    const shot = await takePhoto();
    if ("error" in shot) { if (shot.error === "permission_denied") setNotice(t("new.cameraDenied")); return; }
    setPhotos((list) => [...list, shot.photo]);
    if (!shot.photo.fits) setNotice(t("new.photoBig"));
  }

  async function useMyLocation() {
    const fix = await currentPosition();
    if ("error" in fix) { setNotice(t("new.locationDenied")); return; }
    setPosition(fix);
    setNotice(t("new.locationSet"));
  }

  const locationText = [where.trim(), landmark?.name, position ? `(${position.latitude.toFixed(5)}, ${position.longitude.toFixed(5)})` : ""].filter(Boolean).join(" ");

  const submit = useCallback(async () => {
    const created = await send.run(async () => {
      if (!(await isOnline())) throw Object.assign(new Error("offline"), { offline: true });
      return ok(api.POST("/complaints", { body: {
        description: description.trim(), channel: null, categoryId: null, houseId: null, locationId: landmark?.locationId ?? null, locationText: locationText || null, reportedByUserId: null,
        reporterPhone: null, assetRef: asset.trim() || null, mediaOnly: description.trim() === "" && (photos.length > 0 || !!voice.note),
        latitude: position?.latitude ?? null, longitude: position?.longitude ?? null,
      } }));
    });
    if (!created) {
      // No signal (or the server could not be reached): keep the words so nothing is lost.
      await drafts.save({ description, locationText: where, landmarkId: landmark?.id, latitude: position?.latitude, longitude: position?.longitude, photoUris: photos.map((p) => p.uri), voiceUri: voice.note?.uri, savedAt: new Date().toISOString() });
      setNotice(t("new.saved"));
      return;
    }
    const files = [...photos.map((p) => ({ uri: p.uri, mimeType: p.mimeType, size: p.size })), ...(voice.note ? [{ uri: voice.note.uri, mimeType: voice.note.mimeType, size: voice.note.size }] : [])];
    for (const f of files) await queue.add({ target: { kind: "complaint", id: created.id }, ...f });
    await receipts.add({ id: created.id, reference: created.reference, description: description.trim(), createdAt: new Date().toISOString(), needsLocation: created.needsLocation, photos: files.length });
    await drafts.clear();
    void drain();
    setDescription(""); setWhere(""); setPhotos([]); setLandmark(undefined); setPosition(undefined); voice.discard();
    nav.navigate("Receipt", { id: created.id });
  }, [send, description, landmark, locationText, where, position, photos, voice, drafts, queue, receipts, drain, nav, t, asset]);

  return (
    <Screen>
      {!online && <Banner text={t("offline")} />}
      {restored && <Banner tone="info" text={t("new.draftFound")} />}
      {restored && <Button title={t("retry")} variant="secondary" onPress={restore} />}
      <Field label={t("new.describe")} hint={photos.length > 0 || voice.note ? t("new.mediaOnlyHint") : t("new.describeHint")} value={description} onChangeText={setDescription} multiline numberOfLines={5} maxLength={4000} />
      <Field label={t("new.where")} hint={t("new.whereHint")} value={where} onChangeText={setWhere} />
      <Field label={t("new.asset")} hint={t("new.assetHint")} value={asset} onChangeText={setAsset} autoCapitalize="characters" maxLength={100} />

      <Field label={t("new.landmark")} value={landmark ? landmark.name : landmarkQuery} onChangeText={(v) => { setLandmark(undefined); setLandmarkQuery(v); }} />
      {!landmark && (landmarks.data ?? []).slice(0, 5).map((l) => (
        <Pressable key={l.id} accessibilityRole="button" onPress={() => setLandmark(l)} style={{ minHeight: TOUCH, justifyContent: "center" }}><Txt color={p.link} bold>{l.name}</Txt></Pressable>
      ))}
      <Button title={t("new.useLocation")} variant="secondary" onPress={() => void useMyLocation()} />

      <Row wrap>
        <Button title={t("new.photo")} variant="secondary" onPress={() => void addPhoto()} />
        {photos.length > 0 && <Chip label={t("new.photos", { count: photos.length })} tone="info" glyph="🖼" />}
        {photos.length > 0 && <Button title={t("new.remove")} variant="ghost" onPress={() => setPhotos((l) => l.slice(0, -1))} />}
      </Row>

      <View style={{ gap: space[2] }}>
        {voice.recording
          ? <Button title={`${t("new.stop")} · ${t("new.recording", { seconds: voice.seconds })}`} variant="danger" onPress={() => void voice.stop()} />
          : <Button title={voice.note ? t("new.voiceSaved", { seconds: voice.seconds }) : t("new.voice")} variant="secondary" onPress={() => void voice.start()} />}
        {voice.denied && <Banner text={t("new.voiceDenied")} />}
      </View>

      {notice && <Banner tone="info" text={notice} />}
      {send.error && send.error.code !== "network" && <Banner tone="danger" text={errorText(t, send.error)} />}
      {danger && asked && <EmergencyPrompt onSend={() => void submit()} busy={send.busy} />}
      {!(danger && asked) && (
        <Button title={t("new.submit")} onPress={() => { if (danger) setAsked(true); else void submit(); }} loading={send.busy} disabled={description.trim().length === 0 && photos.length === 0 && !voice.note} />
      )}
    </Screen>
  );
}

/** Shown immediately after sending, and kept on the phone for reading offline (RA-03). */
export function ReceiptScreen({ route }: NativeStackScreenProps<RootStackParams, "Receipt">) {
  const t = useT();
  const f = useDate();
  const nav = useNavigation<Nav>();
  const { receipts, waiting } = useServices();
  const [receipt, setReceipt] = useState<Receipt>();
  useEffect(() => { void receipts.get(route.params.id).then(setReceipt); }, [receipts, route.params.id]);
  if (!receipt) return <Screen><Loading /></Screen>;
  return (
    <Screen>
      <Card>
        <Txt size="lg" bold>{t("receipt.title")}</Txt>
        <Txt muted>{t("receipt.number")}</Txt>
        <Txt size="xxl" bold style={{ writingDirection: "ltr", textAlign: "center", letterSpacing: 1 }}>{receipt.reference}</Txt>
        <Txt size="sm" muted>{f(receipt.createdAt)}</Txt>
        <Txt>{receipt.description}</Txt>
      </Card>
      <Banner tone="success" text={t("receipt.keep")} />
      {receipt.needsLocation && <Banner text={t("receipt.needsLocation")} />}
      {receipt.photos > 0 && waiting > 0 && <Banner tone="info" text={t("receipt.photos", { count: waiting })} />}
      <Button title={t("home.mine")} onPress={() => nav.navigate("ComplaintDetail", { id: receipt.id })} />
      <Button title={t("done")} variant="secondary" onPress={() => nav.navigate("Tabs", { screen: "home" } as never)} />
    </Screen>
  );
}

export function ReceiptsScreen() {
  const t = useT();
  const nav = useNavigation<Nav>();
  const { receipts } = useServices();
  const [list, setList] = useState<Receipt[]>();
  useEffect(() => { void receipts.list().then(setList); }, [receipts]);
  if (!list) return <Screen><Loading /></Screen>;
  return (
    <Screen>
      {list.length === 0 ? <Empty text={t("receipt.none")} /> : list.map((r) => (
        <Card key={r.id} onPress={() => nav.navigate("Receipt", { id: r.id })}><Txt bold style={{ writingDirection: "ltr" }}>{r.reference}</Txt><Txt numberOfLines={2}>{r.description}</Txt></Card>
      ))}
    </Screen>
  );
}
