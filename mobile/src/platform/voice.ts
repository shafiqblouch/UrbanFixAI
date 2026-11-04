import { AudioQuality, IOSOutputFormat, type RecordingOptions, requestRecordingPermissionsAsync, setAudioModeAsync, useAudioRecorder, useAudioRecorderState } from "expo-audio";
import { useCallback, useState } from "react";

export interface VoiceNote {
  uri: string;
  size: number;
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
    // Read the finalized file from the recorder's status, not `recorder.uri`: a failed stop still
    // reports a uri but leaves an empty file, and queuing that would silently fail on upload.
    const status = recorder.getStatus();
    if (status.url && status.fileSize > 0) setNote({ uri: status.url, size: status.fileSize, mimeType: "audio/mp4" });
  }, [recorder]);

  return { recording: state.isRecording, seconds: Math.round((state.durationMillis ?? 0) / 1000), note, denied, start, stop, discard: () => setNote(undefined) };
}
