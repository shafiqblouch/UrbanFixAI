import { Alert, Button } from "@urbanfix/ui";
import { useEffect, useState } from "react";
import { api, ok } from "./api";
import { ErrorNote } from "./components";
import { useAction } from "./hooks";
import { useT } from "./i18n";

const DEVICE_KEY = "uf.push.device";

const supported = () => typeof window !== "undefined" && "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;

const bytes = (base64Url: string): Uint8Array => {
  const padded = base64Url.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(base64Url.length / 4) * 4, "=");
  return Uint8Array.from(atob(padded), (c) => c.charCodeAt(0));
};

const readDevice = (): string | null => { try { return localStorage.getItem(DEVICE_KEY); } catch { return null; } };
const writeDevice = (id: string | null) => { try { id ? localStorage.setItem(DEVICE_KEY, id) : localStorage.removeItem(DEVICE_KEY); } catch { /* storage unavailable */ } };

/** Turn notifications on for this browser, off again, or send a test to every device (FD-16). Shown in the notifications panel. */
export function PushSettings() {
  const t = useT();
  const action = useAction();
  const [device, setDevice] = useState<string | null>(readDevice);
  const [available, setAvailable] = useState(false);
  const [result, setResult] = useState<string>();

  useEffect(() => {
    if (!supported()) return;
    ok(api.GET("/push/config")).then((c) => setAvailable(c.web && !!c.webPublicKey), () => setAvailable(false));
  }, []);

  if (!supported() || !available) return null;

  const turnOn = () => action.run(async () => {
    setResult(undefined);
    const config = await ok(api.GET("/push/config"));
    if (await Notification.requestPermission() !== "granted") throw Object.assign(new Error("denied"), { code: "push_permission_denied" });
    const registration = await navigator.serviceWorker.register("/sw.js");
    await navigator.serviceWorker.ready;
    const subscription = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: bytes(config.webPublicKey!) as BufferSource });
    const json = subscription.toJSON();
    const saved = await ok(api.POST("/me/push-devices", {
      body: { kind: "web", token: subscription.endpoint, keys: { p256dh: json.keys!.p256dh!, auth: json.keys!.auth! }, label: browserName() },
    }));
    writeDevice(saved.id);
    setDevice(saved.id);
  });

  const turnOff = () => action.run(async () => {
    setResult(undefined);
    const registration = await navigator.serviceWorker.getRegistration("/sw.js");
    await (await registration?.pushManager.getSubscription())?.unsubscribe();
    if (device) await api.DELETE("/me/push-devices/{id}", { params: { path: { id: device } } });
    writeDevice(null);
    setDevice(null);
  });

  const test = () => action.run(async () => {
    const r = await ok(api.POST("/me/push-test"));
    setResult(t("push.testResult", { sent: r.sent, failed: r.failed + r.gone }));
  });

  return (
    <section className="stack" aria-label={t("push.title")}>
      <strong>{t("push.title")}</strong>
      <p className="muted">{device ? t("push.on") : t("push.off")}</p>
      <ErrorNote error={action.error} />
      {result && <Alert tone="info">{result}</Alert>}
      <div className="row wrap">
        {device ? <Button variant="secondary" onClick={() => void turnOff()} loading={action.busy}>{t("push.turnOff")}</Button> : <Button onClick={() => void turnOn()} loading={action.busy}>{t("push.turnOn")}</Button>}
        {device && <Button variant="ghost" onClick={() => void test()} loading={action.busy}>{t("push.test")}</Button>}
      </div>
    </section>
  );
}

function browserName(): string {
  const ua = navigator.userAgent;
  return /Edg\//.test(ua) ? "Edge" : /Chrome\//.test(ua) ? "Chrome" : /Firefox\//.test(ua) ? "Firefox" : /Safari\//.test(ua) ? "Safari" : "Browser";
}
