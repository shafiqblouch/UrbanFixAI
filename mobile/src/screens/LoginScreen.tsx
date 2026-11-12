import { useEffect, useRef, useState, type ComponentRef, type ReactNode } from "react";
import { Pressable, ScrollView, TextInput, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { ApiError, api, ok, setToken } from "../api";
import { errorText, useAction } from "../hooks";
import { useI18n, useT } from "../i18n";
import { normalizePhone, resendWait } from "../phone";
import { useSession } from "../session";
import { fontSize, radius, space, TOUCH, usePalette } from "../theme";
import { Banner, Button, Card, Field, Gradient, Row, Txt } from "../ui";

/**
 * Sign-in. Residents use a code sent to their phone (RA-04); staff use email, password and an authenticator code.
 * First-time staff enrol an authenticator app before they can continue.
 */
export function LoginScreen() {
  const [mode, setMode] = useState<"resident" | "staff">("resident");
  if (mode === "resident") return <PhoneSignIn onStaff={() => setMode("staff")} />;
  return <StaffSignIn onResident={() => setMode("resident")} />;
}

/** The teal→blue brand hero shown above the sign-in card, with the app logo and tagline. */
function LogoHero() {
  const t = useT();
  const insets = useSafeAreaInsets();
  return (
    <Gradient style={{ paddingTop: insets.top + space[5], paddingBottom: space[6], paddingHorizontal: space[4], alignItems: "center", gap: space[2] }}>
      <View style={{ width: 64, height: 64, borderRadius: 20, backgroundColor: "rgba(255,255,255,0.2)", alignItems: "center", justifyContent: "center" }}>
        <Txt color="#FFFFFF" style={{ fontSize: 30 }}>📍</Txt>
      </View>
      <Txt size="xxl" bold color="#FFFFFF">{t("app.name")}</Txt>
      <Txt color="#E7F3FF">{t("app.tagline")}</Txt>
    </Gradient>
  );
}

/** A soft rounded pill label, e.g. "RESIDENT PORTAL". */
function Pill({ text }: { text: string }) {
  const p = usePalette();
  return (
    <View style={{ alignSelf: "flex-start", backgroundColor: p.primarySoft, borderRadius: radius.pill, paddingHorizontal: space[3], paddingVertical: space[1] }}>
      <Txt size="sm" bold color={p.onPrimarySoft} style={{ letterSpacing: 0.5 }}>{text}</Txt>
    </View>
  );
}

/** The outer layout shared by every sign-in step: a gradient hero, a floating card and a footer. */
function AuthLayout({ hero, children, footer }: { hero: ReactNode; children: ReactNode; footer?: ReactNode }) {
  const p = usePalette();
  return (
    <View style={{ flex: 1, backgroundColor: p.bg }}>
      <ScrollView keyboardShouldPersistTaps="handled" contentContainerStyle={{ flexGrow: 1, paddingBottom: space[6] }}>
        {hero}
        <View style={{ padding: space[4], gap: space[4], marginTop: -space[4] }}>
          <Card style={{ gap: space[3], padding: space[5] }}>{children}</Card>
          {footer}
        </View>
      </ScrollView>
    </View>
  );
}

function PhoneSignIn({ onStaff }: { onStaff: () => void }) {
  const t = useT();
  const p = usePalette();
  const insets = useSafeAreaInsets();
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

  const staffToggle = (
    <Pressable accessibilityRole="button" onPress={onStaff} style={{ alignSelf: "center", flexDirection: "row", alignItems: "center", gap: space[2], borderWidth: 1.5, borderColor: p.borderSoft, backgroundColor: p.surfaceRaised, borderRadius: radius.pill, paddingHorizontal: space[4], minHeight: TOUCH }}>
      <Txt color={p.success}>✓</Txt>
      <Txt bold>{t("login.staffAdmin")}</Txt>
    </Pressable>
  );
  const footerNote = <Txt size="sm" muted style={{ textAlign: "center" }}>{t("login.protectedBy")}</Txt>;

  // The verification step: a dedicated hero with a back arrow, then the code entry card.
  if (sentAt !== undefined) {
    const verifyHero = (
      <Gradient style={{ paddingTop: insets.top + space[3], paddingBottom: space[5], paddingHorizontal: space[4] }}>
        <Row gap={space[3]}>
          <Pressable accessibilityRole="button" accessibilityLabel={t("back")} onPress={() => { setSentAt(undefined); setCode(""); verify.clear(); request.clear(); }} style={{ width: 40, height: 40, borderRadius: 20, backgroundColor: "rgba(255,255,255,0.2)", alignItems: "center", justifyContent: "center" }}>
            <Txt color="#FFFFFF" size="lg" bold>←</Txt>
          </Pressable>
          <View style={{ flex: 1 }}>
            <Txt size="xl" bold color="#FFFFFF">{t("login.verifyTitle")}</Txt>
            <Txt color="#E7F3FF">{t("login.verifySubtitle")}</Txt>
          </View>
        </Row>
      </Gradient>
    );
    return (
      <AuthLayout hero={verifyHero} footer={<View style={{ alignItems: "center", gap: space[3] }}>{staffToggle}</View>}>
        {/* Sent-to summary with an Edit shortcut back to the number field. */}
        <View style={{ flexDirection: "row", alignItems: "center", gap: space[3], backgroundColor: p.surface, borderRadius: radius.md, padding: space[3] }}>
          <Txt style={{ fontSize: 22 }}>📱</Txt>
          <View style={{ flex: 1 }}>
            <Txt size="sm" bold muted style={{ letterSpacing: 0.5 }}>{t("login.sentTo")}</Txt>
            <Txt size="lg" bold style={{ writingDirection: "ltr" }}>{normalized}</Txt>
          </View>
          <Pressable accessibilityRole="button" onPress={() => { setSentAt(undefined); setCode(""); verify.clear(); request.clear(); }} style={{ flexDirection: "row", alignItems: "center", gap: 4, minHeight: TOUCH, paddingHorizontal: space[2] }}>
            <Txt color={p.link}>✎</Txt>
            <Txt bold color={p.link}>{t("login.edit")}</Txt>
          </Pressable>
        </View>

        <Banner tone="info" text={t("login.smsValid")} />

        <Txt bold>{t("login.enterCode")}</Txt>
        <OtpInput value={code} onChange={setCode} autoFocus />
        {verify.error && <Banner tone="danger" text={errorText(t, verify.error)} />}
        {request.error && <Banner tone="danger" text={errorText(t, request.error)} />}
        <Button title={t("login.verifySignIn")} onPress={() => void signIn()} loading={verify.busy} disabled={code.length !== 6} />

        <Row gap={space[2]} style={{ justifyContent: "center" }}>
          <Txt size="sm" muted>{t("login.noSms")}</Txt>
          {wait > 0
            ? <Txt size="sm" bold muted style={{ writingDirection: "ltr" }}>{t("login.resendTimer", { time: mmss(wait) })}</Txt>
            : <Pressable accessibilityRole="button" onPress={() => void send()} disabled={request.busy}><Txt size="sm" bold color={p.link}>{t("login.resend")}</Txt></Pressable>}
        </Row>
      </AuthLayout>
    );
  }

  // The number-entry step.
  return (
    <AuthLayout hero={<LogoHero />} footer={<View style={{ alignItems: "center", gap: space[4] }}>{staffToggle}{footerNote}</View>}>
      <Pill text={t("login.residentPortal")} />
      <Txt size="xxl" bold>{t("login.residentTitle")}</Txt>
      <Txt muted>{t("login.residentSubtitle")}</Txt>

      <Txt bold>{t("login.phone")}</Txt>
      <PhoneInput value={phone} onChangeText={setPhone} />
      <Txt size="sm" muted>{t("login.phoneFormat")}</Txt>
      {phone.length > 0 && !normalized && <Txt size="sm" color={p.danger} bold>{t("error.invalid_phone")}</Txt>}
      {request.error && <Banner tone="danger" text={errorText(t, request.error)} />}
      <Button title={`${t("login.sendVerification")}  →`} onPress={() => void send()} loading={request.busy} disabled={!normalized} />
    </AuthLayout>
  );
}

/** Whole seconds as m:ss, e.g. 51 → "0:51". */
function mmss(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  return `${m}:${String(s).padStart(2, "0")}`;
}

/** The mobile-number field with a fixed +92 country prefix, as in the design. */
function PhoneInput({ value, onChangeText }: { value: string; onChangeText: (v: string) => void }) {
  const p = usePalette();
  const { rtl } = useI18n();
  const [focused, setFocused] = useState(false);
  return (
    <View style={{ flexDirection: rtl ? "row-reverse" : "row", alignItems: "stretch", borderWidth: 2, borderColor: focused ? p.focusRing : p.border, borderRadius: radius.md, backgroundColor: p.surfaceRaised, overflow: "hidden", minHeight: TOUCH }}>
      <View style={{ flexDirection: "row", alignItems: "center", gap: 6, paddingHorizontal: space[3], backgroundColor: p.surface }}>
        <Txt style={{ fontSize: 16 }}>🇵🇰</Txt>
        <Txt bold>+92</Txt>
      </View>
      <TextInput
        value={value}
        onChangeText={onChangeText}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        keyboardType="phone-pad"
        autoComplete="tel"
        placeholder="0300 1234567"
        placeholderTextColor={p.textMuted}
        accessibilityLabel="+92"
        style={{ flex: 1, paddingHorizontal: space[3], color: p.text, fontSize: fontSize.md, textAlign: rtl ? "right" : "left", writingDirection: "ltr" }}
      />
    </View>
  );
}

/** Six single-digit boxes backed by one hidden input, so SMS auto-fill and paste still work. */
function OtpInput({ value, onChange, autoFocus }: { value: string; onChange: (v: string) => void; autoFocus?: boolean }) {
  const p = usePalette();
  const ref = useRef<ComponentRef<typeof TextInput>>(null);
  const active = Math.min(value.length, 5);
  return (
    <Pressable accessibilityRole="none" onPress={() => ref.current?.focus()} style={{ position: "relative" }}>
      <View style={{ flexDirection: "row", gap: space[2], justifyContent: "space-between" }}>
        {Array.from({ length: 6 }).map((_, i) => {
          const char = value[i] ?? "";
          const on = i === active || !!char;
          return (
            <View key={i} style={{ flex: 1, maxWidth: 52, height: 56, borderRadius: radius.md, borderWidth: on ? 2 : 1.5, borderColor: on ? p.primary : p.borderSoft, backgroundColor: p.surfaceRaised, alignItems: "center", justifyContent: "center" }}>
              <Txt size="xl" bold>{char}</Txt>
            </View>
          );
        })}
      </View>
      <TextInput
        ref={ref}
        value={value}
        onChangeText={(v) => onChange(v.replace(/\D/g, "").slice(0, 6))}
        keyboardType="number-pad"
        autoComplete="sms-otp"
        textContentType="oneTimeCode"
        maxLength={6}
        autoFocus={autoFocus}
        caretHidden
        accessibilityLabel="OTP"
        style={{ position: "absolute", top: 0, left: 0, right: 0, bottom: 0, opacity: 0 }}
      />
    </Pressable>
  );
}

function StaffSignIn({ onResident }: { onResident: () => void }) {
  const t = useT();
  const p = usePalette();
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

  const residentToggle = (
    <Pressable accessibilityRole="button" onPress={onResident} style={{ alignSelf: "center", flexDirection: "row", alignItems: "center", gap: space[2], borderWidth: 1.5, borderColor: p.borderSoft, backgroundColor: p.surfaceRaised, borderRadius: radius.pill, paddingHorizontal: space[4], minHeight: TOUCH }}>
      <Txt color={p.success}>📱</Txt>
      <Txt bold>{t("login.residentSignIn")}</Txt>
    </Pressable>
  );

  return (
    <AuthLayout hero={<LogoHero />} footer={secret === undefined ? <View style={{ alignItems: "center" }}>{residentToggle}</View> : undefined}>
      {secret === undefined ? (
        <>
          <Pill text={t("login.staffAdmin")} />
          <Txt size="xxl" bold>{t("login.title")}</Txt>
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
    </AuthLayout>
  );
}
