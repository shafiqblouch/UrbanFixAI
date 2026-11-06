import { tokens } from "@urbanfix/tokens";
import { useColorScheme } from "react-native";

export type Palette = (typeof tokens.themes)["light"] | (typeof tokens.themes)["dark"];

/** The design tokens (DS-01) for the phone's current light or dark setting. */
export function usePalette(): Palette {
  return useColorScheme() === "dark" ? tokens.themes.dark : tokens.themes.light;
}

export const space = tokens.space;
export const radius = tokens.radius;
/** Every tap target is at least this tall and wide (48 dp), so gloved or hurried hands hit them. */
export const TOUCH = tokens.touchTarget;
export const fontSize = tokens.font.size;
/** Urdu needs a larger size and taller lines than Latin to stay legible on small screens. */
export const URDU_SCALE = tokens.font.urduScale;
export const URDU_LINE_HEIGHT = tokens.font.urduLineHeight;
export const gradients = tokens.gradients;

/** Mobile brand gradient: teal → blue. Used on headers and primary action pills for a bright, modern feel. */
export const brandGradient = ["#12B6A6", "#1C7CD6"] as const;

/** Vivid tones for the complaint-progress timeline — a filled dot per step, never colour alone. */
export const timelineColors = {
  done: { ring: "#1FB573", icon: "#FFFFFF", line: "#1FB573" }, // completed step: green check
  active: { ring: "#F7901E", icon: "#FFFFFF", line: "#CBD5D0" }, // current step: orange clock
  danger: { ring: "#E0493B", icon: "#FFFFFF", line: "#CBD5D0" }, // rejected step: red cross
} as const;

export const URDU_FONT = "NotoNaskhArabic_400Regular";
export const URDU_FONT_BOLD = "NotoNaskhArabic_700Bold";
