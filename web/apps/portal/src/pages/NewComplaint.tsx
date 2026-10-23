import { Alert, Button, Card, SelectField, TextAreaField, TextField } from "@urbanfix/ui";
import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { api, ok, upload, type S } from "../api";
import { ErrorNote, PageHeader, usePeople } from "../components";
import { useAction, useLoad } from "../hooks";
import { useT } from "../i18n";
import { useLookups } from "../lookups";
import { useSession } from "../session";

/** Entry form for walk-in and phone complaints, and for residents using the web (WP-05). */
export function NewComplaintPage() {
  const t = useT();
  const { role } = useSession();
  const { nameOf } = usePeople();
  const lookups = useLookups();
  const staff = role !== "Resident";
  const action = useAction();

  const [channel, setChannel] = useState("phone");
  const [houseFilter, setHouseFilter] = useState("");
  const [houseId, setHouseId] = useState("");
  const [locationId, setLocationId] = useState("");
  const [reporterId, setReporterId] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [description, setDescription] = useState("");
  const [locationText, setLocationText] = useState("");
  const [callerPhone, setCallerPhone] = useState("");
  const [assetRef, setAssetRef] = useState("");
  const [files, setFiles] = useState<File[]>([]);
  const [created, setCreated] = useState<S["CreatedDto"]>();
  const [skipped, setSkipped] = useState(0);

  const houses = useLoad(() => (staff ? ok(api.GET("/houses", {})) : Promise.resolve([])), [staff]);
  const residents = useLoad(
    () => (staff && houseId ? ok(api.GET("/houses/{houseId}/residents", { params: { path: { houseId } } })) : Promise.resolve([])),
    [staff, houseId]);

  const names = useMemo(() => new Map(lookups.locations.map((l) => [l.id, l.name])), [lookups.locations]);
  const houseLabel = (h: S["HouseDto"]) => h.locationPath.split("/").filter(Boolean).map((id) => names.get(id) ?? "").filter(Boolean).join(" › ");
  const houseOptions = (houses.data ?? [])
    .map((h) => ({ value: h.id, label: houseLabel(h) }))
    .filter((o) => o.label.toLowerCase().includes(houseFilter.trim().toLowerCase()))
    .slice(0, 50);
  const leafLocations = lookups.locations.filter((l) => l.kind !== "sector");

  async function submit() {
    const result = await action.run(async () => {
      const made = await ok(api.POST("/complaints", { body: {
        description, channel: staff ? channel : null, categoryId: categoryId || null, houseId: houseId || null,
        locationId: houseId ? null : locationId || null, locationText: locationText || null, reportedByUserId: staff ? reporterId || null : null,
        // A caller who is not picked from the house is matched by number; unknown numbers follow the society's policy (CP-08).
        reporterPhone: staff && !reporterId ? callerPhone.trim() || null : null, assetRef: assetRef.trim() || null,
      } }));
      // The complaint exists now; a photo that fails to upload must not lose it.
      let failed = 0;
      for (const file of files) { try { await upload(`/complaints/${made.id}/media`, file); } catch { failed++; } }
      setSkipped(failed);
      return made;
    });
    if (result) setCreated(result);
  }

  function reset() {
    setCreated(undefined); setDescription(""); setLocationText(""); setFiles([]); setHouseId(""); setLocationId(""); setReporterId(""); setCategoryId(""); setSkipped(0); setCallerPhone(""); setAssetRef("");
  }

  if (created) {
    return (
      <>
        <PageHeader title={t("menu.new")} />
        <Card>
          <div className="stack">
            <Alert tone="success">{t("new.created", { reference: created.reference })}</Alert>
            {created.needsLocation && <Alert tone="warning">{t("new.needsLocation")}</Alert>}
            {created.status === "OnHold" && !created.needsLocation && <Alert tone="warning">{t("new.heldUnverified")}</Alert>}
            {skipped > 0 && <Alert tone="warning">{t("new.photosFailed", { count: skipped })}</Alert>}
            <div className="row">
              <Link to={`/complaints/${created.id}`}>{t("new.open")}</Link>
              <Button variant="secondary" onClick={reset}>{t("new.another")}</Button>
            </div>
          </div>
        </Card>
      </>
    );
  }

  return (
    <>
      <PageHeader title={t("menu.new")} />
      <Card>
        <form className="stack form" onSubmit={(e) => { e.preventDefault(); void submit(); }}>
          {staff && (
            <SelectField label={t("channel")} value={channel} onChange={(e) => setChannel(e.target.value)}
              options={[{ value: "phone", label: t("channel.phone") }, { value: "walk_in", label: t("channel.walk_in") }]} />
          )}
          <TextAreaField label={t("description")} value={description} onChange={(e) => setDescription(e.target.value)} required maxLength={4000} />
          <SelectField label={t("category")} placeholder={t("new.categoryLater")} value={categoryId} onChange={(e) => setCategoryId(e.target.value)}
            options={lookups.categories.filter((c) => c.isActive).map((c) => ({ value: c.id, label: lookups.categoryName(c.id) }))} />

          {staff && (
            <>
              <TextField label={t("new.findHouse")} value={houseFilter} onChange={(e) => setHouseFilter(e.target.value)} />
              <SelectField label={t("house")} placeholder={t("new.noHouse")} value={houseId} onChange={(e) => { setHouseId(e.target.value); setReporterId(""); }} options={houseOptions} />
              {!houseId && (
                <SelectField label={t("location")} placeholder={t("choose")} value={locationId} onChange={(e) => setLocationId(e.target.value)}
                  options={leafLocations.map((l) => ({ value: l.id, label: l.name }))} />
              )}
              {houseId && (
                <SelectField label={t("reporter")} placeholder={t("new.anonymous")} value={reporterId} onChange={(e) => setReporterId(e.target.value)}
                  options={(residents.data ?? []).filter((r) => !r.toDate).map((r) => ({ value: r.userId, label: `${nameOf(r.userId)} (${r.relation})` }))} />
              )}
            </>
          )}
          {staff && !reporterId && <TextField label={t("new.callerPhone")} hint={t("new.callerPhoneHint")} value={callerPhone} onChange={(e) => setCallerPhone(e.target.value)} inputMode="tel" />}
          <TextField label={t("new.locationText")} value={locationText} onChange={(e) => setLocationText(e.target.value)} />
          <TextField label={t("new.asset")} hint={t("new.assetHint")} value={assetRef} onChange={(e) => setAssetRef(e.target.value)} maxLength={100} />
          <label className="stack">
            <span className="uf-field__label">{t("new.photos")}</span>
            <input type="file" accept="image/jpeg,image/png,image/webp,audio/*" multiple onChange={(e) => setFiles(Array.from(e.target.files ?? []))} />
          </label>
          <ErrorNote error={action.error} />
          <Button type="submit" loading={action.busy}>{t("new.submit")}</Button>
        </form>
      </Card>
    </>
  );
}
