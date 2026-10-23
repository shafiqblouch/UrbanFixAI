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

export const URDU_FONT = "NotoNaskhArabic_400Regular";
export const URDU_FONT_BOLD = "NotoNaskhArabic_700Bold";
