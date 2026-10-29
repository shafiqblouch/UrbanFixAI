import { Platform } from "react-native";
import Constants, { ExecutionEnvironment } from "expo-constants";
import { requireOptionalNativeModule } from "expo-modules-core";
import { api, ok, type S } from "../api";

type Notifications = typeof import("expo-notifications");

/**
 * The notifications module is native code that Expo Go does not contain, and importing it there fails the whole app.
 * So it is loaded on first use, and without it push simply reports "unavailable" (a real build has it).
 */
let loaded: Notifications | null | undefined;
function native(): Notifications | null {
  if (loaded !== undefined) return loaded;
  // A failed require still raises the red error screen in development even when caught, so Expo Go is ruled out first:
  // by the environment, and by asking whether the module's native half is there at all (it is not in every Expo Go build).
  if (Constants.executionEnvironment === ExecutionEnvironment.StoreClient || !requireOptionalNativeModule("ExpoTopicSubscriptionModule")) return (loaded = null);
  try {
    loaded = require("expo-notifications") as Notifications;
    // Show a message that arrives while the app is open as a banner, so a resident does not miss it.
    loaded.setNotificationHandler({
      handleNotification: async () => ({ shouldShowBanner: true, shouldShowList: true, shouldPlaySound: false, shouldSetBadge: false }),
    });
  } catch {
    loaded = null;
  }
  return loaded;
}

export type PushState = "on" | "off" | "denied" | "unavailable";

/** The phone's push address (an FCM token on Android), or why there is none. */
async function deviceToken(ask: boolean): Promise<{ token: string } | { state: PushState }> {
  const Notifications = native();
  if (!Notifications || Platform.OS !== "android") return { state: "unavailable" };
  let { status } = await Notifications.getPermissionsAsync();
  if (status !== "granted" && ask) status = (await Notifications.requestPermissionsAsync()).status;
  if (status !== "granted") return { state: "denied" };
  await Notifications.setNotificationChannelAsync("default", { name: "UrbanFix", importance: Notifications.AndroidImportance.DEFAULT });
  try {
    const t = await Notifications.getDevicePushTokenAsync();
    return typeof t.data === "string" && t.data ? { token: t.data } : { state: "unavailable" };
  } catch {
    return { state: "unavailable" }; // no Google services, or the app was built without a Firebase config
  }
}

/** Asks permission (when `ask`) and tells the server about this phone. The server turns the push channel on for the person. */
export async function registerForPush(ask: boolean): Promise<PushState> {
  const got = await deviceToken(ask);
  if ("state" in got) return got.state;
  try {
    await ok(api.POST("/me/push-devices", { body: { kind: "fcm", token: got.token, label: "Phone" } as S["RegisterDevice"] }));
    return "on";
  } catch {
    return "unavailable"; // the server has no push set up, or no signal; the next sign-in tries again
  }
}

/** Calls `open` with the link of each notification the person taps, including the one that started the app. Returns a stop function. */
export function onNotificationTap(open: (link: string | undefined) => void): () => void {
  const Notifications = native();
  if (!Notifications) return () => {};
  const link = (r: import("expo-notifications").NotificationResponse) => {
    const v = r.notification.request.content.data?.link;
    return typeof v === "string" && v ? v : undefined;
  };
  void Notifications.getLastNotificationResponseAsync().then((r) => { if (r) open(link(r)); });
  const sub = Notifications.addNotificationResponseReceivedListener((r) => open(link(r)));
  return () => sub.remove();
}
