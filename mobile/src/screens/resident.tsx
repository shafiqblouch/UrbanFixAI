import type { NativeStackScreenProps } from "@react-navigation/native-stack";
import { useFocusEffect, useNavigation } from "@react-navigation/native";
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Animated, AppState, Image, Modal, Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { setAudioModeAsync, useAudioPlayer, useAudioPlayerStatus } from "expo-audio";
import { api, ok, type S } from "../api";
import { errorText, useAction, useLoad, usePolling, useRefetchOnFocus } from "../hooks";
import { useI18n, useT } from "../i18n";
import type { Key } from "../i18n/strings";
import type { Draft, Receipt } from "../offline/store";
import { isOnline, useOnline } from "../platform/device";
import { fileExists } from "../platform/files";
import { takePhoto, type Photo } from "../platform/photo";
import { useVoiceRecorder, type VoiceNote } from "../platform/voice";
import { isSafetyText } from "../safety";
import { currentPosition } from "../platform/device";
import { useServices } from "../services";
import { useSession } from "../session";
import { fontSize, radius, space, timelineColors, TOUCH, usePalette } from "../theme";
import { Banner, Button, Card, Chip, Empty, Field, Gradient, Loading, Row, Screen, Txt } from "../ui";
import type { RootStackParams } from "../navigation";
import { EmergencyPrompt, FeedbackCard, FindByReference, MissingInfoCard } from "./residentExtras";
import { useUnreadNotices } from "./notices";

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

/** Complaint statuses that still need the resident's or the team's attention. */
const ACTIVE_STATUSES: S["ComplaintStatus"][] = ["New", "OnHold", "UnderReview", "Assigned", "InProgress"];

/** Resident home: a brand hero, a one-tap report call-to-action and recent complaints (RA-01). */
export function HomeScreen() {
  const t = useT();
  const nav = useNavigation<Nav>();
  const insets = useSafeAreaInsets();
  const { me, view } = useSession();
  const { waiting } = useServices();
  const online = useOnline();
  const unread = useUnreadNotices();
  const recent = useLoad(() => ok(api.GET("/complaints", { params: { query: { limit: 50 } } })), []);
  // The visitor-passes entry only appears where the society has gate security switched on (best-effort: hidden if unknown).
  const features = useLoad(() => ok(api.GET("/features", {})).catch(() => ({}) as Record<string, boolean>), []);
  const gateOn = features.data?.gate === true;
  useRefetchOnFocus(recent.reload); // refresh the recent list on return instead of only at login
  const p = usePalette();

  const all = recent.data ?? [];
  const activeCount = all.filter((c) => ACTIVE_STATUSES.includes(c.status)).length;
  const portalLabel = [view?.organizationName, t("home.portal")].filter(Boolean).join(" • ");
  const goReport = () => nav.navigate("Tabs", { screen: "new" } as never);

  return (
    <View style={{ flex: 1, backgroundColor: p.bg }}>
      {/* Gradient hero with the resident's greeting and a bell to the notices. */}
      <Gradient style={{ paddingTop: insets.top + space[3], paddingBottom: space[5], paddingHorizontal: space[4], gap: space[3] }}>
        <Row style={{ justifyContent: "space-between" }}>
          <View style={{ backgroundColor: "rgba(255,255,255,0.18)", borderRadius: radius.pill, paddingHorizontal: space[3], paddingVertical: space[1] }}>
            <Txt size="sm" bold color="#FFFFFF">{portalLabel}</Txt>
          </View>
          <Pressable accessibilityRole="button" accessibilityLabel={t("tab.notices")} onPress={() => nav.navigate("Tabs", { screen: "notices" } as never)} style={{ width: 40, height: 40, borderRadius: 20, backgroundColor: "rgba(255,255,255,0.18)", alignItems: "center", justifyContent: "center" }}>
            <Txt color="#FFFFFF" style={{ fontSize: 18 }}>🔔</Txt>
            {unread > 0 && <View style={{ position: "absolute", top: 8, right: 9, width: 10, height: 10, borderRadius: 5, backgroundColor: p.danger, borderWidth: 1.5, borderColor: "#FFFFFF" }} />}
          </Pressable>
        </Row>
        <Txt size="xxl" bold color="#FFFFFF">{t("home.greeting", { name: me?.displayName ?? "" })}</Txt>
      </Gradient>

      <ScrollView keyboardShouldPersistTaps="handled" contentContainerStyle={{ padding: space[4], gap: space[4], paddingBottom: space[6] }}>
        {!online && <Banner text={t("offline")} />}
        {waiting > 0 && <Banner tone="info" text={t("offline.queued", { count: waiting })} />}

        {/* The 10-second report call-to-action. */}
        <Pressable accessibilityRole="button" accessibilityLabel={t("home.reportTitle")} onPress={goReport}>
          <Gradient style={{ borderRadius: radius.lg, padding: space[4] }}>
            <Row gap={space[3]} style={{ alignItems: "center" }}>
              <View style={{ flex: 1, gap: space[2] }}>
                <View style={{ alignSelf: "flex-start", flexDirection: "row", alignItems: "center", gap: 4, backgroundColor: "rgba(0,0,0,0.18)", borderRadius: radius.pill, paddingHorizontal: space[2], paddingVertical: 2 }}>
                  <Txt size="sm" color="#FFFFFF">⚡</Txt>
                  <Txt size="sm" bold color="#FFFFFF" style={{ letterSpacing: 0.5 }}>{t("home.reportBadge").toUpperCase()}</Txt>
                </View>
                <Txt size="xl" bold color="#FFFFFF">{t("home.reportTitle")}</Txt>
                <Txt color="#EAF6FF">{t("home.reportSubtitle")}</Txt>
                <Row gap={space[2]}>
                  <HeroChip glyph="📷" label={t("home.photo")} />
                  <HeroChip glyph="🎤" label={t("home.voice")} />
                </Row>
              </View>
              <View style={{ width: 56, height: 56, borderRadius: 28, backgroundColor: "#FFFFFF", alignItems: "center", justifyContent: "center" }}>
                <Txt size="xxl" bold color={p.primary}>＋</Txt>
              </View>
            </Row>
          </Gradient>
        </Pressable>

        {/* Quick links: the two complaint shortcuts stay a balanced two-up row. */}
        <Row gap={space[3]} style={{ alignItems: "stretch" }}>
          <ActionTile glyph="📋" title={t("home.mine")} subtitle={t("home.trackStatus")} badge={activeCount > 0 ? t("home.active", { count: activeCount }) : undefined} onPress={() => nav.navigate("Tabs", { screen: "complaints" } as never)} />
          <ActionTile glyph="🧾" title={t("home.receipts")} subtitle={t("home.receiptsSub")} note="PDF" onPress={() => nav.navigate("Receipts")} />
        </Row>

        {/* Visitor passes get their own full-width row so they don't squeeze the complaint tiles, and only where gate is on. */}
        {gateOn && (
          <WideTile glyph="🚪" title={t("guest.title")} subtitle={t("guest.homeSub")} onPress={() => nav.navigate("GatePasses")} />
        )}

        {/* Recent complaints. */}
        <Row style={{ justifyContent: "space-between" }}>
          <Txt size="lg" bold>{t("home.recent")}</Txt>
          <Pressable accessibilityRole="button" onPress={() => nav.navigate("Tabs", { screen: "complaints" } as never)} style={{ minHeight: TOUCH, justifyContent: "center" }}>
            <Txt bold color={p.link}>{t("home.viewAll")} ›</Txt>
          </Pressable>
        </Row>
        {recent.loading && !recent.data ? <Loading /> : all.length > 0
          ? all.slice(0, 3).map((c) => <ComplaintCard key={c.id} c={c} onPress={() => nav.navigate("ComplaintDetail", { id: c.id })} />)
          : <Empty text={t("home.noComplaints")} />}
      </ScrollView>
    </View>
  );
}

/** A translucent pill with an icon used inside the gradient report card. */
function HeroChip({ glyph, label }: { glyph: string; label: string }) {
  return (
    <View style={{ flexDirection: "row", alignItems: "center", gap: 4, backgroundColor: "rgba(255,255,255,0.2)", borderRadius: radius.pill, paddingHorizontal: space[3], paddingVertical: space[1] }}>
      <Txt size="sm" color="#FFFFFF">{glyph}</Txt>
      <Txt size="sm" bold color="#FFFFFF">{label}</Txt>
    </View>
  );
}

/** One of the two quick-link tiles under the report card. */
function ActionTile({ glyph, title, subtitle, badge, note, onPress }: { glyph: string; title: string; subtitle: string; badge?: string; note?: string; onPress: () => void }) {
  const p = usePalette();
  return (
    <Pressable
      accessibilityRole="button" accessibilityLabel={title} onPress={onPress}
      style={({ pressed }) => ({ flex: 1, gap: space[2], backgroundColor: p.surfaceRaised, borderColor: p.borderSoft, borderWidth: 1, borderRadius: radius.lg, padding: space[4], opacity: pressed ? 0.9 : 1 })}
    >
      <Row style={{ justifyContent: "space-between" }}>
        <View style={{ width: 40, height: 40, borderRadius: 12, backgroundColor: p.primarySoft, alignItems: "center", justifyContent: "center" }}>
          <Txt style={{ fontSize: 20 }}>{glyph}</Txt>
        </View>
        {badge
          ? <View style={{ backgroundColor: p.successBg, borderRadius: radius.pill, paddingHorizontal: space[2], paddingVertical: 2 }}><Txt size="sm" bold color={p.success}>{badge}</Txt></View>
          : note ? <Txt size="sm" muted bold>{note}</Txt> : null}
      </Row>
      <Txt bold>{title}</Txt>
      <Txt size="sm" muted>{subtitle}</Txt>
    </Pressable>
  );
}

/** A full-width quick link (icon left, text, chevron). Used for entries that should not share the two-up tile row. */
function WideTile({ glyph, title, subtitle, onPress }: { glyph: string; title: string; subtitle: string; onPress: () => void }) {
  const p = usePalette();
  const { rtl } = useI18n();
  return (
    <Pressable
      accessibilityRole="button" accessibilityLabel={title} onPress={onPress}
      style={({ pressed }) => ({ flexDirection: rtl ? "row-reverse" : "row", alignItems: "center", gap: space[3], backgroundColor: p.surfaceRaised, borderColor: p.borderSoft, borderWidth: 1, borderRadius: radius.lg, padding: space[4], opacity: pressed ? 0.9 : 1 })}
    >
      <View style={{ width: 44, height: 44, borderRadius: 12, backgroundColor: p.primarySoft, alignItems: "center", justifyContent: "center" }}>
        <Txt style={{ fontSize: 22 }}>{glyph}</Txt>
      </View>
      <View style={{ flex: 1, gap: 2 }}>
        <Txt bold>{title}</Txt>
        <Txt size="sm" muted>{subtitle}</Txt>
      </View>
      <Txt size="lg" muted>{rtl ? "‹" : "›"}</Txt>
    </Pressable>
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
  useRefetchOnFocus(list.reload); // the resident's complaint list stays current on return
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

      <MediaCard media={d.media} />

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
      <Timeline history={d.history} status={d.complaint.status} />
    </Screen>
  );
}

type NodeKind = "done" | "active" | "danger";

/** Split a timestamp into a short date ("06 Oct 2024") and a 12-hour time ("1:49 pm"), in the active language. */
function useTimelineFormat() {
  const { locale } = useI18n();
  const l = locale === "ur" ? "ur-PK-u-nu-latn" : "en-GB";
  return (iso: string) => {
    const d = new Date(iso);
    const date = new Intl.DateTimeFormat(l, { day: "2-digit", month: "short", year: "numeric" }).format(d);
    const time = new Intl.DateTimeFormat(l, { hour: "numeric", minute: "2-digit", hour12: true }).format(d).toLowerCase();
    return { date, time };
  };
}

/** The circular step marker: a white tick, clock or cross on a vivid disc — colour plus a clear icon. */
function TimelineDot({ kind }: { kind: NodeKind }) {
  const c = timelineColors[kind];
  return (
    <View style={[styles.dot, { backgroundColor: c.ring }]}>
      {kind === "active"
        ? ( // a small clock drawn from Views so it stays crisp white with no icon font
          <View style={styles.clockFace}>
            <View style={styles.clockHandHour} />
            <View style={styles.clockHandMinute} />
          </View>
        )
        : <RNTextIcon glyph={kind === "danger" ? "✕" : "✓"} color={c.icon} />}
    </View>
  );
}

function RNTextIcon({ glyph, color }: { glyph: string; color: string }) {
  return <Txt bold color={color} size="lg" style={{ lineHeight: 22 }}>{glyph}</Txt>;
}

/** A Dastak-style progress timeline: dated steps down a connected rail, each a tappable card that opens its remark. */
function Timeline({ history, status }: { history: S["HistoryDto"][]; status: S["ComplaintStatus"] }) {
  if (history.length === 0) return null;
  const terminal = status === "Resolved" || status === "Closed";
  return (
    <View>
      {history.map((h, i) => {
        const last = i === history.length - 1;
        const kind: NodeKind = h.to === "Rejected" ? "danger" : last && !terminal ? "active" : "done";
        return <TimelineRow key={i} entry={h} kind={kind} last={last} />;
      })}
    </View>
  );
}

function TimelineRow({ entry, kind, last }: { entry: S["HistoryDto"]; kind: NodeKind; last: boolean }) {
  const t = useT();
  const p = usePalette();
  const fmt = useTimelineFormat();
  const [open, setOpen] = useState(false);
  const { date, time } = fmt(entry.occurredAt);
  const hasRemark = !!entry.reason?.trim();
  const line = timelineColors[kind].line;
  return (
    <Row gap={space[3]} style={{ alignItems: "stretch" }}>
      <View style={{ width: 58, paddingTop: 10 }}>
        <Txt size="sm" muted>{date}</Txt>
      </View>
      <View style={{ width: 40, alignItems: "center" }}>
        <TimelineDot kind={kind} />
        {!last && <View style={{ width: 3, flexGrow: 1, minHeight: 16, backgroundColor: line, borderRadius: 2, marginTop: 2 }} />}
      </View>
      <View style={{ flex: 1, paddingBottom: space[4] }}>
        <Pressable
          accessibilityRole={hasRemark ? "button" : undefined}
          onPress={hasRemark ? () => setOpen((v) => !v) : undefined}
          style={{ backgroundColor: p.surface, borderRadius: radius.lg, borderWidth: 1, borderColor: p.borderSoft, padding: space[4], gap: space[1] }}
        >
          <Row gap={space[2]}>
            <Txt bold style={{ flex: 1 }}>{t(`status.${entry.to}` as Key)}</Txt>
            {hasRemark && <Txt size="lg" muted>{open ? "▴" : "▾"}</Txt>}
          </Row>
          <Txt size="sm" muted>{time}</Txt>
          {open && hasRemark && (
            <View style={{ gap: space[2], marginTop: space[2] }}>
              <View style={{ height: 1, backgroundColor: p.borderSoft }} />
              <Txt bold>{t("complaint.remarks")}</Txt>
              <Txt muted>{entry.reason}</Txt>
            </View>
          )}
        </Pressable>
      </View>
    </Row>
  );
}

const styles = StyleSheet.create({
  dot: { width: 40, height: 40, borderRadius: 20, alignItems: "center", justifyContent: "center", shadowColor: "#102820", shadowOpacity: 0.18, shadowRadius: 4, shadowOffset: { width: 0, height: 2 }, elevation: 3 },
  clockFace: { width: 20, height: 20, borderRadius: 10, borderWidth: 2.5, borderColor: "#FFFFFF" },
  clockHandHour: { position: "absolute", left: 6.25, top: 1.5, width: 2, height: 6, borderRadius: 1, backgroundColor: "#FFFFFF" }, // points up to 12
  clockHandMinute: { position: "absolute", left: 7, top: 6.25, width: 6, height: 2, borderRadius: 1, backgroundColor: "#FFFFFF" }, // points right to 3
});

/** A playable voice note, with the text the AI heard when it has been transcribed (AI-09).
 * Matches the themed player used on the report form: a round play button, a waveform and the length. */
function VoiceNote({ media }: { media: S["MediaDto"] }) {
  const t = useT();
  const p = usePalette();
  const player = useAudioPlayer(media.url);
  const status = useAudioPlayerStatus(player);
  const toggle = async () => {
    if (status.playing) { player.pause(); return; }
    await setAudioModeAsync({ playsInSilentMode: true }); // plays even when the ringer is on silent
    if (status.duration > 0 && status.currentTime >= status.duration - 0.05) await player.seekTo(0); // replay once it has finished
    player.play();
  };
  const seconds = Math.max(0, Math.round(status.duration || 0));
  return (
    <View style={{ gap: space[2] }}>
      <AttachmentRow>
        <Row gap={space[3]}>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={status.playing ? t("complaint.voicePause") : t("complaint.voicePlay")}
            onPress={() => void toggle()}
            style={{ width: 44, height: 44, borderRadius: 22, backgroundColor: p.primarySoft, alignItems: "center", justifyContent: "center" }}
          >
            <Txt color={p.onPrimarySoft} style={{ fontSize: 16 }}>{status.playing ? "⏸" : "▶"}</Txt>
          </Pressable>
          <View style={{ flex: 1, gap: space[1] }}>
            <Txt bold>{t("complaint.voiceNote")}</Txt>
            <Waveform color={status.playing ? p.primary : "#AEC6F2"} />
          </View>
          {seconds > 0 && (
            <View style={{ backgroundColor: p.infoBg, borderRadius: radius.pill, paddingHorizontal: space[2], paddingVertical: 2 }}>
              <Txt size="sm" bold color={p.info} style={{ writingDirection: "ltr" }}>{formatDuration(seconds)}</Txt>
            </View>
          )}
        </Row>
      </AttachmentRow>
      {media.transcript && media.transcriptStatus === "done" && <Txt muted>{t("complaint.voiceHeard", { text: media.transcript })}</Txt>}
    </View>
  );
}

/** The resident's own photos and voice note on a complaint, so they can see and hear what they sent (RA-03). */
function MediaCard({ media }: { media: S["MediaDto"][] }) {
  const t = useT();
  const [viewing, setViewing] = useState<string>();
  const images = media.filter((m) => m.kind === "image");
  const audio = media.filter((m) => m.kind === "audio");
  if (images.length === 0 && audio.length === 0) return null;
  return (
    <Card>
      <Txt bold>{t("complaint.evidence")}</Txt>
      {audio.map((m) => <VoiceNote key={m.id} media={m} />)}
      {images.length > 0 && (
        <Row wrap gap={space[2]}>
          {images.map((m) => (
            <Pressable key={m.id} accessibilityRole="imagebutton" accessibilityLabel={t("complaint.viewPhoto")} onPress={() => setViewing(m.url)}>
              <Image accessibilityIgnoresInvertColors source={{ uri: m.thumbUrl ?? m.url }} style={{ width: 96, height: 96, borderRadius: 8 }} />
            </Pressable>
          ))}
        </Row>
      )}
      <ImageViewer uri={viewing} onClose={() => setViewing(undefined)} />
    </Card>
  );
}

/** A full-screen, tap-to-dismiss photo viewer. Thumbnails across the app open the full image here. */
export function ImageViewer({ uri, onClose }: { uri?: string; onClose: () => void }) {
  const t = useT();
  const insets = useSafeAreaInsets();
  return (
    <Modal visible={!!uri} transparent animationType="fade" statusBarTranslucent onRequestClose={onClose}>
      <Pressable accessibilityRole="button" accessibilityLabel={t("complaint.closePhoto")} onPress={onClose} style={{ flex: 1, backgroundColor: "rgba(0,0,0,0.94)", alignItems: "center", justifyContent: "center" }}>
        {uri && <Image accessibilityIgnoresInvertColors source={{ uri }} resizeMode="contain" style={{ width: "100%", height: "100%" }} />}
        <Pressable
          accessibilityRole="button" accessibilityLabel={t("complaint.closePhoto")} onPress={onClose}
          style={{ position: "absolute", top: insets.top + space[2], right: space[4], width: 44, height: 44, borderRadius: 22, backgroundColor: "rgba(255,255,255,0.16)", alignItems: "center", justifyContent: "center" }}
        >
          <Txt color="#FFFFFF" style={{ fontSize: 20 }}>✕</Txt>
        </Pressable>
      </Pressable>
    </Modal>
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
  const insets = useSafeAreaInsets();
  const { rtl, locale } = useI18n();
  const toast = useToast();

  const [description, setDescription] = useState("");
  const [where, setWhere] = useState("");
  const [landmarkQuery, setLandmarkQuery] = useState("");
  const [landmark, setLandmark] = useState<S["LandmarkDto"]>();
  const [position, setPosition] = useState<{ latitude: number; longitude: number }>();
  const [photos, setPhotos] = useState<Photo[]>([]);
  const [voiceNotes, setVoiceNotes] = useState<VoiceNote[]>([]);
  const [notice, setNotice] = useState<string>();
  const [restored, setRestored] = useState<Draft>();
  const [asset, setAsset] = useState("");
  const [asked, setAsked] = useState(false);
  const [categoryId, setCategoryId] = useState<string | null>(null);
  const [detailsOpen, setDetailsOpen] = useState(true);
  const danger = isSafetyText(description) || isSafetyText(where);
  useEffect(() => { setAsked(false); }, [description, where]); // new words, new check

  // The quick-category chips (RA-02): the active categories, shown as one-tap pills. Best-effort —
  // if they cannot be fetched (e.g. offline), the chips are simply hidden and the complaint still sends.
  const categories = useLoad(() => ok(api.GET("/categories")).catch(() => [] as S["CategoryDto"][]), []);
  const landmarks = useLoad(() => (landmarkQuery.trim().length >= 2 ? ok(api.GET("/landmarks", { params: { query: { q: landmarkQuery.trim() } } })).catch(() => []) : Promise.resolve([])), [landmarkQuery]);

  // Each finished recording is added to the list so the resident can keep several, then cleared from
  // the recorder so the next one starts fresh.
  useEffect(() => {
    if (!voice.note) return;
    toast.show(t("new.voiceAdded", { number: voiceNotes.length + 1 }));
    setVoiceNotes((list) => [...list, voice.note!]);
    voice.discard();
  }, [voice, voiceNotes.length, toast, t]);

  const restore = () => {
    if (!restored) return;
    setDescription(restored.description);
    setWhere(restored.locationText);
    if (restored.latitude != null && restored.longitude != null) setPosition({ latitude: restored.latitude, longitude: restored.longitude });
    // Rebuild the attachments from the saved files, dropping any the OS has purged from the cache
    // while the app was closed. Width/height are only used when first taking a photo, so placeholders
    // are fine here; the uri, size and type are what the upload needs.
    const savedPhotos = (restored.photos ?? []).filter((ph) => fileExists(ph.uri));
    const savedVoices = (restored.voiceNotes ?? []).filter((v) => fileExists(v.uri));
    const dropped = (restored.photos?.length ?? 0) - savedPhotos.length + (restored.voiceNotes?.length ?? 0) - savedVoices.length;
    setPhotos(savedPhotos.map((ph) => ({ uri: ph.uri, size: ph.size, mimeType: "image/jpeg" as const, width: 0, height: 0, fits: true })));
    setVoiceNotes(savedVoices.map((v) => ({ uri: v.uri, size: v.size, seconds: v.seconds, mimeType: "audio/mp4" as const })));
    if (dropped > 0) setNotice(t("new.mediaMissing"));
    setRestored(undefined);
  };

  async function addPhoto() {
    setNotice(undefined);
    const shot = await takePhoto();
    if ("error" in shot) { if (shot.error === "permission_denied") setNotice(t("new.cameraDenied")); return; }
    setPhotos((list) => [...list, shot.photo]);
    toast.show(t("new.photoAdded", { number: photos.length + 1 }));
    if (!shot.photo.fits) setNotice(t("new.photoBig"));
  }

  async function useMyLocation() {
    const fix = await currentPosition();
    if ("error" in fix) { setNotice(t("new.locationDenied")); return; }
    setPosition(fix);
    setNotice(t("new.locationSet"));
  }

  const locationText = [where.trim(), landmark?.name, position ? `(${position.latitude.toFixed(5)}, ${position.longitude.toFixed(5)})` : ""].filter(Boolean).join(" ");

  // The latest form values, kept in a ref so the auto-save on leaving always sees current data, and a
  // flag set after a successful send so leaving the just-cleared screen does not re-create a draft.
  const formRef = useRef({ description, where, landmark, position, photos, voiceNotes });
  formRef.current = { description, where, landmark, position, photos, voiceNotes };
  const submittedRef = useRef(false);

  const persistDraft = useCallback(() => {
    if (submittedRef.current) return; // just submitted: the draft was cleared on purpose
    const f = formRef.current;
    if (f.description.trim() === "" && f.where.trim() === "" && f.photos.length === 0 && f.voiceNotes.length === 0) return;
    void drafts.save({
      description: f.description, locationText: f.where, landmarkId: f.landmark?.id, latitude: f.position?.latitude, longitude: f.position?.longitude,
      photos: f.photos.map((ph) => ({ uri: ph.uri, size: ph.size })), voiceNotes: f.voiceNotes.map((v) => ({ uri: v.uri, size: v.size, seconds: v.seconds })),
      savedAt: new Date().toISOString(),
    });
  }, [drafts]);

  // Save a draft whenever the resident leaves this tab, and reload the latest draft when they return,
  // so an in-progress complaint survives a tab switch, pressing Home, or the app being closed.
  useFocusEffect(useCallback(() => {
    submittedRef.current = false;
    void drafts.get().then(setRestored);
    return () => persistDraft();
  }, [drafts, persistDraft]));

  // Backgrounding the app (the device Home button) also saves, in case the OS then closes the app.
  useEffect(() => {
    const sub = AppState.addEventListener("change", (s) => { if (s !== "active") persistDraft(); });
    return () => sub.remove();
  }, [persistDraft]);

  const submit = useCallback(async () => {
    // If a recording is still running when Send is pressed, stop it first and include it, so a note
    // the resident never explicitly stopped is not lost.
    const pending = voice.recording ? await voice.stop() : undefined;
    const notes = pending ? [...voiceNotes, pending] : voiceNotes;
    const created = await send.run(async () => {
      if (!(await isOnline())) throw Object.assign(new Error("offline"), { offline: true });
      return ok(api.POST("/complaints", { body: {
        description: description.trim(), channel: null, categoryId, houseId: null, locationId: landmark?.locationId ?? null, locationText: locationText || null, reportedByUserId: null,
        reporterPhone: null, assetRef: asset.trim() || null, mediaOnly: description.trim() === "" && (photos.length > 0 || notes.length > 0),
        latitude: position?.latitude ?? null, longitude: position?.longitude ?? null,
      } }));
    });
    if (!created) {
      // No signal (or the server could not be reached): keep the words so nothing is lost.
      await drafts.save({ description, locationText: where, landmarkId: landmark?.id, latitude: position?.latitude, longitude: position?.longitude, photos: photos.map((ph) => ({ uri: ph.uri, size: ph.size })), voiceNotes: notes.map((v) => ({ uri: v.uri, size: v.size, seconds: v.seconds })), savedAt: new Date().toISOString() });
      setNotice(t("new.saved"));
      return;
    }
    const files = [...photos.map((p) => ({ uri: p.uri, mimeType: p.mimeType, size: p.size })), ...notes.map((v) => ({ uri: v.uri, mimeType: v.mimeType, size: v.size }))];
    for (const f of files) await queue.add({ target: { kind: "complaint", id: created.id }, ...f });
    await receipts.add({ id: created.id, reference: created.reference, description: description.trim(), createdAt: new Date().toISOString(), needsLocation: created.needsLocation, photos: files.length });
    await drafts.clear();
    void drain();
    submittedRef.current = true; // sent and cleared: do not auto-save a draft when this tab blurs
    setDescription(""); setWhere(""); setPhotos([]); setVoiceNotes([]); setLandmark(undefined); setPosition(undefined); setCategoryId(null); voice.discard();
    nav.navigate("Receipt", { id: created.id });
  }, [send, description, categoryId, landmark, locationText, where, position, photos, voiceNotes, voice, drafts, queue, receipts, drain, nav, t, asset]);

  // While recording, Send stays enabled: pressing it stops the recording and submits it too.
  const nothingToSend = description.trim().length === 0 && photos.length === 0 && voiceNotes.length === 0 && !voice.recording;

  const attachedCount = photos.length + voiceNotes.length;
  const cats = categories.data ?? [];

  return (
    <View style={{ flex: 1, backgroundColor: p.bg }}>
      {/* Gradient hero — this screen hides the stock navigation header and draws its own. */}
      <Gradient style={{ paddingTop: insets.top + space[3], paddingBottom: space[4], paddingHorizontal: space[4] }}>
        <Row gap={space[3]} style={{ alignItems: "flex-start" }}>
          <View style={{ flex: 1, gap: 2 }}>
            <Txt size="xxl" bold color="#FFFFFF">{t("new.heroTitle")}</Txt>
            <Txt color="#E7F3FF">{t("new.heroSubtitle")}</Txt>
          </View>
          <Pressable
            accessibilityRole="button" accessibilityLabel={t("new.help")} onPress={() => toast.show(t("new.help"))}
            style={{ width: 40, height: 40, borderRadius: 20, backgroundColor: "rgba(255,255,255,0.18)", alignItems: "center", justifyContent: "center" }}
          >
            <Txt size="lg" bold color="#FFFFFF">?</Txt>
          </Pressable>
        </Row>
      </Gradient>

      <ScrollView keyboardShouldPersistTaps="handled" contentContainerStyle={{ padding: space[4], gap: space[4], paddingBottom: space[6] }}>
        {!online && <Banner text={t("offline")} />}
        {restored && nothingToSend && <Banner tone="info" text={t("new.draftFound")} />}
        {restored && nothingToSend && <Button title={t("new.restore")} variant="secondary" onPress={restore} />}
        {notice && <Banner tone="info" text={notice} />}
        {send.error && send.error.code !== "network" && <Banner tone="danger" text={errorText(t, send.error)} />}

        {/* "A photo or voice note is enough" — the one thing a first-time user needs to know. */}
        <View style={{ flexDirection: rtl ? "row-reverse" : "row", alignItems: "center", gap: space[3], backgroundColor: p.successBg, borderColor: p.success, borderWidth: 1, borderRadius: radius.lg, padding: space[3] }}>
          <View style={{ width: 36, height: 36, borderRadius: 18, backgroundColor: p.success, alignItems: "center", justifyContent: "center" }}>
            <Txt bold color="#FFFFFF">⚡</Txt>
          </View>
          <Txt style={{ flex: 1 }} color={p.success}>{t("new.help")}</Txt>
        </View>

        {/* 1. Add quick evidence */}
        <Row style={{ justifyContent: "space-between" }}>
          <SectionLabel text={t("new.evidenceStep")} />
          {attachedCount > 0 && <Chip label={t("new.attachedCount", { count: attachedCount })} tone="success" />}
        </Row>
        <Row gap={space[3]} style={{ alignItems: "stretch" }}>
          <EvidenceCard glyph="📷" iconBg={p.successBg} title={t("new.takePhoto")} subtitle={t("new.takePhotoHint")} onPress={() => void addPhoto()} />
          <EvidenceCard
            glyph={voice.recording ? "⏹" : "🎤"}
            iconBg={voice.recording ? p.danger : p.infoBg}
            iconColor={voice.recording ? "#FFFFFF" : undefined}
            title={voice.recording ? t("new.recordingTitle") : t("new.recordVoice")}
            subtitle={voice.recording ? t("new.recordingHint") : t("new.recordVoiceHint")}
            active={voice.recording}
            onPress={() => (voice.recording ? void voice.stop() : void voice.start())}
          />
        </Row>
        {voice.recording && <RecordingBar seconds={voice.seconds} onDone={() => void voice.stop()} />}
        {voice.denied && <Banner text={t("new.voiceDenied")} />}

        {/* Attached evidence */}
        {attachedCount > 0 && (
          <View style={{ gap: space[2] }}>
            <Row style={{ justifyContent: "space-between" }}>
              <SectionLabel text={t("new.attachedEvidence")} />
              <Pressable accessibilityRole="button" onPress={() => { setPhotos([]); setVoiceNotes([]); }} style={{ minHeight: TOUCH, justifyContent: "center" }}>
                <Txt bold color={p.link}>{t("new.clearAll")}</Txt>
              </Pressable>
            </Row>
            {photos.map((photo, i) => (
              <PhotoRow key={photo.uri} number={i + 1} photo={photo} onRemove={() => setPhotos((list) => list.filter((x) => x.uri !== photo.uri))} />
            ))}
            {voiceNotes.map((note, i) => (
              <VoiceRow key={note.uri} number={i + 1} note={note} onRemove={() => setVoiceNotes((list) => list.filter((x) => x.uri !== note.uri))} />
            ))}
          </View>
        )}

        {/* Add details (optional) — collapsible */}
        <Card style={{ gap: space[3] }}>
          <Pressable accessibilityRole="button" accessibilityState={{ expanded: detailsOpen }} onPress={() => setDetailsOpen((v) => !v)} style={{ minHeight: TOUCH, justifyContent: "center" }}>
            <Row gap={space[2]}>
              <Txt size="lg">⚙</Txt>
              <Txt bold style={{ flex: 1 }}>{t("new.optionalDetails")}</Txt>
              <Txt size="lg" muted>{detailsOpen ? "▴" : "▾"}</Txt>
            </Row>
          </Pressable>

          {detailsOpen && (
            <View style={{ gap: space[3] }}>
              {cats.length > 0 && (
                <View style={{ gap: space[2] }}>
                  <SectionLabel text={t("new.quickCategory")} />
                  <Row wrap gap={space[2]}>
                    {cats.slice(0, 6).map((c) => (
                      <CategoryChip key={c.id} glyph={categoryGlyph(c)} label={locale === "ur" && c.nameUr ? c.nameUr : c.nameEn} selected={categoryId === c.id} onPress={() => setCategoryId((id) => (id === c.id ? null : c.id))} />
                    ))}
                    <CategoryChip glyph="•••" label={t("new.categoryOther")} selected={categoryId === null} onPress={() => setCategoryId(null)} />
                  </Row>
                </View>
              )}

              {/* Description with a live character counter */}
              <View style={{ gap: space[1] }}>
                <Row style={{ justifyContent: "space-between" }}>
                  <Txt bold>{t("new.description")}</Txt>
                  <Txt size="sm" muted>{description.length} / {MAX_DESCRIPTION}</Txt>
                </Row>
                <TextInput
                  accessibilityLabel={t("new.description")}
                  value={description}
                  onChangeText={setDescription}
                  multiline
                  numberOfLines={4}
                  maxLength={MAX_DESCRIPTION}
                  placeholder={t("new.descriptionPlaceholder")}
                  placeholderTextColor={p.textMuted}
                  style={{ minHeight: 100, borderWidth: 2, borderColor: p.border, borderRadius: radius.md, paddingHorizontal: space[3], paddingVertical: space[2], color: p.text, backgroundColor: p.surfaceRaised, fontSize: fontSize.md, textAlignVertical: "top", textAlign: rtl ? "right" : "left", writingDirection: rtl ? "rtl" : "ltr" }}
                />
              </View>

              <Field label={t("new.where")} hint={t("new.whereHint")} value={where} onChangeText={setWhere} />
              <Field label={t("new.asset")} hint={t("new.assetHint")} value={asset} onChangeText={setAsset} autoCapitalize="characters" maxLength={100} />
              <Field label={t("new.landmark")} value={landmark ? landmark.name : landmarkQuery} onChangeText={(v) => { setLandmark(undefined); setLandmarkQuery(v); }} />
              {!landmark && (landmarks.data ?? []).slice(0, 5).map((l) => (
                <Pressable key={l.id} accessibilityRole="button" onPress={() => setLandmark(l)} style={{ minHeight: TOUCH, justifyContent: "center" }}><Txt color={p.link} bold>{l.name}</Txt></Pressable>
              ))}
              <Button title={t("new.useLocation")} variant="secondary" onPress={() => void useMyLocation()} />
            </View>
          )}
        </Card>
      </ScrollView>

      {/* Sticky Send Complaint action, always reachable above the tab bar. */}
      <View style={{ paddingHorizontal: space[4], paddingTop: space[3], paddingBottom: space[3], backgroundColor: p.surfaceRaised, borderTopWidth: 1, borderTopColor: p.borderSoft }}>
        {danger && asked
          ? <EmergencyPrompt onSend={() => void submit()} busy={send.busy} />
          : <Button title={t("new.submit")} onPress={() => { if (danger) setAsked(true); else void submit(); }} loading={send.busy} disabled={nothingToSend} />}
      </View>

      <Toast message={toast.message} opacity={toast.opacity} top={insets.top + 72} />
    </View>
  );
}

/** A small uppercase-style section label, as in the mockup ("1. ADD QUICK EVIDENCE"). */
function SectionLabel({ text }: { text: string }) {
  const p = usePalette();
  return <Txt size="sm" bold color={p.textMuted} style={{ letterSpacing: 0.5 }}>{text}</Txt>;
}

/** The longest description the quick form accepts (matches the on-screen counter). */
const MAX_DESCRIPTION = 300;

/** Human-readable file size, e.g. "1.4 MB" or "640 KB". */
function formatBytes(n: number): string {
  if (n >= 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  if (n >= 1024) return `${Math.round(n / 1024)} KB`;
  return `${n} B`;
}

/** Whole seconds as m:ss, e.g. 14 → "0:14". */
function formatDuration(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = Math.max(0, Math.round(seconds)) % 60;
  return `${m}:${String(s).padStart(2, "0")}`;
}

/** A glyph for a category, picked from its key/name so the chips read at a glance. */
function categoryGlyph(c: S["CategoryDto"]): string {
  const s = `${c.key} ${c.nameEn}`.toLowerCase();
  if (/water|sewer|sewage|drain|pani/.test(s)) return "💧";
  if (/electric|power|light|bijli/.test(s)) return "⚡";
  if (/garbage|waste|trash|solid|sanitation|kachra/.test(s)) return "🗑";
  if (/road|street|footpath|pothole|sadak/.test(s)) return "🛣";
  if (/security|safety|crime|police/.test(s)) return "🛡";
  return "🏷";
}

/** Lightweight top toast ("Photo 2 added"), auto-hiding after a moment. */
function useToast() {
  const [message, setMessage] = useState<string>();
  const opacity = useRef(new Animated.Value(0)).current;
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const show = useCallback((msg: string) => {
    setMessage(msg);
    Animated.timing(opacity, { toValue: 1, duration: 150, useNativeDriver: true }).start();
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      Animated.timing(opacity, { toValue: 0, duration: 250, useNativeDriver: true }).start(({ finished }) => { if (finished) setMessage(undefined); });
    }, 1900);
  }, [opacity]);
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);
  return { message, opacity, show };
}

function Toast({ message, opacity, top }: { message?: string; opacity: Animated.Value; top: number }) {
  if (!message) return null;
  return (
    <Animated.View pointerEvents="none" style={{ position: "absolute", top, left: space[4], right: space[4], opacity, alignItems: "center" }}>
      <View accessibilityRole="alert" style={{ flexDirection: "row", alignItems: "center", gap: space[2], backgroundColor: "#1F2937", borderRadius: radius.pill, paddingHorizontal: space[4], paddingVertical: space[2], maxWidth: "100%" }}>
        <Txt bold color="#9BE8C9">ⓘ</Txt>
        <Txt color="#FFFFFF" numberOfLines={1}>{message}</Txt>
      </View>
    </Animated.View>
  );
}

/** One of the two big "quick evidence" buttons. Turns red while recording a voice note. */
function EvidenceCard({ glyph, iconBg, iconColor, title, subtitle, onPress, active }: {
  glyph: string; iconBg: string; iconColor?: string; title: string; subtitle: string; onPress: () => void; active?: boolean;
}) {
  const p = usePalette();
  return (
    <Pressable
      accessibilityRole="button" accessibilityLabel={title} onPress={onPress}
      style={({ pressed }) => ({
        flex: 1, minHeight: 132, borderRadius: radius.lg, borderWidth: 2,
        borderColor: active ? p.danger : p.borderSoft, backgroundColor: active ? p.dangerBg : p.surfaceRaised,
        padding: space[3], alignItems: "center", justifyContent: "center", gap: space[2], opacity: pressed ? 0.9 : 1,
      })}
    >
      <View style={{ width: 56, height: 56, borderRadius: 16, backgroundColor: iconBg, alignItems: "center", justifyContent: "center" }}>
        <Txt color={iconColor} style={{ fontSize: 26 }}>{glyph}</Txt>
      </View>
      <Txt bold color={active ? p.danger : undefined} style={{ textAlign: "center" }}>{title}</Txt>
      <Txt size="sm" muted style={{ textAlign: "center" }}>{subtitle}</Txt>
    </Pressable>
  );
}

/** The live recording strip under the evidence cards, with a timer and a Done button. */
function RecordingBar({ seconds, onDone }: { seconds: number; onDone: () => void }) {
  const p = usePalette();
  const t = useT();
  const { rtl } = useI18n();
  return (
    <View style={{ flexDirection: rtl ? "row-reverse" : "row", alignItems: "center", gap: space[3], backgroundColor: p.dangerBg, borderColor: p.danger, borderWidth: 1, borderRadius: radius.lg, padding: space[3] }}>
      <View style={{ width: 12, height: 12, borderRadius: 6, backgroundColor: p.danger }} />
      <View style={{ flex: 1 }}>
        <Txt bold color={p.danger}>{t("new.recordingBar")}</Txt>
        <Txt size="sm" color={p.danger} style={{ writingDirection: "ltr" }}>{formatDuration(seconds)}</Txt>
      </View>
      <Pressable accessibilityRole="button" accessibilityLabel={t("done")} onPress={onDone} style={{ flexDirection: "row", alignItems: "center", gap: space[2], backgroundColor: p.danger, borderRadius: radius.md, paddingHorizontal: space[4], minHeight: TOUCH, justifyContent: "center" }}>
        <Txt color="#FFFFFF" style={{ fontSize: 12 }}>■</Txt>
        <Txt bold color="#FFFFFF">{t("done")}</Txt>
      </Pressable>
    </View>
  );
}

/** The card wrapper shared by attached photo and voice-note rows. */
function AttachmentRow({ children }: { children: ReactNode }) {
  const p = usePalette();
  return <View style={{ backgroundColor: p.surfaceRaised, borderColor: p.borderSoft, borderWidth: 1, borderRadius: radius.lg, padding: space[3] }}>{children}</View>;
}

/** A round icon button (used for removing an attachment). */
function TrashButton({ onRemove, label }: { onRemove: () => void; label: string }) {
  const p = usePalette();
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={onRemove} style={{ width: 40, height: 40, borderRadius: 20, alignItems: "center", justifyContent: "center" }}>
      <Txt style={{ fontSize: 18 }} color={p.textMuted}>🗑</Txt>
    </Pressable>
  );
}

/** One attached photo, with a thumbnail, size and a delete button. */
function PhotoRow({ number, photo, onRemove }: { number: number; photo: Photo; onRemove: () => void }) {
  const t = useT();
  const label = t("new.photoItem", { number });
  return (
    <AttachmentRow>
      <Row gap={space[3]}>
        <Image accessibilityIgnoresInvertColors source={{ uri: photo.uri }} style={{ width: 56, height: 56, borderRadius: radius.md }} />
        <View style={{ flex: 1, gap: 2 }}>
          <Txt bold>{label}</Txt>
          <Txt size="sm" muted>{t("new.capturedNow")} • {formatBytes(photo.size)}</Txt>
        </View>
        <TrashButton onRemove={onRemove} label={`${t("new.remove")}: ${label}`} />
      </Row>
    </AttachmentRow>
  );
}

/** A faux audio waveform drawn from thin bars, so a voice note looks the part. */
function Waveform({ color }: { color: string }) {
  const bars = useMemo(() => Array.from({ length: 24 }, (_, i) => 4 + Math.round((Math.sin(i * 1.7) * 0.5 + 0.5) * 14)), []);
  return (
    <View style={{ flexDirection: "row", alignItems: "center", gap: 3, height: 18 }}>
      {bars.map((h, i) => <View key={i} style={{ width: 3, height: h, borderRadius: 2, backgroundColor: color }} />)}
    </View>
  );
}

/** One recorded voice note the resident can play back before sending, then remove. */
function VoiceRow({ number, note, onRemove }: { number: number; note: VoiceNote; onRemove: () => void }) {
  const t = useT();
  const p = usePalette();
  const player = useAudioPlayer(note.uri);
  const status = useAudioPlayerStatus(player);
  const label = t("new.voiceNote", { number });
  const toggle = async () => {
    if (status.playing) { player.pause(); return; }
    await setAudioModeAsync({ playsInSilentMode: true }); // plays even when the ringer is on silent
    if (status.duration > 0 && status.currentTime >= status.duration - 0.05) await player.seekTo(0); // replay once finished
    player.play();
  };
  return (
    <AttachmentRow>
      <Row gap={space[3]}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={`${status.playing ? t("complaint.voicePause") : t("complaint.voicePlay")}: ${label}`}
          onPress={() => void toggle()}
          style={{ width: 44, height: 44, borderRadius: 22, backgroundColor: p.primarySoft, alignItems: "center", justifyContent: "center" }}
        >
          <Txt color={p.onPrimarySoft} style={{ fontSize: 16 }}>{status.playing ? "⏸" : "▶"}</Txt>
        </Pressable>
        <View style={{ flex: 1, gap: space[1] }}>
          <Txt bold>{label}</Txt>
          <Waveform color={status.playing ? p.primary : "#AEC6F2"} />
        </View>
        <View style={{ backgroundColor: p.infoBg, borderRadius: radius.pill, paddingHorizontal: space[2], paddingVertical: 2 }}>
          <Txt size="sm" bold color={p.info} style={{ writingDirection: "ltr" }}>{formatDuration(note.seconds)}</Txt>
        </View>
        <TrashButton onRemove={onRemove} label={`${t("new.remove")}: ${label}`} />
      </Row>
    </AttachmentRow>
  );
}

/** A one-tap category pill with an icon, as in the mockup's "Quick category" row. */
function CategoryChip({ glyph, label, selected, onPress }: { glyph: string; label: string; selected: boolean; onPress: () => void }) {
  const p = usePalette();
  const { rtl } = useI18n();
  return (
    <Pressable
      accessibilityRole="button" accessibilityState={{ selected }} accessibilityLabel={label} onPress={onPress}
      style={{ flexDirection: rtl ? "row-reverse" : "row", alignItems: "center", gap: space[1], minHeight: 40, paddingHorizontal: space[3], borderRadius: radius.pill, borderWidth: 1.5, borderColor: selected ? p.primary : p.borderSoft, backgroundColor: selected ? p.primarySoft : p.surfaceRaised }}
    >
      <Txt style={{ fontSize: 14 }}>{glyph}</Txt>
      <Txt size="sm" bold color={selected ? p.onPrimarySoft : undefined}>{label}</Txt>
    </Pressable>
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
