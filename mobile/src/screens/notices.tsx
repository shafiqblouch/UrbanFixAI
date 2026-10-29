// The resident's notices feed (RA-09): announcements from the society or office, newest first, with unread ones marked.
import { useFocusEffect } from "@react-navigation/native";
import { useCallback } from "react";
import { api, ok } from "../api";
import { errorText, useAction, useLoad, usePolling } from "../hooks";
import { useT } from "../i18n";
import { Banner, Button, Card, Chip, Empty, Loading, Row, Screen, Txt } from "../ui";
import { useDate } from "./resident";

const loadNotices = () => ok(api.GET("/me/notices"));

/** How many notices the person has not opened, for the tab badge. Checks again every minute while the app is in front. */
export function useUnreadNotices(): number {
  const list = useLoad(loadNotices, []);
  usePolling(list.reload, 60_000);
  return list.data?.filter((n) => !n.readAt).length ?? 0;
}

export function NoticesScreen() {
  const t = useT();
  const f = useDate();
  const list = useLoad(loadNotices, []);
  const mark = useAction();
  usePolling(list.reload);
  useFocusEffect(useCallback(() => { list.reload(); }, [list.reload]));

  const open = (id: string) => void mark.run(async () => {
    await ok(api.POST("/notifications/{id}/read", { params: { path: { id } } }));
    list.reload();
  });
  const unread = list.data?.filter((n) => !n.readAt) ?? [];

  return (
    <Screen>
      {list.error && !list.data && <Banner tone="danger" text={errorText(t, list.error)} />}
      {mark.error && <Banner tone="danger" text={errorText(t, mark.error)} />}
      {unread.length > 1 && (
        <Button title={t("notices.markAll")} variant="secondary" loading={mark.busy}
          onPress={() => void mark.run(async () => { await ok(api.POST("/notifications/read-all")); list.reload(); })} />
      )}
      {list.loading && !list.data ? <Loading /> : list.data && list.data.length > 0
        ? list.data.map((n) => (
          <Card key={n.id} onPress={n.readAt ? undefined : () => open(n.id)}>
            <Row>
              <Txt bold style={{ flex: 1 }}>{n.title}</Txt>
              {!n.readAt && <Chip label={t("notices.unread")} tone="info" />}
            </Row>
            <Txt>{n.body}</Txt>
            <Txt size="sm" muted>{f(n.createdAt)}</Txt>
          </Card>
        ))
        : !list.error && <Empty text={t("notices.empty")} />}
    </Screen>
  );
}
