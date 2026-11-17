import { AudioQuality, IOSOutputFormat, type RecordingOptions, requestRecordingPermissionsAsync, setAudioModeAsync, useAudioRecorder, useAudioRecorderState } from "expo-audio";
import { useCallback, useRef, useState } from "react";

export interface VoiceNote {
  uri: string;
  size: number;
  /** Length of this note in whole seconds, captured when recording stops. */
  seconds: number;
  /** The recorder writes AAC in an MP4 container, which the server accepts as audio/mp4. */
  mimeType: "audio/mp4";
}

/**
 * Speech-tuned AAC/MP4, mono. This replaces `RecordingPresets.LOW_QUALITY`, whose Android branch
 * pairs an AMR-NB encoder with 44.1 kHz stereo — settings AMR-NB does not support, so `MediaRecorder`
 * fails and leaves a 0-byte file. AAC accepts these values on both platforms, and a minute at 64 kbps
 * is well under the upload limit. The `.m4a` output really is audio/mp4, matching VoiceNote.mimeType.
 */
const SPEECH_PRESET: RecordingOptions = {
  extension: ".m4a",
  sampleRate: 22050,
  numberOfChannels: 1,
  bitRate: 64000,
  android: { outputFormat: "mpeg4", audioEncoder: "aac" },
  ios: {
    outputFormat: IOSOutputFormat.MPEG4AAC,
    audioQuality: AudioQuality.LOW,
    linearPCMBitDepth: 16,
    linearPCMIsBigEndian: false,
    linearPCMIsFloat: false,
  },
  web: { mimeType: "audio/webm", bitsPerSecond: 64000 },
};

/** Records a voice note (RA-02). */
export function useVoiceRecorder() {
  const recorder = useAudioRecorder(SPEECH_PRESET);
  const state = useAudioRecorderState(recorder);
  const [note, setNote] = useState<VoiceNote>();
  const [denied, setDenied] = useState(false);
  // Both the live state and the finalized status can report `durationMillis` as 0 on Android once
  // recording stops, so we also time the recording from the wall clock as a reliable fallback.
  const startedAt = useRef(0);

  const start = useCallback(async () => {
    const permission = await requestRecordingPermissionsAsync();
    if (!permission.granted) { setDenied(true); return; }
    setDenied(false);
    await setAudioModeAsync({ allowsRecording: true, playsInSilentMode: true });
    await recorder.prepareToRecordAsync();
    recorder.record();
    startedAt.current = Date.now();
  }, [recorder]);

  const stop = useCallback(async (): Promise<VoiceNote | undefined> => {
    const elapsed = startedAt.current ? Math.round((Date.now() - startedAt.current) / 1000) : 0;
    await recorder.stop();
    await setAudioModeAsync({ allowsRecording: false });
    // Read the finalized file from the recorder's status, not `recorder.uri`: a failed stop still
    // reports a uri but leaves an empty file, and queuing that would silently fail on upload.
    const status = recorder.getStatus();
    // Prefer the recorder's reported length, but fall back to the measured elapsed time: on Android
    // the finalized status often reports 0 ms, which showed saved notes as "0s".
    const reported = Math.round((status.durationMillis ?? 0) / 1000);
    if (!status.url || status.fileSize <= 0) return undefined;
    const finished: VoiceNote = { uri: status.url, size: status.fileSize, seconds: reported || elapsed, mimeType: "audio/mp4" };
    setNote(finished); // also returned so a caller stopping mid-recording (e.g. on Send) keeps it
    return finished;
  }, [recorder]);

  return { recording: state.isRecording, seconds: Math.round((state.durationMillis ?? 0) / 1000), note, denied, start, stop, discard: () => setNote(undefined) };
}
