// Native versions of the shared components (DS-02): same tokens, same wording, mirrored for right-to-left in JavaScript.
import { useState, type ReactNode } from "react";
import {
  ActivityIndicator, Pressable, ScrollView, StyleSheet, Text as RNText, TextInput, View, type StyleProp, type TextInputProps, type TextStyle, type ViewStyle,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { useI18n } from "../i18n";
import { fontSize, radius, space, TOUCH, URDU_FONT, URDU_FONT_BOLD, URDU_LINE_HEIGHT, URDU_SCALE, usePalette } from "../theme";

type Size = keyof typeof fontSize;

export function Txt({ children, size = "md", bold, muted, color, style, numberOfLines, selectable }: {
  children: ReactNode; size?: Size; bold?: boolean; muted?: boolean; color?: string; style?: StyleProp<TextStyle>; numberOfLines?: number; selectable?: boolean;
}) {
  const { rtl, locale } = useI18n();
  const p = usePalette();
  const base = fontSize[size];
  const urdu = locale === "ur";
  return (
    <RNText
      numberOfLines={numberOfLines}
      selectable={selectable}
      style={[
        { color: color ?? (muted ? p.textMuted : p.text), fontSize: urdu ? base * URDU_SCALE : base, textAlign: rtl ? "right" : "left", writingDirection: rtl ? "rtl" : "ltr" },
        urdu ? { fontFamily: bold ? URDU_FONT_BOLD : URDU_FONT, lineHeight: base * URDU_SCALE * URDU_LINE_HEIGHT / 1.15 } : { fontWeight: bold ? "700" : "400" },
        style,
      ]}
    >
      {children}
    </RNText>
  );
}

/** A horizontal row that reverses for right-to-left languages. */
export function Row({ children, gap = space[3], style, wrap }: { children: ReactNode; gap?: number; style?: StyleProp<ViewStyle>; wrap?: boolean }) {
  const { rtl } = useI18n();
  return <View style={[{ flexDirection: rtl ? "row-reverse" : "row", alignItems: "center", gap, flexWrap: wrap ? "wrap" : "nowrap" }, style]}>{children}</View>;
}

export function Screen({ children, scroll = true, footer }: { children: ReactNode; scroll?: boolean; footer?: ReactNode }) {
  const p = usePalette();
  const body = <View style={{ padding: space[4], gap: space[4] }}>{children}</View>;
  return (
    <SafeAreaView style={{ flex: 1, backgroundColor: p.bg }} edges={["left", "right", "bottom"]}>
      {scroll ? <ScrollView keyboardShouldPersistTaps="handled" contentContainerStyle={{ flexGrow: 1 }}>{body}</ScrollView> : <View style={{ flex: 1 }}>{body}</View>}
      {footer}
    </SafeAreaView>
  );
}

export function Button({ title, onPress, variant = "primary", loading, disabled, accessibilityLabel }: {
  title: string; onPress: () => void; variant?: "primary" | "secondary" | "danger" | "ghost"; loading?: boolean; disabled?: boolean; accessibilityLabel?: string;
}) {
  const p = usePalette();
  const colors = {
    primary: { bg: p.primary, fg: p.onPrimary, border: p.primary },
    secondary: { bg: p.primarySoft, fg: p.onPrimarySoft, border: p.primarySoft },
    danger: { bg: p.dangerBg, fg: p.danger, border: p.danger },
    ghost: { bg: "transparent", fg: p.link, border: "transparent" },
  }[variant];
  const off = disabled || loading;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? title}
      accessibilityState={{ disabled: !!off, busy: !!loading }}
      disabled={off}
      onPress={onPress}
      style={({ pressed }) => [styles.button, { backgroundColor: colors.bg, borderColor: colors.border, opacity: off ? 0.6 : pressed ? 0.85 : 1 }]}
    >
      {loading ? <ActivityIndicator color={colors.fg} /> : <Txt bold color={colors.fg} style={{ textAlign: "center" }}>{title}</Txt>}
    </Pressable>
  );
}

export function Card({ children, onPress, style }: { children: ReactNode; onPress?: () => void; style?: StyleProp<ViewStyle> }) {
  const p = usePalette();
  const body = <View style={[{ backgroundColor: p.surfaceRaised, borderColor: p.border, borderWidth: 1, borderRadius: radius.lg, padding: space[4], gap: space[2] }, style]}>{children}</View>;
  return onPress ? <Pressable accessibilityRole="button" onPress={onPress} style={{ minHeight: TOUCH }}>{body}</Pressable> : body;
}

export function Field({ label, hint, error, ...input }: TextInputProps & { label: string; hint?: string; error?: string }) {
  const p = usePalette();
  const { rtl } = useI18n();
  const [focused, setFocused] = useState(false);
  return (
    <View style={{ gap: space[1] }}>
      <Txt bold>{label}</Txt>
      <TextInput
        {...input}
        accessibilityLabel={label}
        onFocus={(e) => { setFocused(true); input.onFocus?.(e); }}
        onBlur={(e) => { setFocused(false); input.onBlur?.(e); }}
        placeholderTextColor={p.textMuted}
        style={{
          minHeight: TOUCH, borderWidth: focused ? 3 : 2, borderColor: error ? p.danger : focused ? p.focusRing : p.border, borderRadius: radius.md,
          paddingHorizontal: space[3], paddingVertical: space[2], color: p.text, backgroundColor: p.surfaceRaised, fontSize: fontSize.md,
          textAlign: rtl ? "right" : "left", writingDirection: rtl ? "rtl" : "ltr", textAlignVertical: input.multiline ? "top" : "center",
        }}
      />
      {hint && <Txt size="sm" muted>{hint}</Txt>}
      {error && <Txt size="sm" color={p.danger} bold>{error}</Txt>}
    </View>
  );
}

/** Status with an icon and words, never colour alone (DS-02). */
export function Chip({ label, tone, glyph }: { label: string; tone: "success" | "warning" | "danger" | "info" | "neutral"; glyph?: string }) {
  const p = usePalette();
  const colors = { success: [p.successBg, p.success], warning: [p.warningBg, p.warning], danger: [p.dangerBg, p.danger], info: [p.infoBg, p.info], neutral: [p.neutralBg, p.neutral] }[tone];
  return (
    <View style={{ backgroundColor: colors[0], borderRadius: radius.pill, paddingHorizontal: space[3], paddingVertical: space[1], alignSelf: "flex-start" }}>
      <Row gap={space[1]}>
        {glyph && <RNText accessibilityElementsHidden importantForAccessibility="no" style={{ color: colors[1], fontSize: fontSize.sm }}>{glyph}</RNText>}
        <Txt size="sm" bold color={colors[1]}>{label}</Txt>
      </Row>
    </View>
  );
}

export function Banner({ text, tone = "warning" }: { text: string; tone?: "warning" | "info" | "danger" | "success" }) {
  const p = usePalette();
  const c = { warning: [p.warningBg, p.warning], info: [p.infoBg, p.info], danger: [p.dangerBg, p.danger], success: [p.successBg, p.success] }[tone];
  return (
    <View accessibilityRole="alert" style={{ backgroundColor: c[0], padding: space[3], borderRadius: radius.md }}>
      <Txt bold color={c[1]}>{text}</Txt>
    </View>
  );
}

export function Empty({ text }: { text: string }) {
  return <View style={{ padding: space[6], alignItems: "center" }}><Txt muted style={{ textAlign: "center" }}>{text}</Txt></View>;
}

export function Loading() {
  const p = usePalette();
  return <View style={{ padding: space[6] }}><ActivityIndicator color={p.primary} /></View>;
}

/** A choice among a few options, as large tappable rows. */
export function Choice<V extends string>({ label, value, options, onChange }: { label: string; value: V | undefined; options: { value: V; label: string }[]; onChange: (v: V) => void }) {
  const p = usePalette();
  return (
    <View style={{ gap: space[1] }} accessibilityRole="radiogroup">
      <Txt bold>{label}</Txt>
      {options.map((o) => {
        const selected = o.value === value;
        return (
          <Pressable
            key={o.value}
            accessibilityRole="radio"
            accessibilityState={{ selected }}
            onPress={() => onChange(o.value)}
            style={{ minHeight: TOUCH, justifyContent: "center", paddingHorizontal: space[3], borderRadius: radius.md, borderWidth: 2, borderColor: selected ? p.primary : p.border, backgroundColor: selected ? p.primarySoft : p.surfaceRaised }}
          >
            <Row><RNText style={{ color: selected ? p.primary : p.textMuted, fontSize: fontSize.lg }}>{selected ? "●" : "○"}</RNText><Txt bold={selected} style={{ flex: 1 }}>{o.label}</Txt></Row>
          </Pressable>
        );
      })}
    </View>
  );
}

export function Badge({ count }: { count: number }) {
  const p = usePalette();
  if (count <= 0) return null;
  return (
    <View style={{ minWidth: 24, height: 24, borderRadius: 12, paddingHorizontal: 6, backgroundColor: p.accent, alignItems: "center", justifyContent: "center" }}>
      <RNText style={{ color: p.onAccent, fontSize: fontSize.xs, fontWeight: "700" }}>{count > 99 ? "99+" : count}</RNText>
    </View>
  );
}

const styles = StyleSheet.create({
  button: { minHeight: TOUCH, minWidth: TOUCH, borderRadius: radius.md, borderWidth: 2, paddingHorizontal: space[4], justifyContent: "center", alignItems: "center" },
});
