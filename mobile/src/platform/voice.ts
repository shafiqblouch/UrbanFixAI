import { RecordingPresets, requestRecordingPermissionsAsync, setAudioModeAsync, useAudioRecorder, useAudioRecorderState } from "expo-audio";
import { useCallback, useState } from "react";
import { fileSize } from "./files";

export interface VoiceNote {
  uri: string;
  size: number;
  /** The recorder writes AAC in an MP4 container, which the server accepts as audio/mp4. */
  mimeType: "audio/mp4";
}

/** Records a voice note (RA-02). Low-quality preset: speech only, so a minute stays well under the upload limit. */
export function useVoiceRecorder() {
  const recorder = useAudioRecorder(RecordingPresets.LOW_QUALITY);
  const state = useAudioRecorderState(recorder);
  const [note, setNote] = useState<VoiceNote>();
  const [denied, setDenied] = useState(false);

  const start = useCallback(async () => {
    const permission = await requestRecordingPermissionsAsync();
    if (!permission.granted) { setDenied(true); return; }
    setDenied(false);
    await setAudioModeAsync({ allowsRecording: true, playsInSilentMode: true });
    await recorder.prepareToRecordAsync();
    recorder.record();
  }, [recorder]);

  const stop = useCallback(async () => {
    await recorder.stop();
    await setAudioModeAsync({ allowsRecording: false });
    if (recorder.uri) setNote({ uri: recorder.uri, size: fileSize(recorder.uri), mimeType: "audio/mp4" });
  }, [recorder]);

  return { recording: state.isRecording, seconds: Math.round((state.durationMillis ?? 0) / 1000), note, denied, start, stop, discard: () => setNote(undefined) };
}
