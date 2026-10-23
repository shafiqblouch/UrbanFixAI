// On-device photo compression to at most 500 KB (RA-02). The first attempt that fits wins, so good light photos keep their quality.

export const MAX_PHOTO_BYTES = 500 * 1024;

export interface Rendered {
  uri: string;
  size: number;
  width?: number;
  height?: number;
}

/** Longest side in pixels and JPEG quality, from best to smallest. */
export const LADDER: readonly { maxSide: number; quality: number }[] = [
  { maxSide: 1600, quality: 0.8 },
  { maxSide: 1280, quality: 0.7 },
  { maxSide: 1024, quality: 0.6 },
  { maxSide: 800, quality: 0.5 },
  { maxSide: 640, quality: 0.4 },
  { maxSide: 480, quality: 0.3 },
];

/**
 * Renders the photo at each step until it fits. `render` does the native work. If even the smallest step is too big the
 * smallest result is returned and `fits` is false, so the caller can warn instead of silently sending a huge file.
 */
export async function compressToTarget(
  render: (step: { maxSide: number; quality: number }) => Promise<Rendered>,
  maxBytes = MAX_PHOTO_BYTES,
  ladder = LADDER,
): Promise<Rendered & { fits: boolean; steps: number }> {
  let last: Rendered | undefined;
  for (let i = 0; i < ladder.length; i++) {
    last = await render(ladder[i]);
    if (last.size <= maxBytes) return { ...last, fits: true, steps: i + 1 };
  }
  return { ...last!, fits: false, steps: ladder.length };
}
