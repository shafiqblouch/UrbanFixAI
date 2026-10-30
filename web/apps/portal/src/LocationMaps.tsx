import { Card } from "@urbanfix/ui";
import { useMemo } from "react";
import { useNavigate } from "react-router-dom";
import { type S } from "./api";
import { usePeople } from "./components";
import { useFormat, useT, type Key } from "./i18n";
import { MapView } from "./LazyMap";
import type { MapMarker } from "./MapView";

/** Where a complaint was reported from, on a small map (shown only when the report came with a position). */
export function ComplaintLocationCard({ complaint }: { complaint: S["ComplaintDto"] }) {
  const t = useT();
  const navigate = useNavigate();
  const markers = useMemo<MapMarker[]>(() => complaint.latitude == null || complaint.longitude == null ? [] : [{
    id: complaint.id, lat: complaint.latitude, lon: complaint.longitude, tone: "info", title: complaint.reference,
    lines: complaint.locationText ? [complaint.locationText] : [],
  }], [complaint]);

  return (
    <Card title={t("map.complaintPlace")}>
      {markers.length === 0
        ? <p className="muted">{t("map.noPosition")}</p>
        : <MapView markers={markers} height={260} ariaLabel={t("map.aria")} openLabel={t("map.open")} onOpen={navigate} />}
    </Card>
  );
}

/**
 * The complaint's pin and the places the technician sent a position from, numbered in the order they were sent
 * and joined by a line (start, notes with location, finishing the work).
 */
export function JobMapCard({ job, complaint }: { job: S["JobDetailDto"]; complaint: S["ComplaintDto"] | undefined }) {
  const t = useT();
  const f = useFormat();
  const navigate = useNavigate();
  const { nameOf } = usePeople();

  const { markers, route } = useMemo(() => {
    const out: MapMarker[] = [];
    if (complaint?.latitude != null && complaint.longitude != null) {
      out.push({ id: "complaint", lat: complaint.latitude, lon: complaint.longitude, tone: "info", title: t("map.complaintPin"), lines: [complaint.reference, ...(complaint.locationText ? [complaint.locationText] : [])] });
    }
    const steps = job.timeline.filter((u) => u.latitude != null && u.longitude != null);
    steps.forEach((u, i) => out.push({
      id: u.id, lat: u.latitude!, lon: u.longitude!, tone: "step", badge: String(i + 1),
      title: t("map.step", { n: i + 1, what: t(`update.${u.kind}` as Key) + (u.toStatus ? ` → ${t(`jobStatus.${u.toStatus}` as Key)}` : "") }),
      lines: [`${f.dateTime(u.createdAt)} · ${nameOf(u.authorId)}`],
    }));
    for (const e of job.evidence) {
      if (e.latitude != null && e.longitude != null) out.push({
        id: e.id, lat: e.latitude, lon: e.longitude, tone: "neutral", title: t("map.photo", { stage: t(`stage.${e.stage}` as Key) }), lines: [f.dateTime(e.createdAt)],
      });
    }
    return { markers: out, route: steps.map((u) => [u.latitude!, u.longitude!] as [number, number]) };
  }, [job, complaint, t, f, nameOf]);

  return (
    <Card title={t("map.jobTitle")}>
      <div className="stack">
        {markers.length === 0
          ? <p className="muted">{t("map.jobNone")}</p>
          : <>
              <MapView markers={markers} route={route} height={320} ariaLabel={t("map.aria")} openLabel={t("map.open")} onOpen={navigate} />
              <p className="muted">{t("map.jobHint")}</p>
            </>}
      </div>
    </Card>
  );
}
