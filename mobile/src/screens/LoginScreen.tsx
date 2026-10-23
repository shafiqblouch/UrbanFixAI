import { useEffect, useState } from "react";
import { ApiError, api, ok, setToken } from "../api";
import { errorText, useAction } from "../hooks";
import { useT } from "../i18n";
import { normalizePhone, resendWait } from "../phone";
import { useSession } from "../session";
import { Banner, Button, Card, Field, Screen, Txt } from "../ui";

/**
 * Sign-in. Residents use a code sent to their phone (RA-04); staff use email, password and an authenticator code.
 * First-time staff enrol an authenticator app before they can continue.
 */
export function LoginScreen() {
  const [mode, setMode] = useState<"resident" | "staff">("resident");
  const t = useT();
  return (
    <Screen>
      <Txt size="xxl" bold>{t("app.name")}</Txt>
      {mode === "resident" ? <PhoneSignIn /> : <StaffSignIn />}
      <Button title={mode === "resident" ? t("login.staff") : t("login.resident")} variant="ghost" onPress={() => setMode(mode === "resident" ? "staff" : "resident")} />
    </Screen>
  );
}

function PhoneSignIn() {
  const t = useT();
  const session = useSession();
  const request = useAction();
  const verify = useAction();
  const [phone, setPhone] = useState("");
  const [code, setCode] = useState("");
  const [sentAt, setSentAt] = useState<number>();
  const [now, setNow] = useState(Date.now());
  const normalized = normalizePhone(phone);

  // Ticks once a second while a code is waiting, so the "send again" button counts down.
  useEffect(() => {
    if (sentAt === undefined) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [sentAt]);
  const wait = resendWait(sentAt, now);

  async function send() {
    if (!normalized) return;
    const done = await request.run(async () => { await ok(api.POST("/auth/otp/request", { body: { phone: normalized } })); return true; });
    if (done) { setSentAt(Date.now()); setNow(Date.now()); setCode(""); }
  }

  async function signIn() {
    if (!normalized) return;
    const result = await verify.run(() => ok(api.POST("/auth/otp/verify", { body: { phone: normalized, code: code.trim(), organizationId: null } })));
    if (result) await session.signIn(result.token);
  }

  return (
    <Card>
      <Txt size="lg" bold>{t("login.resident")}</Txt>
      <Field label={t("login.phone")} hint={t("login.phoneHint")} value={phone} onChangeText={setPhone} keyboardType="phone-pad" autoComplete="tel"
        editable={sentAt === undefined} error={phone && !normalized ? t("error.invalid_phone") : undefined} />
      {sentAt === undefined ? (
        <>
          {request.error && <Banner tone="danger" text={errorText(t, request.error)} />}
          <Button title={t("login.sendCode")} onPress={() => void send()} loading={request.busy} disabled={!normalized} />
        </>
      ) : (
        <>
          <Banner tone="info" text={t("login.otpSent")} />
          <Field label={t("login.otpCode")} value={code} onChangeText={(v) => setCode(v.replace(/\D/g, ""))} keyboardType="number-pad" autoComplete="sms-otp" maxLength={6} />
          {verify.error && <Banner tone="danger" text={errorText(t, verify.error)} />}
          {request.error && <Banner tone="danger" text={errorText(t, request.error)} />}
          <Button title={t("login.signIn")} onPress={() => void signIn()} loading={verify.busy} disabled={code.length !== 6} />
          <Button title={wait > 0 ? t("login.resendIn", { seconds: wait }) : t("login.resend")} variant="secondary" onPress={() => void send()} loading={request.busy} disabled={wait > 0} />
          <Button title={t("login.changeNumber")} variant="ghost" onPress={() => { setSentAt(undefined); setCode(""); verify.clear(); request.clear(); }} />
        </>
      )}
    </Card>
  );
}

function StaffSignIn() {
  const t = useT();
  const session = useSession();
  const login = useAction();
  const enroll = useAction();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [needsCode, setNeedsCode] = useState(false);
  const [secret, setSecret] = useState<string>();

  async function submit() {
    const result = await login.run(async () => {
      try {
        return await ok(api.POST("/auth/login", { body: { email: email.trim(), password, totpCode: code || null, organizationId: null } }));
      } catch (e) {
        // The server asks for the authenticator code only once the password was right.
        if (e instanceof ApiError && e.code === "totp_required") { setNeedsCode(true); return undefined; }
        throw e;
      }
    });
    if (!result) return;
    if (result.totpEnrollmentRequired) {
      // Not verified until the first code is confirmed, so /me would refuse it: keep the token, skip the profile load.
      await setToken(result.token);
      const start = await enroll.run(() => ok(api.POST("/auth/totp/enroll")));
      if (start) { setSecret(start.secret); setCode(""); }
      return;
    }
    await session.signIn(result.token);
  }

  async function confirm() {
    const done = await enroll.run(async () => { await ok(api.POST("/auth/totp/confirm", { body: { code } })); return true; });
    if (done) await session.refresh();
  }

  return (
    <Card>
      {secret === undefined ? (
        <>
          <Txt size="lg" bold>{t("login.title")}</Txt>
          <Field label={t("email")} value={email} onChangeText={setEmail} autoCapitalize="none" keyboardType="email-address" autoComplete="username" />
          <Field label={t("password")} value={password} onChangeText={setPassword} secureTextEntry autoComplete="current-password" />
          {needsCode && <Field label={t("login.code")} hint={t("login.codeHint")} value={code} onChangeText={(v) => setCode(v.trim())} keyboardType="number-pad" autoComplete="one-time-code" maxLength={6} />}
          {login.error && <Banner tone="danger" text={errorText(t, login.error)} />}
          <Button title={t("login.signIn")} onPress={() => void submit()} loading={login.busy} disabled={!email || !password} />
        </>
      ) : (
        <>
          <Txt>{t("login.enroll")}</Txt>
          <Txt size="lg" bold selectable style={{ writingDirection: "ltr", textAlign: "left" }}>{secret}</Txt>
          <Field label={t("login.code")} value={code} onChangeText={(v) => setCode(v.trim())} keyboardType="number-pad" autoComplete="one-time-code" maxLength={6} />
          {enroll.error && <Banner tone="danger" text={errorText(t, enroll.error)} />}
          <Button title={t("login.confirm")} onPress={() => void confirm()} loading={enroll.busy} disabled={code.length !== 6} />
        </>
      )}
    </Card>
  );
}
