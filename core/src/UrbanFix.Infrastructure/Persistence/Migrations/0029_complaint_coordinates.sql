-- Where the reporter was when they sent a complaint (app GPS fix or WhatsApp pin), so the portal can draw it on a map.
ALTER TABLE complaints ADD COLUMN latitude double precision;
ALTER TABLE complaints ADD COLUMN longitude double precision;
ALTER TABLE complaints ADD CONSTRAINT complaints_coordinates_valid CHECK (
    (latitude IS NULL AND longitude IS NULL)
    OR (latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180));
-- Complaints from before this column only have the numbers inside the free text ("(31.52000, 74.35000)" or "WhatsApp pin 31.52000, 74.35000").
UPDATE complaints c SET latitude = m[1]::double precision, longitude = m[2]::double precision
FROM (SELECT id, regexp_match(location_text, '(-?\d{1,2}\.\d{3,})\s*,\s*(-?\d{1,3}\.\d{3,})') AS m FROM complaints WHERE location_text IS NOT NULL) x
WHERE c.id = x.id AND x.m IS NOT NULL
  AND x.m[1]::double precision BETWEEN -90 AND 90 AND x.m[2]::double precision BETWEEN -180 AND 180;
