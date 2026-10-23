import * as ImagePicker from "expo-image-picker";
import { ImageManipulator, SaveFormat } from "expo-image-manipulator";
import { compressToTarget, MAX_PHOTO_BYTES } from "../upload/compress";
import { fileSize } from "./files";

export interface Photo {
  uri: string;
  width: number;
  height: number;
  size: number;
  mimeType: "image/jpeg";
  /** False when even the smallest step stayed over the limit; the caller should warn. */
  fits: boolean;
}

export type CaptureResult = { photo: Photo } | { error: "permission_denied" | "cancelled" };

/** Compresses to at most 500 KB. The original is not kept, which is what keeps uploads small on 3G. */
export async function compressPhoto(uri: string, width: number, height: number): Promise<Photo> {
  const longest = Math.max(width, height);
  const result = await compressToTarget(async ({ maxSide, quality }) => {
    const context = ImageManipulator.manipulate(uri);
    // Never enlarge: a photo already smaller than the step keeps its size and only the quality drops.
    if (longest > maxSide) context.resize(width >= height ? { width: maxSide } : { height: maxSide });
    const image = await context.renderAsync();
    try {
      const saved = await image.saveAsync({ compress: quality, format: SaveFormat.JPEG });
      return { uri: saved.uri, size: fileSize(saved.uri), width: saved.width, height: saved.height };
    } finally {
      image.release();
      context.release();
    }
  }, MAX_PHOTO_BYTES);
  return { uri: result.uri, width: result.width ?? width, height: result.height ?? height, size: result.size, mimeType: "image/jpeg", fits: result.fits };
}

/** Opens the camera, asks for permission the first time, and returns a compressed photo. */
export async function takePhoto(): Promise<CaptureResult> {
  const permission = await ImagePicker.requestCameraPermissionsAsync();
  if (!permission.granted) return { error: "permission_denied" };
  const shot = await ImagePicker.launchCameraAsync({ mediaTypes: ["images"], quality: 1, exif: false });
  if (shot.canceled || shot.assets.length === 0) return { error: "cancelled" };
  const asset = shot.assets[0];
  return { photo: await compressPhoto(asset.uri, asset.width, asset.height) };
}
