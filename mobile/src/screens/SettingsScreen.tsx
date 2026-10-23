import Constants from "expo-constants";
import { useI18n, useT } from "../i18n";
import type { Key } from "../i18n/strings";
import { useAction } from "../hooks";
import { viewsOf } from "../roles";
import { useServices } from "../services";
import { useSession } from "../session";
import { space } from "../theme";
import { Banner, Button, Card, Choice, Screen, Txt } from "../ui";
import { View } from "react-native";

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
