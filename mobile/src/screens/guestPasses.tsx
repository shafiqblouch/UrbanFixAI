import { useCallback, useState } from "react";
import { View } from "react-native";
import { api, ok, type S } from "../api";
import { errorText, useAction, useLoad, useRefetchOnFocus } from "../hooks";
import { useT } from "../i18n";
import type { Key } from "../i18n/strings";
import { space } from "../theme";
import { Banner, Button, Card, Chip, Choice, Empty, Field, Loading, Row, Screen, Txt } from "../ui";

type Pass = S["PassDto"];
type House = S["MyHouseDto"];
type Kind = "visitor" | "guest" | "delivery";

const stateTone: Record<string, "success" | "warning" | "danger" | "neutral"> = {
  Valid: "success", NotYetValid: "neutral", Pending: "warning", Expired: "danger", UsedUp: "danger", Cancelled: "danger", Denied: "danger",
};

/** Residents pre-authorise their own visitors (GT-03) and answer "someone is at the gate for you" (GT-04). */
export function GatePassesScreen() {
  const t = useT();
  const requests = useLoad<Pass[]>(() => ok(api.GET("/my/gate-visit-requests", {})), []);
  const passes = useLoad<Pass[]>(() => ok(api.GET("/my/gate-passes", {})), []);
  const houses = useLoad<House[]>(() => ok(api.GET("/my/houses", {})), []);
  const act = useAction();
  const [message, setMessage] = useState<string>();
  // Must be stable: useRefetchOnFocus feeds this to useFocusEffect, so a fresh function each render would
  // re-fire the focus effect every render and spin into an endless refetch loop (which also wedged the create button).
  // requests.reload / passes.reload are already stable (memoised in useLoad), so this stays stable too.
  const reloadAll = useCallback(() => { requests.reload(); passes.reload(); }, [requests.reload, passes.reload]);
  useRefetchOnFocus(reloadAll);

  const decide = (id: string, approve: boolean) => void act.run(async () => {
    await ok(api.POST("/my/gate-visit-requests/{id}/decide", { params: { path: { id } }, body: { approve, note: null } }));
    setMessage(t(approve ? "guest.allowed" : "guest.denied"));
    reloadAll();
  });

  const cancel = (id: string) => void act.run(async () => {
    await ok(api.POST("/my/gate-passes/{id}/cancel", { params: { path: { id } } }));
    passes.reload();
  });

  return (
    <Screen>
      {message && <Banner tone="success" text={message} />}
      {act.error && <Banner tone="danger" text={errorText(t, act.error)} />}

      {/* Someone waiting at the gate right now. */}
      {(requests.data?.length ?? 0) > 0 && (
        <>
          <Txt size="lg" bold>{t("guest.atGate")}</Txt>
          {requests.data!.map((r) => (
            <Card key={r.id}>
              <Txt bold size="lg">{r.visitorName}</Txt>
              <Txt muted>{t(`guest.kind.${r.kind}` as Key)}{r.houseLabel ? ` · ${r.houseLabel}` : ""}</Txt>
              {r.vehiclePlate && <Txt muted>{r.vehiclePlate}</Txt>}
              <Row gap={space[2]}>
                <View style={{ flex: 1 }}><Button title={t("guest.allow")} onPress={() => decide(r.id, true)} /></View>
                <View style={{ flex: 1 }}><Button title={t("guest.deny")} variant="danger" onPress={() => decide(r.id, false)} /></View>
              </Row>
            </Card>
          ))}
        </>
      )}

      <NewPass houses={houses.data ?? []} loading={houses.loading} onCreated={(code) => { setMessage(t("guest.created", { code })); passes.reload(); }} />

      <Txt size="lg" bold>{t("guest.myPasses")}</Txt>
      {passes.loading && !passes.data && <Loading />}
      {passes.data?.length === 0 && <Empty text={t("guest.noPasses")} />}
      {passes.data?.map((p) => (
        <Card key={p.id}>
          <Row style={{ justifyContent: "space-between" }}>
            <Txt bold size="lg">{p.visitorName}</Txt>
            <Chip tone={stateTone[p.state] ?? "neutral"} label={t(`gate.pass.${p.state}` as Key)} />
          </Row>
          <Txt muted>{t("guest.code")}: {p.code}{p.houseLabel ? ` · ${p.houseLabel}` : ""}</Txt>
          {p.vehiclePlate && <Txt muted>{p.vehiclePlate}</Txt>}
          {(p.status === "active" || p.status === "pending") && (
            <Button title={t("guest.cancel")} variant="secondary" onPress={() => cancel(p.id)} />
          )}
        </Card>
      ))}
    </Screen>
  );
}

/** Create a pass for one of the resident's houses. If they have only one, it is used automatically. */
function NewPass({ houses, loading, onCreated }: { houses: House[]; loading: boolean; onCreated: (code: string) => void }) {
  const t = useT();
  const act = useAction();
  const [name, setName] = useState("");
  const [plate, setPlate] = useState("");
  const [kind, setKind] = useState<Kind>("guest");
  const [houseId, setHouseId] = useState<string>();
  const house = houses.length === 1 ? houses[0].id : houseId;
  const canSave = name.trim().length > 0 && !!house && !act.busy;

  const submit = () => void act.run(async () => {
    // Fail fast instead of spinning forever if the server never answers (e.g. a stalled POST through the proxy).
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 20_000);
    try {
      const created = await ok(api.POST("/my/gate-passes", {
        body: { visitorName: name.trim(), phone: null, vehiclePlate: plate.trim() || null, houseId: house!, kind, purpose: null, validFrom: null, validTo: null, maxUses: null },
        signal: controller.signal,
      }));
      setName(""); setPlate("");
      onCreated(created.code);
    } finally {
      clearTimeout(timer);
    }
  });

  if (loading) return <Loading />;
  if (houses.length === 0) return null; // not linked to a house yet

  return (
    <Card>
      <Txt size="lg" bold>{t("guest.create")}</Txt>
      <Field label={t("guest.visitorName")} value={name} onChangeText={setName} placeholder={t("guest.visitorNamePlaceholder")} />
      <Field label={t("guest.plateOptional")} value={plate} onChangeText={setPlate} autoCapitalize="characters" autoCorrect={false} />
      <Choice<Kind>
        label={t("guest.kind")}
        value={kind}
        onChange={setKind}
        options={[{ value: "guest", label: t("guest.kind.guest") }, { value: "visitor", label: t("guest.kind.visitor") }, { value: "delivery", label: t("guest.kind.delivery") }]}
      />
      {houses.length > 1 && (
        <Choice<string>
          label={t("guest.house")}
          value={house}
          onChange={setHouseId}
          options={houses.map((h) => ({ value: h.id, label: h.label || h.id }))}
        />
      )}
      {act.error && <Banner tone="danger" text={errorText(t, act.error)} />}
      <Button title={t("guest.createButton")} onPress={submit} disabled={!canSave} loading={act.busy} />
    </Card>
  );
}
