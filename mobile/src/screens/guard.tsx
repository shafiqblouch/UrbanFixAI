import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Modal, View } from "react-native";
import { CameraView, useCameraPermissions, type BarcodeScanningResult } from "expo-camera";
import { api, ok, type S } from "../api";
import { errorText, useAction, useLoad, usePolling, useRefetchOnFocus } from "../hooks";
import { useI18n, useT } from "../i18n";
import type { Key } from "../i18n/strings";
import type { GateEventInput } from "../offline/store";
import { useServices } from "../services";
import { space } from "../theme";
import { Banner, Button, Card, Chip, Choice, Empty, Field, Loading, Row, Screen, Txt } from "../ui";

type Lookup = S["LookupResult"];
type Vehicle = S["VehicleHit"];
type Help = S["HelpHit"];
type Pass = S["PassHit"];
type Event = S["EventDto"];
type Approval = S["GateApprovalDto"];

type Direction = "in" | "out";
type Decision = "allowed" | "denied";

/** The time of day a crossing happened, in the app's locale. */
function useTimeFormat() {
  const { locale } = useI18n();
  return useCallback((iso: string) => {
    try { return new Date(iso).toLocaleTimeString(locale === "ur" ? "ur-PK" : "en-PK", { hour: "numeric", minute: "2-digit" }); }
    catch { return ""; }
  }, [locale]);
}

/** A short debounce so the lookup fires once the guard stops typing, not on every key. */
function useDebounced<T>(value: T, ms = 300): T {
  const [v, setV] = useState(value);
  useEffect(() => { const id = setTimeout(() => setV(value), ms); return () => clearTimeout(id); }, [value, ms]);
  return v;
}

// ---- Gate: look someone up and record a crossing (GT-01, GT-02) ---------------------------------------------
export function GateScreen() {
  const t = useT();
  const { recordGate, gatePending, drainGate } = useServices();
  const [direction, setDirection] = useState<Direction>("in");
  const [query, setQuery] = useState("");
  const q = useDebounced(query.trim());
  const [message, setMessage] = useState<{ tone: "success" | "info"; text: string }>();

  const results = useLoad<Lookup | undefined>(
    () => (q.length >= 2 ? ok(api.GET("/gate/lookup", { params: { query: { q } } })) : Promise.resolve(undefined)),
    [q],
  );

  const record = useCallback(async (input: GateEventInput, label: string) => {
    const how = await recordGate(input, label).catch(() => "error" as const);
    if (how === "error") { setMessage({ tone: "info", text: t("gate.recordFailed") }); return; }
    setMessage({ tone: how === "sent" ? "success" : "info", text: how === "sent" ? t("gate.logged", { who: label }) : t("gate.queued", { who: label }) });
    setQuery("");
  }, [recordGate, t]);

  const hits = results.data;
  const nothing = q.length >= 2 && !results.loading && hits && hits.vehicles.length === 0 && hits.help.length === 0 && hits.passes.length === 0;

  return (
    <Screen>
      {gatePending > 0 && (
        <Banner tone="info" text={t("gate.pendingBanner", { count: gatePending })} />
      )}
      {message && <Banner tone={message.tone === "success" ? "success" : "info"} text={message.text} />}

      <BarrierWaiting />

      <Choice<Direction>
        label={t("gate.direction")}
        value={direction}
        onChange={setDirection}
        options={[{ value: "in", label: t("gate.in") }, { value: "out", label: t("gate.out") }]}
      />

      <Field
        label={t("gate.search")}
        hint={t("gate.searchHint")}
        value={query}
        onChangeText={setQuery}
        autoCapitalize="characters"
        autoCorrect={false}
        placeholder={t("gate.searchPlaceholder")}
      />

      <ScanStickerButton onScan={setQuery} />

      {results.loading && q.length >= 2 && <Loading />}
      {nothing && <Empty text={t("gate.noMatch")} />}

      {hits?.vehicles.map((v) => <VehicleResult key={v.id} v={v} direction={direction} onRecord={record} />)}
      {hits?.help.map((h) => <HelpResult key={h.id} h={h} direction={direction} onRecord={record} />)}
      {hits?.passes.map((p) => <PassResult key={p.id} p={p} direction={direction} onRecord={record} />)}

      <ManualResult direction={direction} onRecord={record} />

      {gatePending > 0 && <Button title={t("gate.syncNow")} variant="secondary" onPress={() => void drainGate()} />}
    </Screen>
  );
}

/** Cars an RFID reader is holding at the barrier: the boom stays shut until the guard allows or refuses here (GT-05). */
function BarrierWaiting() {
  const t = useT();
  const waiting = useLoad<Approval[]>(() => ok(api.GET("/gate/waiting", {})), []);
  const act = useAction();
  useRefetchOnFocus(waiting.reload);
  usePolling(waiting.reload, 4000); // a car at the barrier should show within a few seconds, then clear once decided
  const [note, setNote] = useState<{ tone: "success" | "info"; text: string }>();

  const decide = (id: string, approve: boolean) => void act.run(async () => {
    await ok(api.POST("/gate/waiting/{id}/decide", { params: { path: { id } }, body: { approve, note: null } }));
    setNote({ tone: approve ? "success" : "info", text: t(approve ? "gate.opened" : "gate.refused") });
    waiting.reload();
  });

  const rows = waiting.data ?? [];
  if (rows.length === 0 && !note) return null;
  return (
    <Card>
      <Txt size="lg" bold>{t("gate.barrier")}</Txt>
      {note && <Banner tone={note.tone === "success" ? "success" : "info"} text={note.text} />}
      {rows.map((a) => (
        <View key={a.id} style={{ gap: space[2] }}>
          <Txt bold>{t("gate.carWaiting", { direction: t(`gate.${a.direction}` as "gate.in" | "gate.out") })}</Txt>
          {(a.vehiclePlate || a.tag) && (
            <Txt muted>{a.vehiclePlate ?? t("gate.carWaitingTag", { tag: a.tag ?? "" })}{a.gateName ? ` · ${a.gateName}` : ""}</Txt>
          )}
          <Row gap={space[2]}>
            <View style={{ flex: 1 }}><Button title={t("gate.openBarrier")} onPress={() => decide(a.id, true)} /></View>
            <View style={{ flex: 1 }}><Button title={t("gate.refuse")} variant="danger" onPress={() => decide(a.id, false)} /></View>
          </Row>
        </View>
      ))}
    </Card>
  );
}

/** Scan a car's gate sticker with the phone camera (GT-05). Reads the QR/barcode on the printed label and drops the
 *  code into the lookup, so the matching car appears and the guard records the crossing — a backup to the fixed UHF
 *  reader, and the way to use a sticker before that hardware is installed. (The phone cannot read the UHF chip itself.) */
function ScanStickerButton({ onScan }: { onScan: (code: string) => void }) {
  const t = useT();
  const [open, setOpen] = useState(false);
  const [denied, setDenied] = useState(false);
  const [permission, requestPermission] = useCameraPermissions();
  const lock = useRef(false);

  const start = async () => {
    setDenied(false);
    const granted = permission?.granted || (await requestPermission()).granted;
    if (!granted) { setDenied(true); return; }
    lock.current = false;
    setOpen(true);
  };

  const onBarcode = (r: BarcodeScanningResult) => {
    if (lock.current) return; // the camera fires continuously; take the first read only
    lock.current = true;
    setOpen(false);
    onScan((r.data ?? "").trim());
  };

  return (
    <>
      <Button title={t("gate.scan")} variant="secondary" onPress={() => void start()} />
      {denied && <Banner tone="info" text={t("gate.scanDenied")} />}
      <Modal visible={open} animationType="slide" onRequestClose={() => setOpen(false)}>
        <View style={{ flex: 1, backgroundColor: "#000" }}>
          {open && (
            <CameraView
              style={{ flex: 1 }}
              facing="back"
              barcodeScannerSettings={{ barcodeTypes: ["qr", "code39", "code128"] }}
              onBarcodeScanned={onBarcode}
            />
          )}
          <View style={{ position: "absolute", top: 56, left: space[4], right: space[4], alignItems: "center" }}>
            <View style={{ backgroundColor: "rgba(0,0,0,0.6)", borderRadius: 12, paddingHorizontal: space[4], paddingVertical: space[3] }}>
              <Txt color="#FFFFFF" style={{ textAlign: "center" }}>{t("gate.scanHint")}</Txt>
            </View>
          </View>
          <View style={{ position: "absolute", bottom: 48, left: space[4], right: space[4] }}>
            <Button title={t("gate.scanClose")} variant="danger" onPress={() => setOpen(false)} />
          </View>
        </View>
      </Modal>
    </>
  );
}

function ActionButtons({ direction, allowLabel, onAllow, onDeny }: {
  direction: Direction; allowLabel: string; onAllow: () => void; onDeny: () => void;
}) {
  const t = useT();
  return (
    <Row gap={space[2]} wrap>
      <View style={{ flex: 1, minWidth: 140 }}><Button title={allowLabel} onPress={onAllow} /></View>
      {direction === "in" && <View style={{ flex: 1, minWidth: 120 }}><Button title={t("gate.deny")} variant="danger" onPress={onDeny} /></View>}
    </Row>
  );
}

function VehicleResult({ v, direction, onRecord }: { v: Vehicle; direction: Direction; onRecord: (i: GateEventInput, label: string) => void }) {
  const t = useT();
  const base: GateEventInput = { direction, subjectType: "vehicle", vehicleId: v.id, houseId: v.houseId ?? null, vehiclePlate: v.plate };
  return (
    <Card>
      <Row style={{ justifyContent: "space-between" }}><Txt bold size="lg">{v.plate}</Txt><Chip tone="info" glyph="🚗" label={t(`gate.vtype.${v.type}` as Key)} /></Row>
      <Txt muted>{v.houseLabel ?? t("gate.noHouse")}</Txt>
      {v.status !== "active" && <Chip tone="warning" glyph="!" label={t("gate.vehicleInactive")} />}
      <ActionButtons
        direction={direction}
        allowLabel={direction === "in" ? t("gate.allowIn") : t("gate.logOut")}
        onAllow={() => onRecord({ ...base, decision: "allowed" }, v.plate)}
        onDeny={() => onRecord({ ...base, decision: "denied" }, v.plate)}
      />
    </Card>
  );
}

function HelpResult({ h, direction, onRecord }: { h: Help; direction: Direction; onRecord: (i: GateEventInput, label: string) => void }) {
  const t = useT();
  const base: GateEventInput = { direction, subjectType: "help", helpId: h.id, houseId: h.houseIds[0] ?? null, subjectName: h.name };
  return (
    <Card>
      <Row style={{ justifyContent: "space-between" }}>
        <Txt bold size="lg">{h.name}</Txt>
        <Chip tone="neutral" glyph="🧹" label={t(`gate.role.${h.role}` as Key)} />
      </Row>
      {direction === "in" && (
        h.allowedNow
          ? <Chip tone="success" glyph="✓" label={t("gate.allowedNow")} />
          : <Chip tone="warning" glyph="⏰" label={t("gate.offSchedule")} />
      )}
      <ActionButtons
        direction={direction}
        allowLabel={direction === "in" ? t("gate.allowIn") : t("gate.logOut")}
        onAllow={() => onRecord({ ...base, decision: "allowed" }, h.name)}
        onDeny={() => onRecord({ ...base, decision: "denied" }, h.name)}
      />
    </Card>
  );
}

function PassResult({ p, direction, onRecord }: { p: Pass; direction: Direction; onRecord: (i: GateEventInput, label: string) => void }) {
  const t = useT();
  const valid = p.state === "Valid";
  const base: GateEventInput = { direction, subjectType: "visitor", passId: p.id, houseId: p.houseId, subjectName: p.visitorName, vehiclePlate: p.vehiclePlate ?? null };
  return (
    <Card>
      <Row style={{ justifyContent: "space-between" }}>
        <Txt bold size="lg">{p.visitorName}</Txt>
        <Chip tone={valid ? "success" : "danger"} glyph={valid ? "✓" : "✕"} label={t(`gate.pass.${p.state}` as Key)} />
      </Row>
      <Txt muted>{t("gate.pass.code")}: {p.code} · {p.houseLabel ?? ""}</Txt>
      {p.vehiclePlate && <Txt muted>{p.vehiclePlate}</Txt>}
      <ActionButtons
        direction={direction}
        allowLabel={direction === "in" ? t("gate.allowIn") : t("gate.logOut")}
        onAllow={() => onRecord({ ...base, decision: "allowed" }, p.visitorName)}
        onDeny={() => onRecord({ ...base, decision: "denied" }, p.visitorName)}
      />
    </Card>
  );
}

/** For someone not in the directory: the guard types a name and (optionally) a plate. */
function ManualResult({ direction, onRecord }: { direction: Direction; onRecord: (i: GateEventInput, label: string) => void }) {
  const t = useT();
  const [name, setName] = useState("");
  const [plate, setPlate] = useState("");
  const clean = name.trim();
  const submit = (decision: Decision) => {
    const label = clean || plate.trim() || t("gate.visitor");
    onRecord({
      direction, subjectType: "visitor", decision,
      subjectName: clean || null, vehiclePlate: plate.trim() || null,
    }, label);
    setName(""); setPlate("");
  };
  return (
    <Card>
      <Txt bold size="lg">{t("gate.manualTitle")}</Txt>
      <Txt muted>{t("gate.manualHint")}</Txt>
      <Field label={t("gate.visitorName")} value={name} onChangeText={setName} placeholder={t("gate.visitorNamePlaceholder")} />
      <Field label={t("gate.plateOptional")} value={plate} onChangeText={setPlate} autoCapitalize="characters" autoCorrect={false} />
      <ActionButtons
        direction={direction}
        allowLabel={direction === "in" ? t("gate.allowIn") : t("gate.logOut")}
        onAllow={() => submit("allowed")}
        onDeny={() => submit("denied")}
      />
    </Card>
  );
}

// ---- Log: who is inside and the recent crossings (GT-01) ----------------------------------------------------
export function LogScreen() {
  const t = useT();
  const { gateQueue, gatePending, drainGate } = useServices();
  const time = useTimeFormat();
  const [tab, setTab] = useState<"inside" | "recent">("inside");
  const [pending, setPending] = useState<Awaited<ReturnType<typeof gateQueue.list>>>([]);

  const inside = useLoad<Event[]>(() => ok(api.GET("/gate/inside", {})), []);
  const recent = useLoad<Event[]>(() => ok(api.GET("/gate/events", { params: { query: { limit: 100 } } })), []);
  const refresh = tab === "inside" ? inside.reload : recent.reload;
  useRefetchOnFocus(refresh);
  useEffect(() => { void gateQueue.list().then(setPending); }, [gateQueue, gatePending, tab]);

  const active = tab === "inside" ? inside : recent;
  const rows = active.data ?? [];

  return (
    <Screen>
      {pending.length > 0 && (
        <Card>
          <Row style={{ justifyContent: "space-between" }}>
            <Txt bold>{t("gate.pending")}</Txt>
            <Button title={t("gate.syncNow")} variant="ghost" onPress={() => void drainGate()} />
          </Row>
          {pending.map((e) => (
            <Row key={e.id} style={{ justifyContent: "space-between" }}>
              <Txt>{e.label}</Txt>
              <Chip tone="warning" glyph="↻" label={t(`gate.${e.input.direction}` as "gate.in" | "gate.out")} />
            </Row>
          ))}
        </Card>
      )}

      <Choice<"inside" | "recent">
        label={t("gate.show")}
        value={tab}
        onChange={setTab}
        options={[{ value: "inside", label: t("gate.insideNow") }, { value: "recent", label: t("gate.recent") }]}
      />

      {active.loading && rows.length === 0 && <Loading />}
      {!active.loading && rows.length === 0 && <Empty text={tab === "inside" ? t("gate.nobodyInside") : t("gate.noEvents")} />}

      {rows.map((e) => <EventRow key={e.id} e={e} time={time} showDirection={tab === "recent"} />)}
    </Screen>
  );
}

function EventRow({ e, time, showDirection }: { e: Event; time: (iso: string) => string; showDirection: boolean }) {
  const t = useT();
  const who = e.subjectName || e.vehiclePlate || t("gate.visitor");
  return (
    <Card>
      <Row style={{ justifyContent: "space-between" }}>
        <Txt bold>{who}</Txt>
        <Txt muted size="sm">{time(e.recordedAt)}</Txt>
      </Row>
      {e.houseLabel && <Txt muted size="sm">{e.houseLabel}</Txt>}
      <Row gap={space[2]} wrap>
        {showDirection && <Chip tone={e.direction === "in" ? "info" : "neutral"} glyph={e.direction === "in" ? "→" : "←"} label={t(`gate.${e.direction}` as "gate.in" | "gate.out")} />}
        {e.decision === "denied" && <Chip tone="danger" glyph="✕" label={t("gate.denied")} />}
        {e.vehiclePlate && <Chip tone="neutral" glyph="🚗" label={e.vehiclePlate} />}
      </Row>
    </Card>
  );
}
