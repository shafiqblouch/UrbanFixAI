import { useLocale } from "@urbanfix/ui";
import { useState, type FormEvent } from "react";
import { Navigate } from "react-router-dom";
import { ApiError, api, ok, setToken } from "../api";
import { ErrorNote } from "../components";
import { useAction } from "../hooks";
import { Icon } from "../icons";
import { saveLocale, useT } from "../i18n";
import { homeFor } from "../roles";
import { useSession } from "../session";

type Step = "credentials" | "enroll";

/** Sign-in with a second factor (WP-01). First-time users enrol an authenticator app before they can continue. */
export function LoginPage({ onLocale }: { onLocale: (l: "en" | "ur") => void }) {
  const t = useT();
  const { locale } = useLocale();
  const session = useSession();
  const login = useAction();
  const enroll = useAction();
  const [step, setStep] = useState<Step>("credentials");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [needsCode, setNeedsCode] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
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

  function switchLocale() {
    const next = locale === "en" ? "ur" : "en";
    saveLocale(next);
    onLocale(next);
  }

  async function confirm(e: FormEvent) {
    e.preventDefault();
    const done = await enroll.run(async () => { await ok(api.POST("/auth/totp/confirm", { body: { code } })); return true; });
    if (done) await session.refresh();
  }

  return (
    <main className="login">
      <section className="login__hero">
        <div className="login__brand">
          <span className="logo-mark logo-mark--lg" aria-hidden="true">UF</span>
          <span><strong className="login__logo">{t("app.name")}</strong><small className="login__brand-sub">{t("login.brandSub")}</small></span>
        </div>
        <div className="login__pitch">
          <span className="login__badge"><Icon name="audit" size={14} />{t("login.badge")}</span>
          <h1 className="login__tagline">{t("login.headline1")}<br /><span className="login__tagline-accent">{t("login.headline2")}</span></h1>
          <ul className="login__points">
            <li>{t("login.point1")}</li>
            <li>{t("login.point2")}</li>
            <li>{t("login.point3")}</li>
          </ul>
          <div className="login__sample" aria-hidden="true">
            <div className="login__sample-head">
              <span className="login__sample-icon"><Icon name="road" size={18} /></span>
              <span className="login__sample-title"><strong>{t("login.sampleTitle")}</strong><small>{t("login.sampleMeta")}</small></span>
              <span className="login__sample-status">{t("login.sampleStatus")}</span>
            </div>
            <div className="login__sample-bar"><span>{t("login.sampleStep")}</span><b>75%</b></div>
            <div className="login__sample-track"><span /></div>
            <div className="login__sample-foot">
              <span><Icon name="clock" size={13} />{t("login.sampleAssigned")}</span>
              <span className="login__sample-ok"><Icon name="camera" size={13} />{t("login.samplePhoto")}</span>
            </div>
          </div>
        </div>
        <p className="login__footer">{t("login.footer")}</p>
      </section>

      <section className="login__form">
        <button type="button" className="login__lang" onClick={switchLocale} aria-label={t("language")}>
          <Icon name="globe" size={15} />{locale === "en" ? "اردو (Urdu)" : "English"}
        </button>
        <div className="login__panel">
          {step === "credentials" && (
            <div className="login__tabs" role="tablist">
              <button type="button" role="tab" aria-selected="true" className="login__tab login__tab--on"><Icon name="logout" size={14} />{t("login.tabSignIn")}</button>
              <button type="button" role="tab" aria-selected="false" aria-disabled="true" title={t("login.soon")} className="login__tab"><Icon name="new" size={14} />{t("login.tabRegister")}</button>
            </div>
          )}
          <h2 className="login__title">{t("login.welcome")}</h2>
          <p className="login__subtitle">{t("login.subtitle")}</p>
          {step === "credentials" ? (
            <form className="login__fields" onSubmit={submit}>
              <div className="login__field">
                <label htmlFor="login-email">{t("email")}</label>
                <div className="login__input">
                  <span className="login__input-icon"><Icon name="mail" size={17} /></span>
                  <input id="login-email" type="email" autoComplete="username" placeholder="name@domain.com" value={email} onChange={(e) => setEmail(e.target.value)} required />
                </div>
              </div>
              <div className="login__field">
                <div className="login__label-row">
                  <label htmlFor="login-password">{t("password")}</label>
                  <button type="button" className="login__link" aria-disabled="true" title={t("login.soon")}>{t("login.forgot")}</button>
                </div>
                <div className="login__input">
                  <span className="login__input-icon"><Icon name="lock" size={17} /></span>
                  <input id="login-password" type={showPassword ? "text" : "password"} autoComplete="current-password" placeholder="••••••••••••" value={password} onChange={(e) => setPassword(e.target.value)} required />
                  <button type="button" className="login__input-toggle" onClick={() => setShowPassword((v) => !v)} aria-label={showPassword ? t("login.hide") : t("login.show")}>
                    <Icon name={showPassword ? "eyeoff" : "eye"} size={17} />
                  </button>
                </div>
              </div>
              {needsCode && (
                <div className="login__field">
                  <label htmlFor="login-code">{t("login.code")}</label>
                  <div className="login__input">
                    <span className="login__input-icon"><Icon name="audit" size={17} /></span>
                    <input id="login-code" inputMode="numeric" autoComplete="one-time-code" value={code} onChange={(e) => setCode(e.target.value.trim())} required />
                  </div>
                  <small className="login__hint">{t("login.codeHint")}</small>
                </div>
              )}
              <label className="login__remember"><input type="checkbox" />{t("login.remember")}</label>
              <ErrorNote error={login.error} />
              <button type="submit" className="login__submit" disabled={login.busy}>{t("login.signIn")}<Icon name="arrow" size={15} /></button>
              <p className="login__guest">{t("login.guestQ")}<br /><button type="button" aria-disabled="true" title={t("login.soon")}><Icon name="new" size={13} />{t("login.guest")}</button></p>
            </form>
          ) : (
            <form className="login__fields" onSubmit={confirm}>
              <p>{t("login.enrollIntro")}</p>
              <p className="secret" dir="ltr"><code>{secret?.secret}</code></p>
              <p className="login__hint" dir="ltr">{secret?.uri}</p>
              <div className="login__field">
                <label htmlFor="login-code">{t("login.code")}</label>
                <div className="login__input">
                  <span className="login__input-icon"><Icon name="audit" size={17} /></span>
                  <input id="login-code" inputMode="numeric" autoComplete="one-time-code" value={code} onChange={(e) => setCode(e.target.value.trim())} required />
                </div>
              </div>
              <ErrorNote error={enroll.error} />
              <button type="submit" className="login__submit" disabled={enroll.busy}>{t("login.confirm")}<Icon name="arrow" size={15} /></button>
            </form>
          )}
        </div>
      </section>
    </main>
  );
}
