import { useNavigation } from "@react-navigation/native";
import type { NativeStackScreenProps } from "@react-navigation/native-stack";
import { useEffect, useMemo, useState } from "react";
import { Image, View } from "react-native";
import { ApiError, api, ok, transcribeVoice, type S } from "../api";
import { amountOf, emptyLine, fromServer, KINDS, problemWith, toInput, totalOf, type Line } from "../costs";
import { emptyRequestLine, requestBody, requestIsValid, type RequestLine } from "../materials";
import { errorText, useAction, useLoad } from "../hooks";
import { useI18n, useT } from "../i18n";
import type { Key } from "../i18n/strings";
import { checklistProgress, completionBlockers } from "../jobRules";
import { currentPosition } from "../platform/device";
import { takePhoto } from "../platform/photo";
import { useVoiceRecorder } from "../platform/voice";
import { dueChip, todaysJobs, type Due } from "../roles";
import { useServices } from "../services";
import { space } from "../theme";
import { Banner, Button, Card, Choice, Chip, Empty, Field, Loading, Row, Screen, Txt } from "../ui";
import type { RootStackParams } from "../navigation";

type Nav = NativeStackScreenProps<RootStackParams>["navigation"];
type Item = S["JobListItemDto"];

const jobTone: Record<string, "success" | "warning" | "danger" | "info" | "neutral"> = {
  Open: "info", Accepted: "neutral", InProgress: "warning", PendingVerification: "warning", Completed: "success", Returned: "danger", Cancelled: "neutral",
};
const jobGlyph: Record<string, string> = { Open: "○", Accepted: "◐", InProgress: "◑", PendingVerification: "⏳", Completed: "✓", Returned: "✕", Cancelled: "✕" };

function JobChip({ status }: { status: string }) {
  const t = useT();
  return <Chip label={t(`job.${status}` as Key)} tone={jobTone[status] ?? "neutral"} glyph={jobGlyph[status]} />;
}

/** "Due in 3 h" / "Overdue by 2 d" as a chip that carries an icon as well as colour. */
function DueChip({ dueAt }: { dueAt: string }) {
  const t = useT();
  const { number } = useI18n();
  const due: Due = dueChip(dueAt, new Date());
  const unit = { m: "min", h: "h", d: "d" }[due.unit];
  const when = `${number(due.amount)} ${unit}`;
  if (due.kind === "overdue") return <Chip tone="danger" glyph="⚠" label={t("today.overdue", { when })} />;
  if (due.kind === "soon") return <Chip tone="warning" glyph="◷" label={t("today.due", { when })} />;
  return <Chip tone="neutral" glyph="◷" label={t("today.later")} />;
}

function JobCard({ item, onPress }: { item: Item; onPress: () => void }) {
  return (
    <Card onPress={onPress}>
      <Row><Txt bold style={{ flex: 1 }}>{item.reference}</Txt><JobChip status={item.job.status} /></Row>
      <Txt numberOfLines={2}>{item.description}</Txt>
      <DueChip dueAt={item.dueAt} />
    </Card>
  );
}

/** Today's jobs with due-time chips, most urgent first (TA-01). Urdu-first, large buttons, icons as well as words. */
export function TodayScreen() {
  const t = useT();
  const nav = useNavigation<Nav>();
  const jobs = useLoad(() => ok(api.GET("/jobs", { params: { query: { mine: true, limit: 100 } } })), []);
  const today = useMemo(() => todaysJobs(jobs.data ?? [], new Date()), [jobs.data]);
  const { waiting } = useServices();
  if (jobs.loading && !jobs.data) return <Screen><Loading /></Screen>;
  return (
    <Screen>
      {waiting > 0 && <Banner tone="info" text={t("offline.queued", { count: waiting })} />}
      {jobs.error && <Banner text={errorText(t, jobs.error)} />}
      <Txt size="xl" bold>{t("today.title")}</Txt>
      {today.length === 0 ? <Empty text={t("today.none")} /> : today.map((j) => <JobCard key={j.job.id} item={j} onPress={() => nav.navigate("JobDetail", { id: j.job.id })} />)}
      <Button title={t("retry")} variant="ghost" onPress={jobs.reload} />
    </Screen>
  );
}

export function JobsScreen() {
  const t = useT();
  const nav = useNavigation<Nav>();
  const jobs = useLoad(() => ok(api.GET("/jobs", { params: { query: { mine: true, limit: 100 } } })), []);
  if (jobs.loading && !jobs.data) return <Screen><Loading /></Screen>;
  const list = [...(jobs.data ?? [])].sort((a, b) => new Date(b.job.createdAt).getTime() - new Date(a.job.createdAt).getTime());
  return (
    <Screen>
      {jobs.error && <Banner text={errorText(t, jobs.error)} />}
      {list.length === 0 ? <Empty text={t("jobs.none")} /> : list.map((j) => <JobCard key={j.job.id} item={j} onPress={() => nav.navigate("JobDetail", { id: j.job.id })} />)}
    </Screen>
  );
}

/** One job: instructions, place, the resident's photos, and every step the technician takes (TA-02 to TA-05). */
export function JobDetailScreen({ route }: NativeStackScreenProps<RootStackParams, "JobDetail">) {
  const t = useT();
  const nav = useNavigation<Nav>();
  const { id } = route.params;
  const { queue, drain, waiting } = useServices();
  const detail = useLoad(() => ok(api.GET("/jobs/{id}", { params: { path: { id } } })), [id]);
  const complaint = useLoad(async () => (detail.data ? ok(api.GET("/complaints/{id}", { params: { path: { id: detail.data.job.complaintId } } })) : undefined), [detail.data?.job.complaintId]);
  const act = useAction();
  const [note, setNote] = useState("");
  const [withGps, setWithGps] = useState(false);
  const [cannot, setCannot] = useState(false);
  const [reason, setReason] = useState("");
  const [message, setMessage] = useState<string>();
  const [queued, setQueued] = useState({ before: 0, after: 0 });
  const { locale } = useI18n();
  const voice = useVoiceRecorder();
  const dictate = useAction();
  const [dictation, setDictation] = useState<"ok" | "low">();

  // A finished recording is turned into words the technician can correct before saving (TA-08).
  useEffect(() => {
    if (!voice.note) return;
    const uri = voice.note.uri;
    voice.discard();
    setDictation(undefined);
    void dictate.run(async () => {
      const r = await transcribeVoice(uri, locale === "ur" ? "ur" : "en");
      if (r.transcript.trim()) setNote((current) => (current.trim() ? `${current.trim()} ${r.transcript.trim()}` : r.transcript.trim()));
      setDictation(r.lowConfidence ? "low" : "ok");
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [voice.note]);

  // Photos taken but not uploaded yet still count for the person holding the phone.
  useEffect(() => {
    void queue.list().then((items) => setQueued({
      before: items.filter((i) => i.target.kind === "job" && i.target.id === id && i.target.stage === "before").length,
      after: items.filter((i) => i.target.kind === "job" && i.target.id === id && i.target.stage === "after").length,
    }));
  }, [queue, id, waiting]);

  if (!detail.data) return <Screen>{detail.error ? <Banner text={errorText(t, detail.error)} /> : <Loading />}</Screen>;
  const d = detail.data;
  const job = d.job;
  const path = { params: { path: { id } } };
  const photos = (stage: string) => d.evidence.filter((e) => e.stage === stage).length;
  const refresh = () => { setMessage(undefined); detail.reload(); };
  const blockers = completionBlockers({
    rule: job.evidenceRule, before: photos("before") + queued.before, after: photos("after") + queued.after, checklist: d.checklist,
    note, requiresCosts: job.requiresCosts, hasCosts: d.actuals.length > 0,
  });
  const progress = checklistProgress(d.checklist);
  const dictateText = dictate.error instanceof ApiError
    ? (dictate.error.code === "ai_not_available" ? t("job.dictateOff") : dictate.error.code === "network" ? t("error.network") : t("job.dictateFailed"))
    : undefined;
  const missing = act.error?.code === "evidence_missing" ? ((act.error.body as { missing?: string[] } | undefined)?.missing ?? []) : [];

  async function photo(stage: "before" | "after") {
    const shot = await takePhoto();
    if ("error" in shot) return;
    await queue.add({ target: { kind: "job", id, stage }, uri: shot.photo.uri, mimeType: shot.photo.mimeType, size: shot.photo.size });
    setQueued((q) => ({ ...q, [stage]: q[stage] + 1 }));
    void drain();
  }

  // Starting and finishing the work always record where the technician was, so the manager's map can show it; notes only when ticked.
  const fixOrUndefined = async () => { const fix = await currentPosition(); return "latitude" in fix ? fix : undefined; };

  const addNote = () => act.run(async () => {
    const at = withGps ? await fixOrUndefined() : undefined;
    await ok(api.POST("/jobs/{id}/updates", { ...path, body: { note: note.trim(), latitude: at?.latitude ?? null, longitude: at?.longitude ?? null, checklistDone: null } }));
    setNote("");
    refresh();
  });

  const toggle = (index: number) => act.run(async () => {
    const done = d.checklist.map((c, i) => (i === index ? !c.done : c.done)).flatMap((v, i) => (v ? [i] : []));
    await ok(api.POST("/jobs/{id}/updates", { ...path, body: { note: null, latitude: null, longitude: null, checklistDone: done } }));
    refresh();
  });

  return (
    <Screen>
      <Row><Txt size="xl" bold style={{ flex: 1 }}>{job.number}</Txt><JobChip status={job.status} /></Row>

      <Card>
        {job.instructions && <><Txt bold>{t("job.instructions")}</Txt><Txt>{job.instructions}</Txt></>}
        {complaint.data && <><Txt bold>{t("job.place")}</Txt><Txt>{complaint.data.complaint.locationText ?? "—"}</Txt><Txt muted>{complaint.data.complaint.description}</Txt></>}
      </Card>

      {complaint.data && complaint.data.media.some((m) => m.kind === "image") && (
        <Card>
          <Txt bold>{t("job.resident")}</Txt>
          <Row wrap gap={space[2]}>
            {complaint.data.media.filter((m) => m.kind === "image").map((m) => <Image key={m.id} accessibilityIgnoresInvertColors source={{ uri: m.thumbUrl ?? m.url }} style={{ width: 96, height: 96, borderRadius: 8 }} />)}
          </Row>
        </Card>
      )}

      {job.status === "Open" && <Button title={t("job.accept")} loading={act.busy} onPress={() => void act.run(async () => { await ok(api.POST("/jobs/{id}/accept", path)); refresh(); })} />}
      {job.status === "Accepted" && <Button title={t("job.start")} loading={act.busy} onPress={() => void act.run(async () => { const at = await fixOrUndefined(); await ok(api.POST("/jobs/{id}/start", { ...path, body: { note: null, latitude: at?.latitude ?? null, longitude: at?.longitude ?? null } })); refresh(); })} />}

      {["Open", "Accepted"].includes(job.status) && <Button title={t("job.estimate")} variant="secondary" onPress={() => nav.navigate("Costs", { jobId: id, mode: "estimate" })} />}
      {d.estimate && <Txt muted>{t("job.estimateSaved", { version: d.estimate.version, total: d.estimate.total.toFixed(2) })}</Txt>}

      {["Accepted", "InProgress"].includes(job.status) && <Button title={t("job.materials")} variant="secondary" onPress={() => nav.navigate("Materials", { jobId: id })} />}

      {["Accepted", "InProgress"].includes(job.status) && (
        <Card>
          <Row wrap>
            <Button title={t("job.before")} variant="secondary" onPress={() => void photo("before")} />
            <Button title={t("job.after")} variant="secondary" onPress={() => void photo("after")} />
          </Row>
          <Txt muted>{t("job.beforeCount", { count: photos("before") + queued.before })} · {t("job.afterCount", { count: photos("after") + queued.after })}</Txt>
          {waiting > 0 && <Txt size="sm" muted>{t("offline.queued", { count: waiting })}</Txt>}
        </Card>
      )}

      {job.status === "InProgress" && (
        <>
          {d.checklist.length > 0 && (
            <Card>
              <Txt bold>{t("job.checklist")} · {t("job.progress", { done: progress.done, total: progress.total })}</Txt>
              {d.checklist.map((c, i) => <Button key={i} variant={c.done ? "secondary" : "ghost"} title={`${c.done ? "☑" : "☐"}  ${c.label}`} onPress={() => void toggle(i)} />)}
            </Card>
          )}
          <Card>
            <Field label={t("job.note")} hint={t("job.noteHint")} value={note} onChangeText={(v) => { setNote(v); setDictation(undefined); }} multiline numberOfLines={3} />
            {voice.recording
              ? <Button title={t("job.dictating")} variant="danger" onPress={() => void voice.stop()} />
              : <Button title={dictate.busy ? t("job.transcribing") : t("job.dictate")} variant="secondary" loading={dictate.busy} onPress={() => void voice.start()} />}
            {voice.denied && <Banner text={t("new.voiceDenied")} />}
            {dictation === "ok" && <Banner tone="info" text={t("job.dictated")} />}
            {dictation === "low" && <Banner text={`${t("job.dictated")} ${t("job.dictateLow")}`} />}
            {dictateText && <Banner text={dictateText} />}
            <Row><Button title={withGps ? `☑ ${t("job.gps")}` : `☐ ${t("job.gps")}`} variant="ghost" onPress={() => setWithGps((v) => !v)} /></Row>
            <Button title={t("save")} variant="secondary" disabled={!note.trim()} loading={act.busy} onPress={() => void addNote()} />
          </Card>
          <Button title={t("job.costs")} variant="secondary" onPress={() => nav.navigate("Costs", { jobId: id, mode: "actuals" })} />
          {blockers.length > 0 && <Banner text={[t("job.notReady"), ...blockers.map((m) => `• ${t(`job.missing.${m}` as Key)}`)].join("\n")} />}
          <Button title={t("job.complete")} loading={act.busy} disabled={blockers.length > 0}
            onPress={() => void act.run(async () => { const at = await fixOrUndefined(); await ok(api.POST("/jobs/{id}/complete", { ...path, body: { note: note.trim() || null, latitude: at?.latitude ?? null, longitude: at?.longitude ?? null } })); setMessage(t("job.completed")); refresh(); })} />
        </>
      )}

      {["Accepted", "InProgress"].includes(job.status) && (
        <>
          {!cannot && <Button title={t("job.cannot")} variant="danger" onPress={() => setCannot(true)} />}
          {cannot && (
            <Card>
              <Field label={t("job.cannotReason")} value={reason} onChangeText={setReason} multiline numberOfLines={3} />
              <Button title={t("send")} variant="danger" disabled={!reason.trim()} loading={act.busy}
                onPress={() => void act.run(async () => { await ok(api.POST("/jobs/{id}/cannot-complete", { ...path, body: { reason: reason.trim() } })); setMessage(t("job.cannotSent")); setCannot(false); refresh(); })} />
            </Card>
          )}
        </>
      )}

      {message && <Banner tone="success" text={message} />}
      {missing.length > 0 && (
        <Banner text={missing.map((m) => t(`job.missing.${m}` as Key)).join("\n") + (waiting > 0 ? `\n${t("offline.queued", { count: waiting })}` : "")} />
      )}
      {act.error && missing.length === 0 && <Banner tone="danger" text={errorText(t, act.error)} />}
    </Screen>
  );
}

/** Estimate before work, actual costs at completion: lines of materials from the price list, labour, transport (TA-06). */
export function CostsScreen({ route }: NativeStackScreenProps<RootStackParams, "Costs">) {
  const t = useT();
  const nav = useNavigation<Nav>();
  const { jobId, mode } = route.params;
  const items = useLoad(() => ok(api.GET("/price-items", {})), []);
  const detail = useLoad(() => ok(api.GET("/jobs/{id}", { params: { path: { id: jobId } } })), [jobId]);
  const [lines, setLines] = useState<Line[]>();
  const act = useAction();
  const [saved, setSaved] = useState<string>();
  const { number } = useI18n();

  useEffect(() => {
    if (lines || !detail.data) return;
    const existing = mode === "estimate" ? detail.data.estimate?.lines ?? [] : (detail.data.actuals as S["CostLineDto"][]);
    setLines(existing.length ? existing.map(fromServer) : [emptyLine()]);
  }, [detail.data, lines, mode]);

  if (!lines || !items.data) return <Screen><Loading /></Screen>;
  const catalogue = items.data;
  const priced = catalogue.filter((i) => i.isActive && i.currentRate);
  const set = (i: number, patch: Partial<Line>) => { setSaved(undefined); setLines((ls) => ls!.map((l, n) => (n === i ? { ...l, ...patch } : l))); };
  const total = totalOf(lines, catalogue);
  const valid = lines.every((l) => !problemWith(l));

  const save = () => act.run(async () => {
    const body = lines.map(toInput);
    if (mode === "estimate") {
      const r = await ok(api.POST("/jobs/{id}/estimates", { params: { path: { id: jobId } }, body: { note: null, lines: body } }));
      setSaved(t("job.estimateSaved", { version: r.version, total: r.total.toFixed(2) }));
    } else {
      const r = await ok(api.PUT("/jobs/{id}/costs", { params: { path: { id: jobId } }, body: { lines: body } }));
      setSaved(t("job.costsSaved", { total: r.lines.reduce((n, l) => n + l.amount, 0).toFixed(2) }));
    }
  });

  return (
    <Screen>
      <Txt size="xl" bold>{mode === "estimate" ? t("job.estimate") : t("job.costs")}</Txt>
      {lines.map((l, i) => (
        <Card key={i}>
          <Choice label={t("cost.title")} value={l.kind} options={KINDS.map((k) => ({ value: k, label: t(`cost.${k}` as Key) }))}
            onChange={(kind) => set(i, { kind, priceItemId: kind === "material" ? l.priceItemId : "" })} />
          {l.kind === "material" ? (
            <Choice label={t("cost.item")} value={l.priceItemId || undefined} options={priced.map((p) => ({ value: p.id, label: `${p.name} — Rs ${p.currentRate!.rate.toFixed(2)} / ${p.unit}` }))} onChange={(id) => set(i, { priceItemId: id })} />
          ) : (
            <>
              <Field label={t("cost.description")} value={l.description} onChangeText={(v) => set(i, { description: v })} />
              <Field label={t("cost.rate")} value={l.unitRate} onChangeText={(v) => set(i, { unitRate: v })} keyboardType="decimal-pad" />
            </>
          )}
          <Field label={t("cost.quantity")} value={l.quantity} onChangeText={(v) => set(i, { quantity: v })} keyboardType="decimal-pad" />
          <Txt bold>Rs {number(amountOf(l, catalogue))}</Txt>
          {lines.length > 1 && <Button title={t("cost.remove")} variant="ghost" onPress={() => setLines((ls) => ls!.filter((_, n) => n !== i))} />}
        </Card>
      ))}
      <Button title={t("cost.add")} variant="secondary" onPress={() => setLines((ls) => [...ls!, emptyLine("labour")])} />
      <Txt size="lg" bold>{t("cost.total", { total: total.toFixed(2) })}</Txt>
      {act.error && <Banner tone="danger" text={act.error.code === "costs_locked" || act.error.code === "work_started" ? t("cost.locked") : errorText(t, act.error)} />}
      {saved && <Banner tone="success" text={saved} />}
      <View style={{ gap: space[2] }}>
        <Button title={mode === "estimate" ? t("cost.saveEstimate") : t("cost.saveCosts")} onPress={() => void save()} loading={act.busy} disabled={!valid} />
        <Button title={t("back")} variant="ghost" onPress={() => nav.goBack()} />
      </View>
    </Screen>
  );
}

const requestTone: Record<string, "success" | "warning" | "danger" | "neutral"> = {
  Open: "warning", Fulfilled: "success", Rejected: "danger", Cancelled: "neutral",
};

/** What the store holds, what has been issued to this job, and asking for more materials (TA-09). */
export function MaterialsScreen({ route }: NativeStackScreenProps<RootStackParams, "Materials">) {
  const t = useT();
  const nav = useNavigation<Nav>();
  const { number } = useI18n();
  const { jobId } = route.params;
  const stock = useLoad(() => ok(api.GET("/stock", {})), []);
  const issued = useLoad(() => ok(api.GET("/stock/jobs/{jobId}/materials", { params: { path: { jobId } } })), [jobId]);
  const requests = useLoad(() => ok(api.GET("/stock/requests", { params: { query: { jobId } } })), [jobId]);
  const act = useAction();
  const cancelAct = useAction();
  const [lines, setLines] = useState<RequestLine[]>([emptyRequestLine()]);
  const [note, setNote] = useState("");
  const [message, setMessage] = useState<string>();

  if (!stock.data) return <Screen>{stock.error ? <Banner text={errorText(t, stock.error)} /> : <Loading />}</Screen>;
  const items = stock.data;
  const byId = new Map(items.map((i) => [i.itemId, i]));
  const set = (i: number, patch: Partial<RequestLine>) => { setMessage(undefined); setLines((ls) => ls.map((l, n) => (n === i ? { ...l, ...patch } : l))); };

  const send = () => act.run(async () => {
    await ok(api.POST("/stock/requests", { body: { jobId, note: note.trim() || null, lines: requestBody(lines) } }));
    setLines([emptyRequestLine()]);
    setNote("");
    setMessage(t("mat.sent"));
    requests.reload();
  });

  const cancel = (id: string) => cancelAct.run(async () => {
    await ok(api.POST("/stock/requests/{id}/cancel", { params: { path: { id } } }));
    setMessage(t("mat.cancelled"));
    requests.reload();
  });

  return (
    <Screen>
      <Txt size="xl" bold>{t("mat.title")}</Txt>

      <Card>
        <Txt bold>{t("mat.issued")}</Txt>
        {issued.data && issued.data.length > 0
          ? issued.data.map((m) => <Txt key={m.itemId}>{t("mat.issuedLine", { item: m.item, used: number(m.used), issued: number(m.issued), unit: m.unit })}</Txt>)
          : <Txt muted>{t("mat.noneIssued")}</Txt>}
      </Card>

      {items.length === 0 ? <Banner text={t("mat.empty")} /> : (
        <Card>
          <Txt bold>{t("mat.open")}</Txt>
          {lines.map((l, i) => {
            const picked = l.itemId ? byId.get(l.itemId) : undefined;
            return (
              <View key={i} style={{ gap: space[1] }}>
                <Choice label={t("mat.item")} value={l.itemId || undefined}
                  options={items.map((it) => ({ value: it.itemId, label: `${it.item} (${number(it.onHand)} ${it.unit})` }))}
                  onChange={(id) => set(i, { itemId: id })} />
                {picked && <Txt size="sm" muted>{t("mat.onHand", { qty: number(picked.onHand), unit: picked.unit })}</Txt>}
                <Field label={t("mat.quantity")} value={l.quantity} onChangeText={(v) => set(i, { quantity: v })} keyboardType="decimal-pad" />
                {lines.length > 1 && <Button title={t("cost.remove")} variant="ghost" onPress={() => setLines((ls) => ls.filter((_, n) => n !== i))} />}
              </View>
            );
          })}
          <Button title={t("mat.addLine")} variant="secondary" onPress={() => setLines((ls) => [...ls, emptyRequestLine()])} />
          <Field label={t("mat.note")} value={note} onChangeText={setNote} multiline numberOfLines={2} />
          {act.error && <Banner tone="danger" text={errorText(t, act.error)} />}
          <Button title={t("mat.send")} loading={act.busy} disabled={!requestIsValid(lines)} onPress={() => void send()} />
        </Card>
      )}

      <Card>
        <Txt bold>{t("mat.requests")}</Txt>
        {requests.data && requests.data.length > 0 ? requests.data.map((r) => (
          <View key={r.id} style={{ gap: space[1], marginTop: space[2] }}>
            <Row><Txt bold style={{ flex: 1 }}>{new Date(r.createdAt).toLocaleDateString()}</Txt><Chip label={t(`mat.status.${r.status}` as Key)} tone={requestTone[r.status] ?? "neutral"} /></Row>
            {r.lines.map((ln) => <Txt key={ln.itemId}>{ln.item}: {number(ln.quantity)} {ln.unit}</Txt>)}
            {r.status === "Rejected" && r.decisionNote && <Txt muted>{t("mat.refused", { reason: r.decisionNote })}</Txt>}
            {r.status === "Open" && <Button title={t("mat.cancel")} variant="ghost" loading={cancelAct.busy} onPress={() => void cancel(r.id)} />}
          </View>
        )) : <Txt muted>{t("mat.noneRequested")}</Txt>}
      </Card>

      {message && <Banner tone="success" text={message} />}
      <Button title={t("back")} variant="ghost" onPress={() => nav.goBack()} />
    </Screen>
  );
}
