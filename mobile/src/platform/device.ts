import AsyncStorage from "@react-native-async-storage/async-storage";
import NetInfo from "@react-native-community/netinfo";
import * as Location from "expo-location";
import * as SecureStore from "expo-secure-store";
import { useEffect, useState } from "react";
import type { KeyValue } from "../offline/store";

/** Non-secret values (receipts, drafts, the upload queue). */
export const storage: KeyValue = {
  getItem: (key) => AsyncStorage.getItem(key),
  setItem: (key, value) => AsyncStorage.setItem(key, value),
  removeItem: (key) => AsyncStorage.removeItem(key),
};

/** The session token lives in the platform keystore, never in plain storage. */
export const secrets = {
  get: (key: string) => SecureStore.getItemAsync(key),
  set: (key: string, value: string) => SecureStore.setItemAsync(key, value),
  delete: (key: string) => SecureStore.deleteItemAsync(key),
};

/** Whether the phone currently has a usable connection. Optimistic until the first reading arrives. */
export function useOnline(): boolean {
  const [online, setOnline] = useState(true);
  useEffect(() => NetInfo.addEventListener((s) => setOnline(s.isConnected !== false && s.isInternetReachable !== false)), []);
  return online;
}

export const isOnline = async (): Promise<boolean> => {
  const s = await NetInfo.fetch();
  return s.isConnected !== false && s.isInternetReachable !== false;
};

/** Calls `onBack` each time the connection returns, so queued work can resume. */
export function onReconnect(onBack: () => void): () => void {
  let was = true;
  const stop = NetInfo.addEventListener((s) => {
    const now = s.isConnected !== false && s.isInternetReachable !== false;
    if (now && !was) onBack();
    was = now;
  });
  return stop;
}

export type Fix = { latitude: number; longitude: number } | { error: "permission_denied" | "unavailable" };

/** One position reading for "use my location" (balanced accuracy: fast and light on battery). */
export async function currentPosition(): Promise<Fix> {
  const permission = await Location.requestForegroundPermissionsAsync();
  if (!permission.granted) return { error: "permission_denied" };
  try {
    const p = await Location.getCurrentPositionAsync({ accuracy: Location.Accuracy.Balanced });
    return { latitude: p.coords.latitude, longitude: p.coords.longitude };
  } catch {
    return { error: "unavailable" };
  }
}
