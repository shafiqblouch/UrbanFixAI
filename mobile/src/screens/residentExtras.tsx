// Pieces of the resident screens: finding a complaint by number (RA-05), feedback on finished work (RA-06),
// answering a request for more information (RA-07) and the emergency prompt (RA-08).
import { useState } from "react";
import { Linking, Pressable } from "react-native";
import { ApiError, api, ok, type S } from "../api";
import { errorText, useAction, useLoad } from "../hooks";
import { useT } from "../i18n";
import { takePhoto } from "../platform/photo";
import { useServices } from "../services";
import { EMERGENCY_NUMBER } from "../safety";
import { space, TOUCH, usePalette } from "../theme";
import { Banner, Button, Card, Field, Loading, Row, Txt } from "../ui";

/** Looks up one of the resident's own complaints by its number. Someone else's number looks the same as one that does not exist. */
export function FindByReference({ onFound }: { onFound: (id: string) => void }) {
  const t = useT();
  const [reference, setReference] = useState("");
  const find = useAction();
  const [missing, setMissing] = useState(false);

  async function go() {
    setMissing(false);
    const result = await find.run(async () => {
      try {
        return await ok(api.GET("/complaints/by-reference/{reference}", { params: { path: { reference: reference.trim() } } }));
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) { setMissing(true); return undefined; }
        throw e;
      }
    });
    if (result) onFound(result.id);
  }

  return (
    <Card>
      <Txt bold>{t("find.title")}</Txt>
      <Field label={t("find.reference")} hint={t("find.hint")} value={reference} onChangeText={(v) => { setReference(v); setMissing(false); }} autoCapitalize="characters" autoCorrect={false} />
      {missing && <Banner text={t("find.notFound")} />}
      {find.error && find.error.status !== 404 && <Banner tone="danger" text={errorText(t, find.error)} />}
      <Button title={t("find.go")} variant="secondary" onPress={() => void go()} loading={find.busy} disabled={reference.trim().length < 6} />
    </Card>
  );
}

/** Five tappable stars with words for screen readers; the number is always shown too. */
function Stars({ value, onChange }: { value: number; onChange: (n: number) => void }) {
  const t = useT();
  const p = usePalette();
  return (
    <Row gap={space[1]}>
      {[1, 2, 3, 4, 5].map((n) => (
        <Pressable key={n} accessibilityRole="radio" accessibilityState={{ selected: n === value }} accessibilityLabel={t("feedback.star", { count: n })}
          onPress={() => onChange(n)} style={{ minWidth: TOUCH, minHeight: TOUCH, alignItems: "center", justifyContent: "center" }}>
          <Txt size="xxl" color={n <= value ? p.accent : p.border}>{n <= value ? "★" : "☆"}</Txt>
        </Pressable>
      ))}
    </Row>
  );
}

/** The resident rates finished work (RA-06, JB-08). Sending again replaces the earlier answer. */
export function FeedbackCard({ complaintId, existing, defaultFixed, onSent }: { complaintId: string; existing?: S["FeedbackDto"] | null; defaultFixed: boolean; onSent: () => void }) {
  const t = useT();
  const send = useAction();
  const [editing, setEditing] = useState(!existing);
  const [rating, setRating] = useState(existing?.rating ?? 0);
  const [fixed, setFixed] = useState(existing?.confirmedFixed ?? defaultFixed);
  const [comment, setComment] = useState(existing?.comment ?? "");
  const [thanks, setThanks] = useState(false);

  if (!editing && existing) {
    return (
      <Card>
        <Txt bold>{t("feedback.title")}</Txt>
        {thanks && <Banner tone="success" text={t("feedback.thanks")} />}
        <Txt>{"★".repeat(existing.rating)}{"☆".repeat(5 - existing.rating)}  {t("feedback.star", { count: existing.rating })}</Txt>
        <Txt>{existing.confirmedFixed ? t("feedback.fixed") : t("feedback.notFixed")}</Txt>
        {existing.comment && <Txt muted>{existing.comment}</Txt>}
        <Button title={t("feedback.change")} variant="ghost" onPress={() => setEditing(true)} />
      </Card>
    );
  }

  return (
    <Card>
      <Txt bold>{t("feedback.title")}</Txt>
      <Txt muted>{t("feedback.rating")}</Txt>
      <Stars value={rating} onChange={setRating} />
      <Row wrap>
        <Button title={t("feedback.fixed")} variant={fixed ? "primary" : "secondary"} onPress={() => setFixed(true)} />
        <Button title={t("feedback.notFixed")} variant={fixed ? "secondary" : "primary"} onPress={() => setFixed(false)} />
      </Row>
      <Field label={t("feedback.comment")} value={comment} onChangeText={setComment} multiline numberOfLines={3} maxLength={1000} />
      {send.error && <Banner tone="danger" text={errorText(t, send.error)} />}
      <Button title={t("feedback.send")} loading={send.busy} disabled={rating === 0}
        onPress={() => void send.run(async () => {
          await ok(api.POST("/complaints/{id}/feedback", { params: { path: { id: complaintId } }, body: { rating, confirmedFixed: fixed, comment: comment.trim() || null } }));
          setThanks(true); setEditing(false); onSent();
        })} />
    </Card>
  );
}

/** Walks down the society's places (sector, street, house…); any level can be chosen. */
function LocationPicker({ onPick }: { onPick: (location: S["LocationDto"]) => void }) {
  const t = useT();
  const [trail, setTrail] = useState<S["LocationDto"][]>([]);
  const parent = trail[trail.length - 1];
  const children = useLoad(() => ok(api.GET("/locations", { params: { query: { parentId: parent?.id } } })), [parent?.id]);
  const p = usePalette();

  return (
    <>
      {trail.length > 0 && (
        <Row wrap gap={space[1]}>
          <Button title="←" variant="ghost" onPress={() => setTrail((x) => x.slice(0, -1))} accessibilityLabel={t("back")} />
          <Txt bold>{trail.map((l) => l.name).join(" › ")}</Txt>
        </Row>
      )}
      {parent && <Button title={`✓  ${parent.name}`} onPress={() => onPick(parent)} />}
      {children.loading && !children.data ? <Loading /> : (children.data ?? []).map((l) => (
        <Pressable key={l.id} accessibilityRole="button" onPress={() => setTrail((x) => [...x, l])}
          style={{ minHeight: TOUCH, justifyContent: "center", paddingHorizontal: space[3], borderRadius: 8, borderWidth: 1, borderColor: p.border }}>
          <Txt>{l.name} ›</Txt>
        </Pressable>
      ))}
    </>
  );
}

/** "Where is it?" for a complaint held because it had no place (RA-07), and a way to add another photo. */
export function MissingInfoCard({ complaint, onAnswered }: { complaint: S["ComplaintDto"]; onAnswered: () => void }) {
  const t = useT();
  const { queue, drain } = useServices();
  const send = useAction();
  const [notice, setNotice] = useState<string>();
  const needsLocation = complaint.status === "OnHold" && complaint.holdReason === "location_missing";

  async function addPhoto() {
    const shot = await takePhoto();
    if ("error" in shot) return;
    await queue.add({ target: { kind: "complaint", id: complaint.id }, uri: shot.photo.uri, mimeType: shot.photo.mimeType, size: shot.photo.size });
    setNotice(t("info.photoAdded"));
    void drain();
  }

  const open = !["Closed", "Rejected", "Merged"].includes(complaint.status);
  if (!needsLocation && !open) return null;

  return (
    <Card>
      {needsLocation && (
        <>
          <Txt bold>{t("info.title")}</Txt>
          <Txt>{t("info.location")}</Txt>
          <Txt bold>{t("info.place")}</Txt>
          <LocationPicker onPick={(location) => void send.run(async () => {
            await ok(api.POST("/complaints/{id}/location", { params: { path: { id: complaint.id } }, body: { houseId: null, locationId: location.id, locationText: location.name } }));
            setNotice(t("info.sent")); onAnswered();
          })} />
          {send.error && <Banner tone="danger" text={errorText(t, send.error)} />}
        </>
      )}
      {open && <Button title={t("info.addPhoto")} variant="secondary" onPress={() => void addPhoto()} />}
      {notice && <Banner tone="success" text={notice} />}
    </Card>
  );
}

/** Shown before sending when the words suggest someone may be in danger (RA-08). It never stops the complaint. */
export function EmergencyPrompt({ onSend, busy }: { onSend: () => void; busy: boolean }) {
  const t = useT();
  const p = usePalette();
  return (
    <Card style={{ borderColor: p.danger, borderWidth: 3 }}>
      <Txt size="lg" bold color={p.danger}>⚠ {t("emergency.title")}</Txt>
      <Txt>{t("emergency.body")}</Txt>
      <Button title={t("emergency.call")} variant="danger" onPress={() => void Linking.openURL(`tel:${EMERGENCY_NUMBER}`)} />
      <Button title={t("emergency.send")} variant="secondary" onPress={onSend} loading={busy} />
    </Card>
  );
}
