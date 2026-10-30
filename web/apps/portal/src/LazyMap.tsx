import { Suspense, lazy, type ComponentProps } from "react";

// Leaflet is a large library only the map screens need, so it loads on first use instead of with every page.
const Loaded = lazy(() => import("./MapView").then((m) => ({ default: m.MapView })));

export function MapView(props: ComponentProps<typeof Loaded>) {
  return (
    <Suspense fallback={<div className="map-view skeleton" role="status" style={{ blockSize: props.height ?? 420 }} />}>
      <Loaded {...props} />
    </Suspense>
  );
}
