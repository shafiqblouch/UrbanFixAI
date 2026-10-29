import Constants from "expo-constants";
import { useI18n, useT } from "../i18n";
import type { Key } from "../i18n/strings";
import { useAction } from "../hooks";
import { viewsOf } from "../roles";
import { useServices } from "../services";
import { useSession } from "../session";
import { space } from "../theme";
import { Banner, Button, Card, Choice, Screen, Txt } from "../ui";
import { useCallback, useEffect, useState } from "react";
import { View } from "react-native";
import { api, ok } from "../api";
import { registerForPush, type PushState } from "../platform/push";
import { ApiError } from "../api";

/** Whether this phone gets push messages, with a button to turn them on and one to send a test (MA-04). */
function PushCard() {
  const t = useT();
  const [state, setState] = useState<PushState>("off");
  const [note, setNote] = useState<{ text: string; tone: "info" | "danger" }>();
  const action = useAction();
  // Asking quietly on open: if permission was already given this just re-registers the phone's current address.
  useEffect(() => { void registerForPush(false).then(setState); }, []);
  const enable = useCallback(() => void action.run(async () => setState(await registerForPush(true))), [action]);
  const test = () => void action.run(async () => {
    setNote(undefined);
    try {
      const r = await ok(api.POST("/me/push-test"));
      setNote(r.sent > 0 ? { text: t("push.testSent"), tone: "info" } : { text: t("push.testFailed"), tone: "danger" });
    } catch (e) {
      setNote({ text: e instanceof ApiError && e.code === "no_push_device" ? t("push.testNone") : t("push.testFailed"), tone: "danger" });
    }
  });
  return (
    <Card>
      <Txt bold>{t("push.title")}</Txt>
      <Txt muted>{t(`push.${state}` as Key)}</Txt>
      {note && <Banner tone={note.tone} text={note.text} />}
      {state === "on"
        ? <Button title={t("push.test")} variant="secondary" loading={action.busy} onPress={test} />
        : state !== "unavailable" && state !== "denied" && <Button title={t("push.enable")} loading={action.busy} onPress={enable} />}
    </Card>
  );
}

/** Language, the role switcher, upload status and sign-out. */
export function SettingsScreen() {
  const t = useT();
  const { locale, setLocale } = useI18n();
  const { me, view, signOut, switchView } = useSession();
  const { waiting, failed, uploading, drain, queue } = useServices();
  const switching = useAction();
  const views = viewsOf(me?.memberships ?? []);
  const key = (v: { organizationId: string; role: string }) => `${v.organizationId}|${v.role}`;

  return (
    <Screen>
      <Card>
        <Choice label={t("settings.language")} value={locale} onChange={setLocale}
          options={[{ value: "en", label: "English" }, { value: "ur", label: "اردو" }]} />
      </Card>

      {views.length > 1 && (
        <Card>
          <Choice label={t("settings.view")} value={view ? key(view) : undefined}
            options={views.map((v) => ({ value: key(v), label: `${t(`role.${v.role}` as Key)} · ${v.organizationName}` }))}
            onChange={(k) => { const next = views.find((v) => key(v) === k); if (next) void switching.run(() => switchView(next)); }} />
        </Card>
      )}
      {views.length === 1 && view && <Txt muted>{t("settings.view")}: {t(`role.${view.role}` as Key)} · {view.organizationName}</Txt>}

      <PushCard />

      <Card>
        <Txt>{t("settings.queue", { count: waiting })}</Txt>
        {failed > 0 && <Banner text={t("settings.failedUploads", { count: failed })} />}
        <View style={{ gap: space[2] }}>
          <Button title={t("settings.retryUploads")} variant="secondary" loading={uploading} disabled={waiting === 0 && failed === 0}
            onPress={() => void queue.retryFailed().then(drain)} />
        </View>
      </Card>

      <Button title={t("settings.signOut")} variant="danger" onPress={() => void signOut()} />
      <Txt size="xs" muted>{t("settings.version", { version: Constants.expoConfig?.version ?? "0.1.0" })}</Txt>
    </Screen>
  );
}
