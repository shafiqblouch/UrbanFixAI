import L from "leaflet";
import "leaflet/dist/leaflet.css";
import { useEffect, useRef } from "react";

export type MapTone = "info" | "warn" | "good" | "bad" | "neutral" | "tech" | "step";

export type MapMarker = {
  id: string;
  lat: number;
  lon: number;
  tone: MapTone;
  /** Bold first line of the popup. */
  title: string;
  /** Plain text lines under the title. */
  lines?: string[];
  /** Portal route the popup's link opens. */
  to?: string;
  /** Digits shown inside the dot (the step number on a route). Must be plain digits: it goes into markup. */
  badge?: string;
};

type Props = {
  markers: MapMarker[];
  /** A route to draw through these points, in order. */
  route?: [number, number][];
  height?: number;
  ariaLabel: string;
  openLabel: string;
  onOpen: (to: string) => void;
};

/** OpenStreetMap tiles need their credit shown wherever they appear. */
const ATTRIBUTION = '© <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noreferrer">OpenStreetMap</a>';

/**
 * A Leaflet map with one dot per marker (WP map view). Popups are built from DOM nodes with textContent,
 * so a complaint's text can never inject markup.
 */
export function MapView({ markers, route, height = 420, ariaLabel, openLabel, onOpen }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const map = useRef<L.Map>();
  const layer = useRef<L.LayerGroup>();
  const fitted = useRef(false);
  // Read through refs inside the popup handlers, so a new callback never rebuilds the map.
  const open = useRef(onOpen);
  open.current = onOpen;

  useEffect(() => {
    if (!host.current) return;
    const m = L.map(host.current, { scrollWheelZoom: false }).setView([30.3753, 69.3451], 5);
    L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", { maxZoom: 19, attribution: ATTRIBUTION }).addTo(m);
    layer.current = L.layerGroup().addTo(m);
    map.current = m;
    return () => { m.remove(); map.current = undefined; layer.current = undefined; fitted.current = false; };
  }, []);

  useEffect(() => {
    const m = map.current;
    const group = layer.current;
    if (!m || !group) return;
    group.clearLayers();
    const bounds: L.LatLngTuple[] = [];

    if (route && route.length > 1) L.polyline(route, { color: "#2563eb", weight: 3, opacity: 0.6, dashArray: "6 6" }).addTo(group);

    for (const p of markers) {
      const icon = L.divIcon({
        className: "",
        html: `<span class="map-pin map-pin--${p.tone}">${p.badge ?? ""}</span>`,
        iconSize: [22, 22],
        iconAnchor: [11, 11],
        popupAnchor: [0, -10],
      });
      const marker = L.marker([p.lat, p.lon], { icon, title: p.title, keyboard: true });

      const box = document.createElement("div");
      const head = document.createElement("strong");
      head.textContent = p.title;
      box.appendChild(head);
      for (const line of p.lines ?? []) {
        const row = document.createElement("div");
        row.textContent = line;
        box.appendChild(row);
      }
      if (p.to) {
        const to = p.to;
        const link = document.createElement("a");
        link.href = to;
        link.textContent = openLabel;
        link.addEventListener("click", (e) => { e.preventDefault(); open.current(to); });
        box.appendChild(link);
      }
      marker.bindPopup(box).addTo(group);
      bounds.push([p.lat, p.lon]);
    }

    // Fit once to what is shown first; later refreshes keep wherever the person has panned to.
    if (!fitted.current && bounds.length > 0) {
      fitted.current = true;
      if (bounds.length === 1) m.setView(bounds[0]!, 16);
      else m.fitBounds(bounds, { padding: [30, 30], maxZoom: 17 });
    }
  }, [markers, route, openLabel]);

  return <div ref={host} className="map-view" style={{ blockSize: height }} role="region" aria-label={ariaLabel} />;
}
