import { File, FileMode } from "expo-file-system";
import type { FileSource } from "../upload/resumable";

/** Opens a local file for chunked reading, so a photo is read 256 KB at a time instead of loaded whole. */
export function openFile(uri: string, mimeType: string): FileSource {
  const file = new File(uri);
  return {
    uri,
    mimeType,
    size: file.size,
    async read(offset, length) {
      const handle = file.open(FileMode.ReadOnly);
      try {
        handle.offset = offset;
        return await handle.readBytes(length);
      } finally {
        handle.close();
      }
    },
  };
}

export const fileSize = (uri: string): number => new File(uri).size;

/** Whether a local file is still on disk. Used to drop draft attachments the OS has purged from cache. */
export const fileExists = (uri: string): boolean => {
  try {
    return new File(uri).exists;
  } catch {
    return false; // an unreadable or malformed uri counts as gone
  }
};
