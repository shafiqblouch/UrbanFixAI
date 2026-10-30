import { Button, SelectField } from "@urbanfix/ui";
import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api, ok, type S } from "../api";
import { Async, PageHeader } from "../components";
import { useLoad } from "../hooks";
import { useFormat, useT, type Key } from "../i18n";
import { MapView } from "../LazyMap";
import type { MapMarker, MapTone } from "../MapView";

const STATUSES = ["New", "OnHold", "UnderReview", "Assigned", "InProgress", "Resolved", "Closed", "Rejected", "Merged"] as const;
const PRIORITIES = ["Low", "Normal", "High", "Urgent"] as const;
const TONE: Record<S["ComplaintStatus"], MapTone> = {
  New: "info", OnHold: "neutral", UnderReview: "info", Assigned: "warn", InProgress: "warn", Resolved: "good", Closed: "good", Rejected: "bad", Merged: "neutral",
};
/** How often the map asks again, so technician positions stay current without a reload. */
const REFRESH_MS = 30_000;

/** Every complaint that came with a position, plus where technicians last reported from (map view). */
export function MapPage() {
  const t = useT();
  const f = useFormat();
  const navigate = useNavigate();
  const [status, setStatus] = useState("");
  const [priority, setPriority] = useState("");
  const [technicians, setTechnicians] = useState(true);

  const map = useLoad(() => ok(api.GET("/map", { params: { query: { status: (status || undefined) as never, priority: priority || undefined } } })), [status, priority]);
  useEffect(() => {
    const id = window.setInterval(map.reload, REFRESH_MS);
    return () => window.clearInterval(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, priority]);

  const markers = useMemo<MapMarker[]>(() => {
    const d = map.data;
    if (!d) return [];
    const complaints = d.complaints.map<MapMarker>((c) => ({
      id: `c-${c.id}`, lat: c.latitude, lon: c.longitude, tone: TONE[c.status],
      title: c.reference,
      lines: [`${t(`complaintStatus.${c.status}` as Key)} · ${t(`priority.${c.priority}` as Key)}`, c.description, ...(c.locationText ? [c.locationText] : [])],
      to: `/complaints/${c.id}`,
    }));
    const techs = technicians ? d.technicians.map<MapMarker>((x) => ({
      id: `t-${x.jobId}`, lat: x.latitude, lon: x.longitude, tone: "tech",
      title: `${x.technicianName ?? t("map.technician")} · ${x.jobNumber}`,
      lines: [x.reference, t("map.lastSeen", { time: f.dateTime(x.at) })],
      to: `/jobs/${x.jobId}`,
    })) : [];
    return [...complaints, ...techs];
  }, [map.data, technicians, t, f]);

  return (
    <>
      <PageHeader title={t("map.title")} />
      <div className="filters">
        <SelectField label={t("status")} value={status} onChange={(e) => setStatus(e.target.value)}
          options={[{ value: "", label: t("any") }, ...STATUSES.map((s) => ({ value: s, label: t(`complaintStatus.${s}` as Key) }))]} />
        <SelectField label={t("priority")} value={priority} onChange={(e) => setPriority(e.target.value)}
          options={[{ value: "", label: t("any") }, ...PRIORITIES.map((p) => ({ value: p, label: t(`priority.${p}` as Key) }))]} />
        <label className="check"><input type="checkbox" checked={technicians} onChange={(e) => setTechnicians(e.target.checked)} /> {t("map.showTechnicians")}</label>
        <Button variant="ghost" onClick={map.reload}>{t("map.refresh")}</Button>
      </div>

      <Async state={map}>
        {(d) => (
          <div className="stack">
            <ul className="map-legend" aria-label={t("map.legend")}>
              {([["info", "map.legend.open"], ["warn", "map.legend.working"], ["good", "map.legend.done"], ["bad", "map.legend.rejected"], ["tech", "map.legend.technician"]] as const)
                .map(([tone, key]) => <li key={tone}><span className={`map-pin map-pin--${tone}`} aria-hidden="true" /> {t(key)}</li>)}
            </ul>
            <MapView markers={markers} ariaLabel={t("map.aria")} openLabel={t("map.open")} onOpen={navigate} height={520} />
            <p className="muted" role="status">
              {t("map.count", { complaints: d.complaints.length, technicians: technicians ? d.technicians.length : 0 })}
              {d.complaintsWithoutPosition > 0 && <> · {t("map.unpinned", { count: d.complaintsWithoutPosition })} <Link to="/complaints">{t("map.viewList")}</Link></>}
            </p>
          </div>
        )}
      </Async>
    </>
  );
}
