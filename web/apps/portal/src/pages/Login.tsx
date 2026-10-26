import { Button, Card, TextField } from "@urbanfix/ui";
import { useState, type FormEvent } from "react";
import { Navigate } from "react-router-dom";
import { ApiError, api, ok, setToken } from "../api";
import { ErrorNote } from "../components";
import { useAction } from "../hooks";
import { useT } from "../i18n";
import { homeFor } from "../roles";
import { useSession } from "../session";

type Step = "credentials" | "enroll";

/** Sign-in with a second factor (WP-01). First-time users enrol an authenticator app before they can continue. */
export function LoginPage() {
  const t = useT();
  const session = useSession();
  const login = useAction();
  const enroll = useAction();
  const [step, setStep] = useState<Step>("credentials");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [needsCode, setNeedsCode] = useState(false);
  const [secret, setSecret] = useState<{ secret: string; uri: string }>();

  if (session.me?.organizationId) return <Navigate to={homeFor(session.role)} replace />;

  async function submit(e: FormEvent) {
    e.preventDefault();
    const result = await login.run(async () => {
      try {
        return await ok(api.POST("/auth/login", { body: { email, password, totpCode: code || null, organizationId: null } }));
      } catch (err) {
        // The server asks for the authenticator code only once the password was right.
        if (err instanceof ApiError && err.code === "totp_required") { setNeedsCode(true); return undefined; }
        throw err;
      }
    });
    if (!result) return;
    if (result.totpEnrollmentRequired) {
      // The session is not verified until the first code is confirmed, so /me would refuse it: keep the token, skip the profile load.
      setToken(result.token);
      const start = await enroll.run(() => ok(api.POST("/auth/totp/enroll")));
      if (start) { setSecret({ secret: start.secret, uri: start.provisioningUri }); setCode(""); setStep("enroll"); }
      return;
    }
    await session.signIn(result.token);
  }

  async function confirm(e: FormEvent) {
    e.preventDefault();
    const done = await enroll.run(async () => { await ok(api.POST("/auth/totp/confirm", { body: { code } })); return true; });
    if (done) await session.refresh();
  }

  return (
    <main className="login">
      <aside className="login__hero">
        <span className="login__logo"><span className="logo-mark" aria-hidden="true">UF</span>{t("app.name")}</span>
        <h1 className="login__tagline">{t("login.tagline")}</h1>
        <ul className="login__points">
          <li>{t("login.point1")}</li>
          <li>{t("login.point2")}</li>
          <li>{t("login.point3")}</li>
        </ul>
      </aside>
      <div className="login__form">
      <Card title={t("login.welcome")}>
        {step === "credentials" ? (
          <form className="stack" onSubmit={submit}>
            <TextField label={t("email")} type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required />
            <TextField label={t("password")} type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
            {needsCode && (
              <TextField label={t("login.code")} hint={t("login.codeHint")} inputMode="numeric" autoComplete="one-time-code" value={code}
                onChange={(e) => setCode(e.target.value.trim())} required />
            )}
            <ErrorNote error={login.error} />
            <Button type="submit" loading={login.busy}>{t("login.signIn")}</Button>
          </form>
        ) : (
          <form className="stack" onSubmit={confirm}>
            <p>{t("login.enrollIntro")}</p>
            <p className="secret" dir="ltr"><code>{secret?.secret}</code></p>
            <p className="muted" dir="ltr">{secret?.uri}</p>
            <TextField label={t("login.code")} inputMode="numeric" autoComplete="one-time-code" value={code} onChange={(e) => setCode(e.target.value.trim())} required />
            <ErrorNote error={enroll.error} />
            <Button type="submit" loading={enroll.busy}>{t("login.confirm")}</Button>
          </form>
        )}
      </Card>
      </div>
    </main>
  );
}
